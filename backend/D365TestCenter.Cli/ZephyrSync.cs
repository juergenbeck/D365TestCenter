using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Cli;

/// <summary>
/// E5 (ADR-0008) CLI wiring for the Zephyr Scale result upload (Decision 24).
/// The payload/mapping logic lives in the Core (<see cref="ZephyrResultBuilder"/>);
/// this class owns the IO the Core must not do: reading the run results from
/// Dataverse (reusing <see cref="RunResultLoader.LoadResultsFromRun"/>), walking the
/// Markdown definitions for the <c>zephyr_key</c> front-matter, and the two HTTP
/// POSTs against the Zephyr Scale Data Center ATM 1.0 API.
///
/// Model: one jbe_testrun -> one NEW Zephyr Test-Run (cycle), then the results are
/// bulk-uploaded into it. Auth is a Jira PAT as a Bearer header (the CLI stays
/// secret-agnostic; the PowerShell wrapper sources the PAT from TokenVault).
/// Tests without a <c>zephyr_key</c> are skipped and reported.
/// </summary>
public static class ZephyrSync
{
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public sealed class SyncSummary
    {
        /// <summary>Results found in the run.</summary>
        public int Total { get; set; }
        /// <summary>Results that matched a zephyr_key and were planned for upload.</summary>
        public int Mapped { get; set; }
        /// <summary>Results skipped because their definition carries no zephyr_key.</summary>
        public int SkippedNoKey { get; set; }
        /// <summary>Results actually uploaded.</summary>
        public int Uploaded { get; set; }
        /// <summary>Key of the created Zephyr Test-Run (cycle), e.g. DYN-R123.</summary>
        public string? RunKey { get; set; }
        public List<string> SkippedIds { get; } = new();
    }

    /// <summary>The matched upload plan: results paired with a zephyr_key, plus the skip list.</summary>
    public sealed class SyncPlan
    {
        public List<ZephyrResultBuilder.ResultInput> Inputs { get; } = new();
        public List<string> SkippedNoKey { get; } = new();
    }

    /// <summary>
    /// Pure plan builder: maps each result to its zephyr_key (by testId), turning it
    /// into a <see cref="ZephyrResultBuilder.ResultInput"/>. Results whose definition
    /// has no zephyr_key go to the skip list. The failure reason (jbe_errormessage)
    /// rides along as the result comment. When <paramref name="stepsByTestId"/> is
    /// supplied (opt-in --script-results, Decision 25), the per-step results for the
    /// matching testId are attached as <c>scriptResults[]</c>. Stays Dataverse-free:
    /// the step dictionary is loaded by the IO seam (<see cref="LoadStepResultsByTestId"/>).
    /// </summary>
    public static SyncPlan BuildPlan(
        IReadOnlyList<TestCaseResult> results, IReadOnlyDictionary<string, string> zephyrKeys,
        IReadOnlyDictionary<string, IReadOnlyList<ZephyrResultBuilder.ScriptResultInput>>? stepsByTestId = null)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        if (zephyrKeys == null) throw new ArgumentNullException(nameof(zephyrKeys));

