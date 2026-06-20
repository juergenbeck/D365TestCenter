using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using D365TestCenter.Core.Config;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// Dataverse source for the run report (ADR-0009 Phase 4, <c>jbe_GenerateReport</c>). Reads the run
/// header (jbe_testrun), the per-test results (<see cref="RunResultLoader"/>) and the definition docs
/// from jbe_testcase (the jbe_documentation sections + jbe_title), then assembles the model through
/// the source-agnostic <see cref="MarkdownReportGenerator.BuildModel"/>. Core home so the Custom-API
/// plugin reuses it - no file system (the CLI <c>report</c> walks the Markdown tree instead and feeds
/// the same renderer).
/// </summary>
public static class DataverseReportSource
{
    /// <summary>Reads the run header facts (started/completed timestamps + filter) from jbe_testrun.</summary>
    public static (DateTime? StartedOn, DateTime? CompletedOn, string Filter) LoadRunHeaderFacts(
        IOrganizationService service, ITestCenterConfig cfg, Guid runId)
    {
        var e = service.Retrieve(cfg.TestRunEntity, runId,
            new ColumnSet(WorkerSchema.RunStartedOn, WorkerSchema.RunCompletedOn, WorkerSchema.RunFilter));
        return (
            e.GetAttributeValue<DateTime?>(WorkerSchema.RunStartedOn),
            e.GetAttributeValue<DateTime?>(WorkerSchema.RunCompletedOn),
            e.GetAttributeValue<string>(WorkerSchema.RunFilter) ?? "");
    }

    /// <summary>
    /// Loads the definition docs (id, titel, "## " sections) for the given test ids from jbe_testcase.
    /// The sections come from jbe_documentation (E1 Markdown without front-matter), so they are split
    /// directly; id/titel come from the dedicated fields (jbe_testid/jbe_title). Missing test cases are
    /// simply absent from the dictionary - the report still lists the result, untitled. Keyed
    /// case-insensitively by test id; first record wins on a duplicate id.
    /// </summary>
    public static Dictionary<string, DefinitionDoc> LoadDocs(
        IOrganizationService service, IEnumerable<string> testIds)
    {
        var docs = new Dictionary<string, DefinitionDoc>(StringComparer.OrdinalIgnoreCase);
        var ids = testIds?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<object>().ToArray() ?? Array.Empty<object>();
        if (ids.Length == 0) return docs;

        var query = new QueryExpression(WorkerSchema.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.TcTestId, WorkerSchema.TcTitle, WorkerSchema.TcDocumentation),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.TcTestId, ConditionOperator.In, ids);

        foreach (var e in service.RetrieveMultiple(query).Entities)
        {
            var id = e.GetAttributeValue<string>(WorkerSchema.TcTestId);
            if (string.IsNullOrWhiteSpace(id) || docs.ContainsKey(id!)) continue;

            var doc = new DefinitionDoc
            {
                Id = id,
                Titel = e.GetAttributeValue<string>(WorkerSchema.TcTitle) ?? ""
            };
            var documentation = e.GetAttributeValue<string>(WorkerSchema.TcDocumentation);
            if (!string.IsNullOrWhiteSpace(documentation))
                foreach (var kv in MarkdownDocument.SplitSections(documentation!))
                    doc.Sections[kv.Key] = kv.Value;
            docs[id!] = doc;
        }
        return docs;
    }

    /// <summary>
    /// Assembles the full report model for a run from Dataverse: header + results + jbe_testcase docs.
    /// There is no suite README in Dataverse, so the suite header stays empty.
    /// </summary>
    public static ReportModel BuildModel(
        IOrganizationService service, ITestCenterConfig cfg, Guid runId, string? env,
        Action<string>? log = null)
    {
        var (startedOn, completedOn, filter) = LoadRunHeaderFacts(service, cfg, runId);
        var results = RunResultLoader.LoadResultsFromRun(service, cfg, runId);
        var docs = LoadDocs(service, results.Select(r => r.TestId));
        return MarkdownReportGenerator.BuildModel(
            startedOn, completedOn, filter, results, docs, suite: null, env, runId, log);
    }
}
