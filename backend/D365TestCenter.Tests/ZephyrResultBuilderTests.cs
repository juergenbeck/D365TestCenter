using System;
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

    // ── OE-10: audit comment (tracked records with names + assert steps) ───

    [Fact]
    public void BuildAuditComment_RecordsWithNamesAndAsserts_RendersBothLines()
    {
        var tracked = new List<TrackedRecord>
        {
            new() { Entity = "account", Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Alias = "acc", Name = "JBE Test GmbH" },
            new() { Entity = "lead", Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    Alias = "lead" }   // no name
        };
        var asserts = new List<StepResult>
        {
            new() { Action = "Assert", Description = "Firma abgeleitet", Success = true, ActualDisplay = "JBE Test GmbH" },
            new() { Action = "CreateRecord", Description = "non-assert ignored", Success = true },
            new() { Action = "Assert", Description = "companyname gesetzt", Success = false, ActualDisplay = "leer" }
        };

        var c = ZephyrResultBuilder.BuildAuditComment(tracked, asserts, null);

        Assert.NotNull(c);
        Assert.Contains("Angelegt:", c);
        Assert.Contains("account \"JBE Test GmbH\" [acc]", c);
        Assert.Contains("lead [lead]", c);                 // no name -> entity + alias only
        Assert.Contains("Geprüft:", c);
        Assert.Contains("Firma abgeleitet = JBE Test GmbH (OK)", c);
        Assert.Contains("companyname gesetzt = leer (FAIL)", c);
        Assert.DoesNotContain("non-assert ignored", c);    // non-assert step skipped
    }

    [Fact]
    public void BuildAuditComment_PassWithRecordsOnly_DocumentsInsteadOfBlank()
    {
        // The PASS case that used to upload an empty comment now documents what ran.
        var tracked = new List<TrackedRecord>
        {
            new() { Entity = "contact", Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    Alias = "con", Name = "JBE Test, Max" }
        };

        var c = ZephyrResultBuilder.BuildAuditComment(tracked, null, null);

        Assert.NotNull(c);
        Assert.Contains("contact \"JBE Test, Max\" [con]", c!);
        Assert.DoesNotContain("Geprüft", c!);              // no asserts
        Assert.DoesNotContain("Fehler", c!);
    }

    [Fact]
    public void BuildAuditComment_NothingToSay_ReturnsNull()
        => Assert.Null(ZephyrResultBuilder.BuildAuditComment(
            new List<TrackedRecord>(), new List<StepResult>(), null));

    [Fact]
    public void BuildAuditComment_ErrorOnly_RendersFehlerLine()
        => Assert.Equal("Fehler: OrganizationServiceFault [0x80040217]",
            ZephyrResultBuilder.BuildAuditComment(null, null, "OrganizationServiceFault [0x80040217]"));
}