        var plan = new SyncPlan();
        foreach (var r in results)
        {
            if (string.IsNullOrWhiteSpace(r.TestId)) continue;
            if (zephyrKeys.TryGetValue(r.TestId, out var zk) && !string.IsNullOrWhiteSpace(zk))
            {
                var input = new ZephyrResultBuilder.ResultInput
                {
                    ZephyrKey = zk,
                    Outcome = r.Outcome,
                    DurationMs = r.DurationMs,
                    // OE-10: Audit-Kommentar aus angelegten Records (mit Namen, CLI-run-Pfad)
                    // + Asserts; macht auch den PASS-Fall selbsterklärend (früher nur ErrorMessage).
                    Comment = ZephyrResultBuilder.BuildAuditComment(
                        r.TrackedRecords, r.StepResults, r.ErrorMessage)
                };
                if (stepsByTestId != null
                    && stepsByTestId.TryGetValue(r.TestId, out var steps) && steps.Count > 0)
                {
                    input.ScriptResults = steps;
                }
                plan.Inputs.Add(input);
            }
            else
            {
                plan.SkippedNoKey.Add(r.TestId);
            }
        }
        return plan;
    }

    /// <summary>
    /// E5 Phase 2 (Decision 25, opt-in): loads the jbe_teststep records of a run and
    /// groups them by the parent result's jbe_testid, mapped to
    /// <see cref="ZephyrResultBuilder.ScriptResultInput"/> in step-number order with a
    /// 0-based running index. One LinkEntity query (jbe_teststep -> jbe_testrunresult,
    /// filtered on jbe_testrunid). The step status is binary in Dataverse (Passed/Failed),
    /// the failure text (jbe_errormessage) becomes the per-step comment.
    ///
    /// Note: these are the D365TestCenter execution steps; they only line up with the
    /// Zephyr test-case's manual script steps when the case mirrors them - hence opt-in.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<ZephyrResultBuilder.ScriptResultInput>>
        LoadStepResultsByTestId(IOrganizationService service, ITestCenterConfig cfg, Guid runId)
    {
        var q = new QueryExpression(cfg.TestStepEntity)
        {
            ColumnSet = new ColumnSet("jbe_stepnumber", "jbe_stepstatus", "jbe_errormessage"),
            Orders = { new OrderExpression("jbe_stepnumber", OrderType.Ascending) }
        };
        var link = q.AddLink(
            cfg.TestRunResultEntity, "jbe_testrunresultid", cfg.TestRunResultEntity + "id");
        link.LinkCriteria.AddCondition("jbe_testrunid", ConditionOperator.Equal, runId);
        link.Columns = new ColumnSet("jbe_testid");
        link.EntityAlias = "res";

        // Collect raw per testId, then assign a contiguous 0-based index in step order.
        var raw = new Dictionary<string, List<(int num, TestOutcome outcome, string? comment)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var e in service.RetrieveMultiple(q).Entities)
        {
            var testId = e.GetAttributeValue<AliasedValue>("res.jbe_testid")?.Value as string;
            if (string.IsNullOrWhiteSpace(testId)) continue;
            var num = e.GetAttributeValue<int?>("jbe_stepnumber") ?? 0;
            var outcome = RunResultLoader.MapOutcome(
                e.GetAttributeValue<OptionSetValue>("jbe_stepstatus")?.Value, cfg);
            var err = e.GetAttributeValue<string>("jbe_errormessage");
            if (!raw.TryGetValue(testId!, out var list)) { list = new(); raw[testId!] = list; }
            list.Add((num, outcome, string.IsNullOrWhiteSpace(err) ? null : err));
        }

        var result = new Dictionary<string, IReadOnlyList<ZephyrResultBuilder.ScriptResultInput>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var kv in raw)
        {
            var ordered = kv.Value.OrderBy(x => x.num).ToList();
            var srs = new List<ZephyrResultBuilder.ScriptResultInput>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
                srs.Add(new ZephyrResultBuilder.ScriptResultInput
                {
                    Index = i,
                    Outcome = ordered[i].outcome,
                    Comment = ordered[i].comment
                });
            result[kv.Key] = srs;
        }
        return result;
    }

    /// <summary>
    /// Reads <c>id -> zephyr_key</c> from the front-matter of every *.md under
    /// <paramref name="defsDir"/> (first id wins). Definitions without a zephyr_key
    /// are simply absent from the map (the plan then skips their results).
    /// </summary>
    public static Dictionary<string, string> LoadZephyrKeys(string defsDir)
    {
        if (!Directory.Exists(defsDir))
            throw new DirectoryNotFoundException($"Definitions directory not found: {defsDir}");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.md", SearchOption.AllDirectories))
        {
            var content = MarkdownDocument.Normalize(File.ReadAllText(file));
            if (!MarkdownDocument.TrySplitFrontmatter(content, out var fm, out _)) continue;
            var id = MarkdownDocument.ReadScalar(fm, "id");
            var zk = MarkdownDocument.ReadScalar(fm, "zephyr_key");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(zk) && !map.ContainsKey(id!))
                map[id!] = zk!;
        }
        return map;
    }

    /// <summary>
    /// Full sync: load the run results, match them to zephyr keys, create a new
    /// Zephyr Test-Run (cycle) and bulk-upload the results into it. Writes to Zephyr,
    /// so it is approval-gated (Goldene Regel). Returns a summary; never partially
    /// silent (skips are logged).
    /// </summary>
    public static async Task<SyncSummary> SyncAsync(
        IOrganizationService service, ITestCenterConfig cfg, Guid runId, string defsDir,
        string serverUrl, string projectKey, string pat, string? env, string? cycleName,
        bool includeScriptResults = false, Action<string>? log = null)
    {
        var results = RunResultLoader.LoadResultsFromRun(service, cfg, runId);
        var summary = new SyncSummary { Total = results.Count };
        if (results.Count == 0)
        {
            log?.Invoke($"  Keine jbe_testrunresult-Records für Run {runId} gefunden.");
            return summary;
        }

        var keys = LoadZephyrKeys(defsDir);
        IReadOnlyDictionary<string, IReadOnlyList<ZephyrResultBuilder.ScriptResultInput>>? steps = null;
        if (includeScriptResults)
        {
            steps = LoadStepResultsByTestId(service, cfg, runId);
            log?.Invoke($"  scriptResults: an ({steps.Count} Testfälle mit Step-Daten geladen)");
        }
        var plan = BuildPlan(results, keys, steps);
        summary.Mapped = plan.Inputs.Count;
        summary.SkippedNoKey = plan.SkippedNoKey.Count;
        summary.SkippedIds.AddRange(plan.SkippedNoKey);
        foreach (var id in plan.SkippedNoKey)
            log?.Invoke($"  SKIP {id} (kein zephyr_key im Frontmatter)");

        if (plan.Inputs.Count == 0)
        {
            log?.Invoke("  Kein Test mit zephyr_key - nichts nach Zephyr hochzuladen.");
            return summary;
        }

        // Cycle name: explicit override, otherwise derived from env + run date + run id.
        // env is optional (Decision 26): drop it from the default name when absent
        // instead of leaving a double space.
        var header = ReportBuilder.LoadRunHeader(service, cfg, runId);
        var date = header?.StartedOn?.ToLocalTime() ?? DateTime.Now;
        var envPart = string.IsNullOrWhiteSpace(env) ? "" : env!.Trim() + " ";
        var name = !string.IsNullOrWhiteSpace(cycleName)
            ? cycleName!
            : $"D365TestCenter {envPart}{date:yyyy-MM-dd HH:mm} ({runId.ToString().Substring(0, 8)})";

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        var atm = serverUrl.TrimEnd('/') + "/rest/atm/1.0";

        // 1. Create the Zephyr Test-Run (cycle) with the distinct keys as items.
        var runPayload = ZephyrResultBuilder.BuildTestRunPayload(
            projectKey, name, plan.Inputs.Select(i => i.ZephyrKey));
        var itemCount = ((JArray)runPayload["items"]!).Count;
        log?.Invoke($"  POST {atm}/testrun  (Cycle \"{name}\", {itemCount} Testfälle)");
        var runResp = await PostJsonAsync(http, atm + "/testrun", runPayload.ToString(Formatting.None));
        var runObj = JObject.Parse(runResp);
        var runKey = (string?)runObj["key"] ?? (string?)runObj["id"];
        if (string.IsNullOrWhiteSpace(runKey))
            throw new InvalidOperationException(
                "Zephyr-Run-Anlage lieferte weder key noch id. Response: " + Truncate(runResp, 800));
        summary.RunKey = runKey;
        log?.Invoke($"  Zephyr Test-Run angelegt: {runKey}");

        // 2. Bulk-upload the results into the new run.
        var resultsPayload = ZephyrResultBuilder.BuildResultsPayload(plan.Inputs, env);
        log?.Invoke($"  POST {atm}/testrun/{runKey}/testresults  ({resultsPayload.Count} Ergebnisse)");
        var resResp = await PostJsonAsync(
            http, $"{atm}/testrun/{runKey}/testresults", resultsPayload.ToString(Formatting.None));
        summary.Uploaded = plan.Inputs.Count;
        log?.Invoke($"  Ergebnisse hochgeladen. Response: {Truncate(resResp, 400)}");

        return summary;
    }

    /// <summary>
    /// POSTs a JSON body as UTF-8 (no BOM, charset=utf-8) and returns the response
    /// body. Throws with status + body on a non-success status so the caller sees
    /// the Zephyr error verbatim.
    /// </summary>
    static async Task<string> PostJsonAsync(HttpClient http, string url, string json)
    {
        using var content = new StringContent(json, Utf8NoBom, "application/json");
        using var resp = await http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Zephyr POST {url} -> HTTP {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(body, 800)}");
        return body;
    }

    static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max) + "...";
}
