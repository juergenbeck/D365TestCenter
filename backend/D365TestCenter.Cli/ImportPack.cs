using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Core.Config;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Cli;

/// <summary>
/// B5 (ADR-0008) CLI wiring for <c>import-pack</c>. Imports a suite pack (built by
/// build-pack) into <c>jbe_testcase</c>, idempotently: a record is created if its
/// <c>jbe_testid</c> is new, otherwise updated. Unlike the legacy Markant PowerShell
/// import it writes <c>jbe_documentation</c> too, so the documentation lands in one step
/// (sync-docs becomes redundant). The pack parsing and field building are pure/testable;
/// only the create/update touches Dataverse.
/// </summary>
public static class ImportPack
{
    public const string DocumentationField = "jbe_documentation";   // Dataverse column
    const string PackDocProperty = "documentation";                  // pack test-case property

    public sealed class ImportSummary
    {
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }   // test cases without a testId
        public List<string> Ids { get; } = new();
    }

    /// <summary>Reads the pack file and returns its test-case objects. Pure (IO only).</summary>
    public static List<JObject> LoadTestCases(string packFile)
    {
        var root = JObject.Parse(File.ReadAllText(packFile));
        return (root["testCases"] as JArray)?.OfType<JObject>().Select(o => o).ToList()
               ?? new List<JObject>();
    }

    /// <summary>
    /// Builds the <c>jbe_definitionjson</c> for a pack test case: the executable
    /// TestCase JSON the engine deserializes. Maps <c>testId</c> -&gt; <c>id</c> (the engine
    /// model uses "id") and drops the fields that have their own columns
    /// (<c>userStories</c>, <c>documentation</c>). Steps and all other definition fields are kept.
    /// </summary>
    public static string BuildDefinitionJson(JObject testCase)
    {
        var def = (JObject)testCase.DeepClone();
        var testId = def.Value<string>("testId") ?? def.Value<string>("id") ?? "";
        def.Remove("testId");
        def.Remove("userStories");
        def.Remove(PackDocProperty);
        def["id"] = testId;
        return def.ToString(Formatting.None);
    }

    /// <summary>
    /// Imports the pack into <c>jbe_testcase</c>, matched by <c>jbe_testid</c>:
    /// create when new, update otherwise. Writes title, definition JSON, tags, user
    /// stories, documentation and enabled. Returns counts of created/updated/skipped.
    /// </summary>
    public static ImportSummary Import(
        IOrganizationService service, ITestCenterConfig cfg, string packFile, Action<string>? log = null)
        => Import(service, cfg, LoadTestCases(packFile), log);

    /// <summary>
    /// Imports the given test-case objects into <c>jbe_testcase</c> (create/update by
    /// <c>jbe_testid</c>). Same logic as the file-based overload but without IO, so the
    /// create/update contract is unit-testable against a fake service.
    /// </summary>
    public static ImportSummary Import(
        IOrganizationService service, ITestCenterConfig cfg, IReadOnlyList<JObject> testCases, Action<string>? log = null)
    {
        var summary = new ImportSummary();

        foreach (var tc in testCases)
        {
            var testId = tc.Value<string>("testId") ?? tc.Value<string>("id");
            if (string.IsNullOrWhiteSpace(testId))
            {
                summary.Skipped++;
                log?.Invoke("  (testCase ohne testId übersprungen)");
                continue;
            }

            var fields = new Dictionary<string, object>
            {
                ["jbe_title"] = tc.Value<string>("title") ?? testId!,
                ["jbe_definitionjson"] = BuildDefinitionJson(tc),
            };
            if (tc["tags"] is JArray tags)
                fields["jbe_tags"] = string.Join(",", tags.Select(t => t.ToString()));
            var userStories = tc.Value<string>("userStories");
            if (!string.IsNullOrWhiteSpace(userStories)) fields["jbe_userstories"] = userStories!;
            var documentation = tc.Value<string>(PackDocProperty);
            if (!string.IsNullOrWhiteSpace(documentation)) fields[DocumentationField] = documentation!;

            var existingId = FindTestCaseId(service, cfg, testId!);
            if (existingId == null)
            {
                // New test: default to enabled. jbe_enabled is set only here, never on update -
                // the pack carries no enabled flag, so an import must not flip the activation
                // status of an existing test (a deliberately disabled test stays disabled).
                var e = new Entity(cfg.TestCaseEntity) { ["jbe_testid"] = testId, ["jbe_enabled"] = true };
                foreach (var kv in fields) e[kv.Key] = kv.Value;
                service.Create(e);
                summary.Created++;
                log?.Invoke($"  CREATE {testId}  ({fields.Count + 1} Felder)");
            }
            else
            {
                var e = new Entity(cfg.TestCaseEntity, existingId.Value);
                foreach (var kv in fields) e[kv.Key] = kv.Value;
                service.Update(e);
                summary.Updated++;
                log?.Invoke($"  UPDATE {testId}  ({fields.Count} Felder)");
            }
            summary.Ids.Add(testId!);
        }
        return summary;
    }

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
