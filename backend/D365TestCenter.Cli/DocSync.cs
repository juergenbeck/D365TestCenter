using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Cli;

/// <summary>
/// E1 (ADR-0008) CLI wiring for documentation pass-through. Reads the whitelisted
/// doc sections from the local Markdown definitions (Core
/// <see cref="MarkdownReportGenerator.BuildDocumentation"/>) and writes them into
/// <c>jbe_testcase.jbe_documentation</c> in Dataverse, matched by
/// <c>jbe_testid == front-matter id</c>. The HTML client then renders the doc per
/// test. The collect step is pure (testable); only the sync step touches Dataverse.
/// </summary>
public static class DocSync
{
    public const string DocumentationField = "jbe_documentation";

    public sealed class SyncSummary
    {
        public int Scanned { get; set; }
        public int WithDoc { get; set; }    // definitions that produced a doc block
        public int Updated { get; set; }    // jbe_testcase records written
        public int NotFound { get; set; }   // doc present but no matching jbe_testcase
        public List<string> UpdatedIds { get; } = new();
    }

    /// <summary>
    /// Walks the definitions and returns (id, documentation) for every *.md that has
    /// a front-matter id and at least one whitelisted doc section. Pure (IO only).
    /// </summary>
    public static List<(string Id, string Documentation)> CollectDocumentation(string defsDir)
    {
        if (!Directory.Exists(defsDir))
            throw new DirectoryNotFoundException($"Definitions directory not found: {defsDir}");

        var list = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.md", SearchOption.AllDirectories))
        {
            var doc = MarkdownReportGenerator.ParseDefinition(File.ReadAllText(file));
            if (string.IsNullOrWhiteSpace(doc.Id)) continue;
            var documentation = MarkdownReportGenerator.BuildDocumentation(doc);
            if (string.IsNullOrWhiteSpace(documentation)) continue;
            list.Add((doc.Id!, documentation));
        }
        return list;
    }

    /// <summary>
    /// Syncs the doc blocks into <c>jbe_documentation</c> on the matching
    /// <c>jbe_testcase</c> records. Definitions without a matching test case are
    /// reported as NotFound (sync-docs updates existing cases, it does not create them).
    /// </summary>
    public static SyncSummary SyncDocumentation(
        IOrganizationService service, ITestCenterConfig cfg, string defsDir, Action<string>? log = null)
    {
        var docs = CollectDocumentation(defsDir);
        var summary = new SyncSummary { Scanned = CountMarkdownFiles(defsDir), WithDoc = docs.Count };

        foreach (var (id, documentation) in docs)
        {
            var tcId = FindTestCaseId(service, cfg, id);
            if (tcId == null)
            {
                summary.NotFound++;
                log?.Invoke($"  (kein jbe_testcase für {id})");
                continue;
            }

            service.Update(new Entity(cfg.TestCaseEntity, tcId.Value)
            {
                [DocumentationField] = documentation
            });
            summary.Updated++;
            summary.UpdatedIds.Add(id);
            log?.Invoke($"  OK   {id}  ({documentation.Length} Zeichen Doku)");
        }
        return summary;
    }

    static int CountMarkdownFiles(string defsDir) =>
        Directory.Exists(defsDir)
            ? Directory.EnumerateFiles(defsDir, "*.md", SearchOption.AllDirectories).Count()
            : 0;

    static Guid? FindTestCaseId(IOrganizationService service, ITestCenterConfig cfg, string testId)
    {
        var q = new QueryExpression(cfg.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(false),
            Criteria = new FilterExpression
            {
                Conditions = { new ConditionExpression("jbe_testid", ConditionOperator.Equal, testId) }
            },
            TopCount = 1
        };
        return service.RetrieveMultiple(q).Entities.FirstOrDefault()?.Id;
    }
}
