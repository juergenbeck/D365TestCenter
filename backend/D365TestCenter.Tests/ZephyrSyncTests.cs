using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E5 (ADR-0008) CLI-side, IO-only seams of the Zephyr upload: the zephyr_key
/// front-matter walk (LoadZephyrKeys) and the result->input matching (BuildPlan).
/// The Dataverse reads and the HTTP POSTs (SyncAsync) need a live server and are
/// verified by the live smoke, not here.
/// </summary>
public class ZephyrSyncTests
{
    static TestCaseResult Tc(string id, TestOutcome o, long ms = 0, string? err = null)
        => new TestCaseResult { TestId = id, Outcome = o, DurationMs = ms, ErrorMessage = err };

    [Fact]
    public void BuildPlan_MatchesKeysAndSkipsMissing()
    {
        var results = new List<TestCaseResult>
        {
            Tc("DYN10000-TC1", TestOutcome.Passed, 5000),
            Tc("DYN10000-TC2", TestOutcome.Failed, 1000, "Assert fehlgeschlagen"),
            Tc("DYN10000-TC9", TestOutcome.Passed),     // no zephyr_key -> skipped
            Tc("", TestOutcome.Passed)                   // blank testId -> ignored
        };
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DYN10000-TC1"] = "DYN-T1",
            ["DYN10000-TC2"] = "DYN-T2"
        };

        var plan = ZephyrSync.BuildPlan(results, keys);

