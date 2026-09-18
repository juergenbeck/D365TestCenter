using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Cli;

/// <summary>
/// sync-devops (ADR 2026-06-24): the licence-free E5 pendant to <see cref="ZephyrSync"/>.
/// CLI wiring that posts a run's results as an Azure-DevOps work-item comment. The HTML
/// fragment logic lives in the Core (<see cref="DevOpsCommentBuilder"/> + the shared
/// <see cref="AuditCommentBuilder"/>); this class owns the IO: reading the run results
/// from Dataverse (reusing <see cref="RunResultLoader.LoadResultsFromRun"/>) and the one
/// HTTP POST against the Azure DevOps Comments API.
///
/// Model: one jbe_testrun -> one explicitly named work-item -> one comment (KPI header +
/// per-test Angelegt/Geprüft/Fehler block). Auth is an AAD Bearer (the CLI stays
/// secret-agnostic; the PowerShell wrapper sources the token from TokenVault). Writes to
/// Azure DevOps, so it is approval-gated (Goldene Regel).
///
/// Note: the PowerShell DevOps-REST pitfalls (the response content-type suffix that defeats
/// Invoke-RestMethod's auto-JSON, -AsHashtable) apply ONLY to the PS wrapper. The
/// HttpClient reads the body as a string and parses it with JObject.Parse - no workaround
/// needed, exactly like <see cref="ZephyrSync"/>.
/// </summary>
public static class DevOpsSync
{
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public sealed class SyncSummary
    {
        /// <summary>Target work-item id.</summary>
        public int WorkItemId { get; set; }
        /// <summary>Id of the created comment (Azure DevOps Comments API), when posted.</summary>
        public int? CommentId { get; set; }
        /// <summary>Results found in the run.</summary>
        public int TestsTotal { get; set; }
        /// <summary>True when a comment was actually posted (false when the run had no results).</summary>
        public bool Posted { get; set; }
    }

    /// <summary>
    /// Full sync: load the run results, build the HTML audit fragment, and post it as a
    /// comment on <paramref name="workItemId"/>. Returns a summary; never partially silent
    /// (the outcome distribution and the posted comment-id are logged). Posts nothing when
    /// the run has no results.
    /// </summary>
    public static async Task<SyncSummary> SyncAsync(
        IOrganizationService service, ITestCenterConfig cfg, Guid runId,
        int workItemId, string devopsBaseUrl, string org, string project, string bearer,
        string? env, Action<string>? log = null)
    {
        var results = RunResultLoader.LoadResultsFromRun(service, cfg, runId);
        var summary = new SyncSummary { WorkItemId = workItemId, TestsTotal = results.Count };
        if (results.Count == 0)
        {
            log?.Invoke($"  Keine jbe_testrunresult-Records für Run {runId} gefunden - nichts zu posten.");
            return summary;
        }

        // Header for the KPI line: run date, filter, and the wall-clock duration
        // (CompletedOn - StartedOn). Builder falls back to the sum of per-test durations.
        var header = ReportBuilder.LoadRunHeader(service, cfg, runId);
        TimeSpan? wallClock = (header?.StartedOn != null && header?.CompletedOn != null)
            ? header.CompletedOn.Value - header.StartedOn.Value
            : null;

        var html = DevOpsCommentBuilder.BuildWorkItemComment(
            header?.StartedOn, header?.Filter, runId, env, results, wallClock);

        log?.Invoke($"  Run {runId}: {results.Count} Testfälle ({OutcomeDistribution(results)}).");

        var url = $"{devopsBaseUrl.TrimEnd('/')}/{org}/{project}" +
                  $"/_apis/wit/workItems/{workItemId}/comments?api-version=7.1-preview.4";
        var body = new JObject { ["text"] = html };

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        log?.Invoke($"  POST {url}");
        var resp = await PostJsonAsync(http, url, body.ToString(Formatting.None));
        var respObj = JObject.Parse(resp);
        summary.CommentId = (int?)respObj["id"];
        summary.Posted = true;
        log?.Invoke($"  Kommentar gepostet: Work-Item {workItemId}, Comment-Id {summary.CommentId}.");

        return summary;
    }

    /// <summary>Compact outcome distribution for the log, e.g. "3 PASS, 1 FAIL, 1 SKIP".</summary>
    static string OutcomeDistribution(IReadOnlyList<TestCaseResult> results)
    {
        var parts = new List<string>();
        int p = results.Count(r => r.Outcome == TestOutcome.Passed);
        int f = results.Count(r => r.Outcome == TestOutcome.Failed);
        int s = results.Count(r => r.Outcome == TestOutcome.Skipped);
        int e = results.Count(r => r.Outcome == TestOutcome.Error);
        if (p > 0) parts.Add($"{p} PASS");
        if (f > 0) parts.Add($"{f} FAIL");
        if (s > 0) parts.Add($"{s} SKIP");
        if (e > 0) parts.Add($"{e} ERROR");
        return parts.Count > 0 ? string.Join(", ", parts) : "0";
    }

    /// <summary>
    /// POSTs a JSON body as UTF-8 (no BOM, charset=utf-8) and returns the response body.
    /// Throws with status + body on a non-success status so the caller sees the Azure
    /// DevOps error verbatim. The Bearer header is set on the shared HttpClient.
    /// </summary>
    static async Task<string> PostJsonAsync(HttpClient http, string url, string json)
    {
        using var content = new StringContent(json, Utf8NoBom, "application/json");
        using var resp = await http.PostAsync(url, content);
        var responseBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"DevOps POST {url} -> HTTP {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(responseBody, 800)}");
        return responseBody;
    }

    static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? (s ?? "") : s.Substring(0, max) + "...";
}
