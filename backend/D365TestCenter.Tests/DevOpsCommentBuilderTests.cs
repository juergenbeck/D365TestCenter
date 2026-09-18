using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// sync-devops (ADR 2026-06-24): the HTML work-item-comment fragment. Verifies the KPI
/// header (counts, wall-clock vs fallback, env/filter), the per-test outcome mapping and
/// block structure, the integration of the shared audit block, and HTML-escaping. Pure
/// builder - the HTTP post and Dataverse reads (DevOpsSync) are covered by the live smoke.
/// </summary>
public class DevOpsCommentBuilderTests
{
    static TestCaseResult Result(string id, TestOutcome outcome, long durationMs = 1000,
        List<TrackedRecord>? tracked = null, List<StepResult>? steps = null, string? error = null)
        => new()
        {
            TestId = id,
            Outcome = outcome,
            DurationMs = durationMs,
            TrackedRecords = tracked ?? new(),
            StepResults = steps ?? new(),
            ErrorMessage = error
        };

    [Fact]
    public void BuildWorkItemComment_MixedRun_CountsKpisAndMapsOutcomes()
    {
        var results = new List<TestCaseResult>
        {
            Result("T1", TestOutcome.Passed),
            Result("T2", TestOutcome.Failed),
            Result("T3", TestOutcome.Skipped),
            Result("T4", TestOutcome.Error),
            Result("T5", TestOutcome.Passed)
        };
        var started = new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc);
        var html = DevOpsCommentBuilder.BuildWorkItemComment(
            started, "CRM-*", Guid.Parse("d6475da5-e46f-f111-ab0d-70a8a52aa915"), "DEV",
            results, TimeSpan.FromSeconds(14));

        Assert.Contains("<b>D365 Test Center: Durchführungsbericht</b><br>", html);
        // KPI: passed/total plus the non-zero buckets, then the wall-clock.
        Assert.Contains("2/5 PASS, 1 FAIL, 1 SKIP, 1 ERROR (14s)", html);
        // Date is rendered local; assert against the local conversion (timezone-safe).
        Assert.Contains($"<b>Lauf:</b> {started.ToLocalTime():yyyy-MM-dd}, Env DEV", html);
        Assert.Contains("<b>Run-ID:</b> d6475da5-e46f-f111-ab0d-70a8a52aa915", html);
        Assert.Contains("<b>Filter:</b> CRM-*", html);
        // Outcome mapping per test.
        Assert.Contains("<b>T1</b>: PASS", html);
        Assert.Contains("<b>T2</b>: FAIL", html);
        Assert.Contains("<b>T3</b>: SKIP", html);
        Assert.Contains("<b>T4</b>: ERROR", html);
        Assert.Contains("<b>T5</b>: PASS", html);
    }

    [Fact]
    public void BuildWorkItemComment_AllPass_OmitsZeroBucketsAndEnv()
    {
        var results = new List<TestCaseResult>
        {
            Result("T1", TestOutcome.Passed), Result("T2", TestOutcome.Passed)
        };
        var html = DevOpsCommentBuilder.BuildWorkItemComment(
            null, "", Guid.NewGuid(), null, results, TimeSpan.FromSeconds(5));

        Assert.Contains("2/2 PASS (5s)", html);
        Assert.DoesNotContain("FAIL", html);
        Assert.DoesNotContain("SKIP", html);
        Assert.DoesNotContain("ERROR", html);
        Assert.DoesNotContain("Env ", html);   // env null -> no env part
    }

    [Fact]
    public void BuildWorkItemComment_NoWallClock_FallsBackToSumOfDurations()
    {
        var results = new List<TestCaseResult>
        {
            Result("T1", TestOutcome.Passed, 3000),
            Result("T2", TestOutcome.Passed, 4000)
        };
        var html = DevOpsCommentBuilder.BuildWorkItemComment(null, "", Guid.NewGuid(), null, results, null);
        Assert.Contains("2/2 PASS (7s)", html);   // 3000 + 4000 = 7000 ms
    }

    [Fact]
    public void BuildWorkItemComment_WallClockWins_AndFormatsMinutes()
    {
        var results = new List<TestCaseResult> { Result("T1", TestOutcome.Passed, 3000) };
        var html = DevOpsCommentBuilder.BuildWorkItemComment(
            null, "", Guid.NewGuid(), null, results, TimeSpan.FromSeconds(90));
        Assert.Contains("1/1 PASS (1m 30s)", html);   // wall-clock 90s wins over the 3s sum
    }

    [Fact]
    public void BuildWorkItemComment_PerTestBlock_IncludesAuditAndEscapesRecordName()
    {
        var results = new List<TestCaseResult>
        {
            Result("CRM-ACC-01", TestOutcome.Passed, 12000,
                tracked: new List<TrackedRecord>
                {
                    new() { Entity = "account", Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            Alias = "acc", Name = "A & B <Co>" }
                },
                steps: new List<StepResult>
                {
                    new() { Action = "Assert", Description = "name", Success = true, ActualDisplay = "A & B <Co>" }
                })
        };
        var html = DevOpsCommentBuilder.BuildWorkItemComment(null, "", Guid.NewGuid(), null, results, null);

        Assert.Contains("<b>CRM-ACC-01</b>: PASS (12s)<br>", html);
        Assert.Contains("account \"A &amp; B &lt;Co&gt;\" [acc] (11111111-1111-1111-1111-111111111111)", html);
        Assert.Contains("&nbsp;&nbsp;- name = A &amp; B &lt;Co&gt; (OK)<br>", html);
        Assert.DoesNotContain("A & B <Co>", html);   // raw value must not leak unescaped
    }

    [Fact]
    public void BuildWorkItemComment_EmptyRun_RendersHeaderWithZeroTotal()
    {
        var html = DevOpsCommentBuilder.BuildWorkItemComment(
            null, "", Guid.NewGuid(), null, new List<TestCaseResult>(), null);

        Assert.Contains("<b>D365 Test Center: Durchführungsbericht</b>", html);
        Assert.Contains("0/0 PASS (0s)", html);
    }

    [Fact]
    public void BuildWorkItemComment_CapsAtMaxLength()
    {
        // Many tests with long error messages -> exceed the 30k cap -> truncated with a hint.
        var results = new List<TestCaseResult>();
        for (int i = 0; i < 200; i++)
            results.Add(Result($"T{i}", TestOutcome.Failed, 1000, error: new string('x', 500)));

        var html = DevOpsCommentBuilder.BuildWorkItemComment(null, "", Guid.NewGuid(), null, results, null);
        Assert.EndsWith("... (gekürzt)", html);
        Assert.True(html.Length <= 30000 + "... (gekürzt)".Length);
    }
}
