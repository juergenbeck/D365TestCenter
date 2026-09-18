using System;
using System.Collections.Generic;
using System.IO;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;

namespace D365TestCenter.Cli;

/// <summary>
/// E3 (ADR-0008) CLI wiring for the run report. The render/parse logic lives in
/// the Core (<see cref="MarkdownReportGenerator"/>); this class owns the IO: it
/// reads the run header (jbe_testrun KPIs/timing) from Dataverse, walks the local
/// Markdown definitions + README, and assembles the <see cref="ReportModel"/>.
/// The per-test results are loaded via <see cref="RunResultLoader.LoadResultsFromRun"/>.
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

    /// <summary>Reads the run header (started/completed timestamps, filter) from jbe_testrun.
    /// Delegates the read to the Core <see cref="DataverseReportSource"/> (shared with the
    /// jbe_GenerateReport Custom API).</summary>
    public static RunHeader LoadRunHeader(IOrganizationService service, ITestCenterConfig cfg, Guid runId)
    {
        var (startedOn, completedOn, filter) = DataverseReportSource.LoadRunHeaderFacts(service, cfg, runId);
        return new RunHeader { StartedOn = startedOn, CompletedOn = completedOn, Filter = filter };
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

        return MarkdownReportGenerator.BuildModel(
            header?.StartedOn, header?.CompletedOn, header?.Filter,
            results, docs, suite, env, runId, log);
    }
}
