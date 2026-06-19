using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Core;
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
}
