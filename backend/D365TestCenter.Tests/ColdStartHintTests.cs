using System.Collections.Generic;
using D365TestCenter.Core;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// C5 Cold-Start-Hint (Session 19). Heuristik: erster Test mit Dauer > 3x
/// Median der nachfolgenden Tests deutet auf JIT/Plugin-Class-Load/Metadata-
/// Cache-Warmup hin. Hint nur dann, sonst kein Rauschen.
/// </summary>
public class ColdStartHintTests
{
    [Fact]
    public void Hint_Fires_When_First_Is_Slow_And_Rest_Is_Fast()
    {
        var result = MakeRun(new long[] { 5000, 200, 180, 220, 190 });

        var (line1, line2) = TestCenterOrchestrator.BuildColdStartHint(result);

        Assert.NotNull(line1);
        Assert.NotNull(line2);
        Assert.Contains("5000ms", line1!);
        Assert.Contains("Cold-Start", line2!);
    }

    [Fact]
    public void Hint_Silent_When_First_Is_Comparable_To_Rest()
    {
        // Faktor 1.5x – keine Cold-Start-Empfehlung
        var result = MakeRun(new long[] { 300, 200, 180, 220, 190 });

        var (line1, line2) = TestCenterOrchestrator.BuildColdStartHint(result);

        Assert.Null(line1);
        Assert.Null(line2);
    }

    [Fact]
    public void Hint_Silent_When_Below_Min_Test_Count()
    {
        // Drei Tests reichen nicht (mindestens 4 für sinnvollen Median)
        var result = MakeRun(new long[] { 5000, 200, 180 });

        var (line1, line2) = TestCenterOrchestrator.BuildColdStartHint(result);

        Assert.Null(line1);
        Assert.Null(line2);
    }

    [Fact]
    public void Hint_Silent_When_First_Has_Zero_Duration()
    {
        // Skipped-Tests haben DurationMs=0 — als ersten ignorieren
        var result = MakeRun(new long[] { 0, 200, 180, 220, 190 });

        var (line1, line2) = TestCenterOrchestrator.BuildColdStartHint(result);

        Assert.Null(line1);
        Assert.Null(line2);
    }

    [Fact]
    public void Hint_Silent_When_Rest_Has_Too_Few_Nonzero_Durations()
    {
        // Wenn Folge-Tests fast alle skipped sind, ist der Median nicht stabil
        var result = MakeRun(new long[] { 5000, 200, 0, 0, 0 });

        var (line1, line2) = TestCenterOrchestrator.BuildColdStartHint(result);

        Assert.Null(line1);
        Assert.Null(line2);
    }

    [Fact]
    public void Hint_Fires_At_Exactly_Above_Three_Times_Median()
    {
        // Median der Folge-Tests = 100ms, Schwelle wäre 300ms (strikt-grösser).
        // 301ms muss kippen, 300ms nicht.
        var resultEdge = MakeRun(new long[] { 300, 100, 100, 100, 100 });
        var (e1, _) = TestCenterOrchestrator.BuildColdStartHint(resultEdge);
        Assert.Null(e1); // 300 ist nicht > 3*100, also kein Hint

        var resultJustAbove = MakeRun(new long[] { 301, 100, 100, 100, 100 });
        var (j1, _) = TestCenterOrchestrator.BuildColdStartHint(resultJustAbove);
        Assert.NotNull(j1);
    }

    // ════════════════════════════════════════════════════════════════
    //  Helper
    // ════════════════════════════════════════════════════════════════

    private static TestRunResult MakeRun(long[] durations)
    {
        var run = new TestRunResult();
        for (int i = 0; i < durations.Length; i++)
        {
            run.Results.Add(new TestCaseResult
            {
                TestId = $"TC{i + 1:D2}",
                Title = $"Test {i + 1}",
                DurationMs = durations[i],
                Outcome = durations[i] > 0 ? TestOutcome.Passed : TestOutcome.Skipped
            });
        }
        return run;
    }
}
