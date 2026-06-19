using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E5 (ADR-0008) pure payload builder for the Zephyr Scale (DC / ATM 1.0) upload:
/// outcome mapping and the two JSON payloads (create cycle, bulk results). The HTTP
/// call and the Dataverse reads (ZephyrSync) need a live server and are verified by
/// the live smoke, not here.
/// </summary>
public class ZephyrResultBuilderTests
{
    [Theory]
    [InlineData(TestOutcome.Passed, "Pass")]
    [InlineData(TestOutcome.Failed, "Fail")]
    [InlineData(TestOutcome.Error, "Fail")]      // Decision 24: Error -> Fail, not Blocked
    [InlineData(TestOutcome.Skipped, "Not Executed")]
    public void MapStatus_MapsEveryOutcome(TestOutcome outcome, string expected)
        => Assert.Equal(expected, ZephyrResultBuilder.MapStatus(outcome));

    [Fact]
    public void BuildTestRunPayload_HasProjectNameAndDistinctItems()
    {
        var p = ZephyrResultBuilder.BuildTestRunPayload(
            "DYN", "Cycle X", new[] { "DYN-T1", "DYN-T2", "dyn-t1", "  " });

        Assert.Equal("DYN", (string?)p["projectKey"]);
        Assert.Equal("Cycle X", (string?)p["name"]);
        var items = (JArray)p["items"]!;
        // dyn-t1 deduped case-insensitively, blank dropped -> 2 distinct keys
        Assert.Equal(2, items.Count);
        Assert.Equal("DYN-T1", (string?)items[0]["testCaseKey"]);
        Assert.Equal("DYN-T2", (string?)items[1]["testCaseKey"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildTestRunPayload_MissingProjectOrName_Throws(string bad)
    {
        Assert.Throws<System.ArgumentException>(() =>
            ZephyrResultBuilder.BuildTestRunPayload(bad, "name", new[] { "DYN-T1" }));
        Assert.Throws<System.ArgumentException>(() =>
            ZephyrResultBuilder.BuildTestRunPayload("DYN", bad, new[] { "DYN-T1" }));
    }

    [Fact]
    public void BuildResult_PassWithDuration_IncludesEnvAndExecutionTime()
    {
        var o = ZephyrResultBuilder.BuildResult(
            new ZephyrResultBuilder.ResultInput
            {
                ZephyrKey = "DYN-T123",
                Outcome = TestOutcome.Passed,
                DurationMs = 180000
            },
            "DEV");

        Assert.Equal("DYN-T123", (string?)o["testCaseKey"]);
        Assert.Equal("Pass", (string?)o["status"]);
        Assert.Equal("DEV", (string?)o["environment"]);
        Assert.Equal(180000L, (long)o["executionTime"]!);
        Assert.Null(o["comment"]);          // omitted when absent
        Assert.Null(o["scriptResults"]);    // omitted in Phase 1
    }

    [Fact]
    public void BuildResult_FailWithComment_IncludesComment()
    {
        var o = ZephyrResultBuilder.BuildResult(
            new ZephyrResultBuilder.ResultInput
            {
                ZephyrKey = "DYN-T9",
                Outcome = TestOutcome.Failed,
                DurationMs = 0,
                Comment = "Assert websiteurl fehlgeschlagen"
            },
            null);

        Assert.Equal("Fail", (string?)o["status"]);
        Assert.Null(o["environment"]);        // omitted when null
        Assert.Null(o["executionTime"]);      // omitted when 0
        Assert.Equal("Assert websiteurl fehlgeschlagen", (string?)o["comment"]);
    }

    [Fact]
    public void BuildResult_WithScriptResults_RendersPerStepArray()
    {
        var o = ZephyrResultBuilder.BuildResult(
            new ZephyrResultBuilder.ResultInput
            {
                ZephyrKey = "DYN-T5",
                Outcome = TestOutcome.Failed,
                ScriptResults = new List<ZephyrResultBuilder.ScriptResultInput>
                {
                    new() { Index = 0, Outcome = TestOutcome.Passed },
                    new() { Index = 1, Outcome = TestOutcome.Failed, Comment = "boom" }
                }
            },
            "DEV");

        var arr = (JArray)o["scriptResults"]!;
        Assert.Equal(2, arr.Count);
        Assert.Equal(0, (int)arr[0]["index"]!);
        Assert.Equal("Pass", (string?)arr[0]["status"]);
        Assert.Null(arr[0]["comment"]);
        Assert.Equal("Fail", (string?)arr[1]["status"]);
        Assert.Equal("boom", (string?)arr[1]["comment"]);
    }

    [Fact]
    public void BuildResult_MissingZephyrKey_Throws()
    {
        Assert.Throws<System.ArgumentException>(() =>
            ZephyrResultBuilder.BuildResult(
                new ZephyrResultBuilder.ResultInput { ZephyrKey = "", Outcome = TestOutcome.Passed }, "DEV"));
    }

    [Fact]
    public void BuildResultsPayload_KeepsOrderAndCount()
    {
        var inputs = new[]
        {
            new ZephyrResultBuilder.ResultInput { ZephyrKey = "DYN-T1", Outcome = TestOutcome.Passed },
            new ZephyrResultBuilder.ResultInput { ZephyrKey = "DYN-T2", Outcome = TestOutcome.Skipped }
        };
        var arr = ZephyrResultBuilder.BuildResultsPayload(inputs, "TEST");

        Assert.Equal(2, arr.Count);
        Assert.Equal("DYN-T1", (string?)arr[0]["testCaseKey"]);
        Assert.Equal("Not Executed", (string?)arr[1]["status"]);
        Assert.True(arr.All(x => (string?)x["environment"] == "TEST"));
    }
}