        Assert.Equal(2, plan.Inputs.Count);
        Assert.Equal("DYN-T1", plan.Inputs[0].ZephyrKey);
        Assert.Equal(5000, plan.Inputs[0].DurationMs);
        Assert.Null(plan.Inputs[0].Comment);
        Assert.Equal("DYN-T2", plan.Inputs[1].ZephyrKey);
        Assert.Equal("Assert fehlgeschlagen", plan.Inputs[1].Comment);    // errormessage -> comment
        Assert.Single(plan.SkippedNoKey);
        Assert.Equal("DYN10000-TC9", plan.SkippedNoKey[0]);
    }

    [Fact]
    public void LoadZephyrKeys_ReadsIdToZephyrKeyFromFrontmatter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "sub", "tc1.md"),
                "---\nid: DYN10000-TC1\nzephyr_key: DYN-T1\ntitel: \"A\"\n---\n\n## Zweck\n\nx\n");
            // No zephyr_key -> absent from the map.
            File.WriteAllText(Path.Combine(dir, "sub", "tc2.md"),
                "---\nid: DYN10000-TC2\ntitel: \"B\"\n---\n\n## Zweck\n\ny\n");

            var map = ZephyrSync.LoadZephyrKeys(dir);

            Assert.Single(map);
            Assert.Equal("DYN-T1", map["DYN10000-TC1"]);
            Assert.False(map.ContainsKey("DYN10000-TC2"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LoadZephyrKeys_MissingDir_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            ZephyrSync.LoadZephyrKeys(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void BuildPlan_AllMissing_EmptyInputs()
    {
        var results = new List<TestCaseResult> { Tc("X", TestOutcome.Passed) };
        var plan = ZephyrSync.BuildPlan(results, new Dictionary<string, string>());
        Assert.Empty(plan.Inputs);
        Assert.Single(plan.SkippedNoKey);
    }

    // ── Phase 2 (Decision 25): per-step scriptResults, opt-in ───────────

    [Fact]
    public void BuildPlan_WithoutSteps_LeavesScriptResultsNull()
    {
        var results = new List<TestCaseResult> { Tc("DYN10000-TC1", TestOutcome.Passed) };
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DYN10000-TC1"] = "DYN-T1"
        };

        var plan = ZephyrSync.BuildPlan(results, keys);   // no stepsByTestId

        Assert.Single(plan.Inputs);
        Assert.Null(plan.Inputs[0].ScriptResults);
    }

    [Fact]
    public void BuildPlan_WithSteps_AttachesScriptResultsToMatchingTestId()
    {
        var results = new List<TestCaseResult>
        {
            Tc("DYN10000-TC1", TestOutcome.Failed, 2000, "boom"),
            Tc("DYN10000-TC2", TestOutcome.Passed)       // no steps for this one
        };
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DYN10000-TC1"] = "DYN-T1",
            ["DYN10000-TC2"] = "DYN-T2"
        };
        var steps = new Dictionary<string, IReadOnlyList<ZephyrResultBuilder.ScriptResultInput>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["DYN10000-TC1"] = new List<ZephyrResultBuilder.ScriptResultInput>
            {
                new() { Index = 0, Outcome = TestOutcome.Passed },
                new() { Index = 1, Outcome = TestOutcome.Failed, Comment = "boom" }
            }
        };

        var plan = ZephyrSync.BuildPlan(results, keys, steps);

        Assert.Equal(2, plan.Inputs.Count);
        var tc1 = plan.Inputs.Single(i => i.ZephyrKey == "DYN-T1");
        Assert.NotNull(tc1.ScriptResults);
        Assert.Equal(2, tc1.ScriptResults!.Count);
        Assert.Equal(1, tc1.ScriptResults[1].Index);
        Assert.Equal(TestOutcome.Failed, tc1.ScriptResults[1].Outcome);
        // testId without step data keeps ScriptResults null (not an empty array)
        var tc2 = plan.Inputs.Single(i => i.ZephyrKey == "DYN-T2");
        Assert.Null(tc2.ScriptResults);
    }

    [Fact]
    public void LoadStepResultsByTestId_GroupsByTestId_OrdersAndIndexesFromZero()
    {
        var cfg = new StandardCrmConfig();
        // Two tests, steps deliberately out of order to exercise the OrderBy.
        var svc = new StepFakeService(cfg, new[]
        {
            Step("DYN10000-TC1", 3, passed: true,  err: null),
            Step("DYN10000-TC1", 1, passed: true,  err: null),
            Step("DYN10000-TC1", 2, passed: false, err: "assert failed"),
            Step("DYN10000-TC2", 1, passed: true,  err: null)
        });

        var map = ZephyrSync.LoadStepResultsByTestId(svc, cfg, Guid.NewGuid());

        Assert.Equal(2, map.Count);
        var tc1 = map["DYN10000-TC1"];
        Assert.Equal(3, tc1.Count);
        // contiguous 0-based index in step-number order
        Assert.Equal(new[] { 0, 1, 2 }, tc1.Select(s => s.Index));
        Assert.Equal(TestOutcome.Passed, tc1[0].Outcome);   // step 1
        Assert.Equal(TestOutcome.Failed, tc1[1].Outcome);   // step 2
        Assert.Equal("assert failed", tc1[1].Comment);
        Assert.Null(tc1[0].Comment);                        // blank error -> no comment
        Assert.Equal(TestOutcome.Passed, tc1[2].Outcome);   // step 3
        Assert.Single(map["DYN10000-TC2"]);
        Assert.Equal(0, map["DYN10000-TC2"][0].Index);
    }

    [Fact]
    public void LoadStepResultsByTestId_NoSteps_EmptyMap()
    {
        var cfg = new StandardCrmConfig();
        var map = ZephyrSync.LoadStepResultsByTestId(
            new StepFakeService(cfg, Array.Empty<Entity>()), cfg, Guid.NewGuid());
        Assert.Empty(map);
    }

    /// <summary>Builds a jbe_teststep entity as the link-query would return it
    /// (step fields + aliased parent testId under "res.jbe_testid").</summary>
    static Entity Step(string testId, int stepNumber, bool passed, string? err)
    {
        var cfg = new StandardCrmConfig();
        var e = new Entity("jbe_teststep")
        {
            ["jbe_stepnumber"] = stepNumber,
            ["jbe_stepstatus"] = new OptionSetValue(passed ? cfg.OutcomePassed : cfg.OutcomeFailed),
            ["res.jbe_testid"] = new AliasedValue("jbe_testrunresult", "jbe_testid", testId)
        };
        if (err != null) e["jbe_errormessage"] = err;
        return e;
    }

    /// <summary>Fake returning a fixed jbe_teststep set on RetrieveMultiple
    /// (the link/filter semantics are verified by the live smoke, not here).</summary>
    sealed class StepFakeService : IOrganizationService
    {
        readonly EntityCollection _steps;
        public StepFakeService(ITestCenterConfig cfg, IEnumerable<Entity> steps)
        {
            _steps = new EntityCollection();
            _steps.Entities.AddRange(steps);
        }

        public EntityCollection RetrieveMultiple(QueryBase query) => _steps;

        public Guid Create(Entity entity) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }
}
