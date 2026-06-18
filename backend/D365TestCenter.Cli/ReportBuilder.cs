using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Cli;

/// <summary>
/// E3 (ADR-0008) CLI wiring for the run report. The render/parse logic lives in
/// the Core (<see cref="MarkdownReportGenerator"/>); this class owns the IO: it
/// reads the run header (jbe_testrun KPIs/timing) from Dataverse, walks the local
/// Markdown definitions + README, and assembles the <see cref="ReportModel"/>.
/// The per-test results are loaded via <see cref="ResultSync.LoadResultsFromRun"/>.
/// </summary>
public static class ReportBuilder
{
    /// <summary>Header facts of a run, read from the jbe_testrun record.</summary>
    public sealed class RunHeader
    {
        public DateTime? StartedOn { get; set; }
        public DateTime? CompletedOn { get; set; }
        public string Filter { get; set; } = "";
    }

    /// <summary>Reads the run header (started/completed timestamps, filter) from jbe_testrun.</summary>
    public static RunHeader LoadRunHeader(IOrganizationService service, ITestCenterConfig cfg, Guid runId)
    {
        var e = service.Retrieve(cfg.TestRunEntity, runId,
            new ColumnSet("jbe_startedon", "jbe_completedon", "jbe_testcasefilter"));
        return new RunHeader
        {
            StartedOn = e.GetAttributeValue<DateTime?>("jbe_startedon"),
            CompletedOn = e.GetAttributeValue<DateTime?>("jbe_completedon"),
            Filter = e.GetAttributeValue<string>("jbe_testcasefilter") ?? ""
        };
    }

    /// <summary>
    /// Assembles the report model: walks every *.md under <paramref name="defsDir"/>,
    /// parses definitions (matched to results by testId == front-matter id) and the
    /// README (suite header), and merges them with the run results. KPIs are counted
    /// from the shown items so the summary always matches the listing; the wall-clock
    /// duration comes from the run header (fallback: sum of test durations).
    /// </summary>
    public static ReportModel BuildModel(
        RunHeader? header, IReadOnlyList<TestCaseResult> results,
        string defsDir, string env, Guid runId, Action<string>? log = null)
    {
        if (results == null) throw new ArgumentNullException(nameof(results));
        if (!Directory.Exists(defsDir))
            throw new DirectoryNotFoundException($"Definitions directory not found: {defsDir}");

        var docs = new Dictionary<string, DefinitionDoc>(StringComparer.OrdinalIgnoreCase);
        SuiteDoc? suite = null;
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.md", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
            if (string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
            {
                // First README found wins (top-most by enumeration order is good enough).
                suite ??= MarkdownReportGenerator.ParseReadme(content);
                continue;
            }
            var doc = MarkdownReportGenerator.ParseDefinition(content);
            if (!string.IsNullOrWhiteSpace(doc.Id) && !docs.ContainsKey(doc.Id!))
                docs[doc.Id!] = doc;
        }

        var model = new ReportModel
        {
            SuiteTitle = suite?.Titel ?? "",
            SuiteIntro = suite?.Intro,
            SuiteCarrier = suite?.Carrier,
            Env = env ?? "",
            RunId = runId,
            Filter = header?.Filter ?? "",
            RunDate = header?.StartedOn?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
        };

        foreach (var r in results.OrderBy(x => x.TestId, StringComparer.Ordinal))
        {
            var item = new ReportItem
            {
                TestId = r.TestId,
                Outcome = r.Outcome,
                DurationMs = r.DurationMs,
                ErrorMessage = r.ErrorMessage
            };
            if (docs.TryGetValue(r.TestId, out var doc))
            {
                item.Titel = doc.Titel;
                foreach (var kv in doc.Sections) item.Sections[kv.Key] = kv.Value;
            }
            else
            {
                log?.Invoke($"  (keine MD-Definition für {r.TestId} unter {defsDir})");
            }
            model.Items.Add(item);
        }

        model.Total = model.Items.Count;
        model.Passed = model.Items.Count(i => i.Outcome == TestOutcome.Passed);
        model.Failed = model.Items.Count(i => i.Outcome == TestOutcome.Failed);
        model.Errored = model.Items.Count(i => i.Outcome == TestOutcome.Error);
        model.Skipped = model.Items.Count(i => i.Outcome == TestOutcome.Skipped);

        if (header?.StartedOn != null && header.CompletedOn != null && header.CompletedOn > header.StartedOn)
            model.DurationSeconds =
                (long)Math.Round((header.CompletedOn.Value - header.StartedOn.Value).TotalSeconds);
        else
            model.DurationSeconds = (long)Math.Round(results.Sum(r => r.DurationMs) / 1000.0);

        return model;
    }
}
