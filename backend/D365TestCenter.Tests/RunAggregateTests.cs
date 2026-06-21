using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für TestRunner.ComputeRunAggregate (ADR-0009 B.5 / Phase 3, Plateau-Aggregat).
/// Outcome-Split + Dauer-Verteilung (über ausgeführte Tests, Skipped ausgenommen) +
/// angelegte Records. Median-Konvention sorted[count/2] (wie Cold-Start-Heuristik).
/// </summary>
public class RunAggregateTests
{
    private static TestCaseResult R(string id, TestOutcome outcome, long ms, int records = 0)
        => new TestCaseResult
        {
            TestId = id,
            Title = id,
            Outcome = outcome,
            DurationMs = ms,
            TrackedRecords = Enumerable.Range(0, records)
                .Select(_ => new TrackedRecord { Entity = "x" })
                .ToList()
        };

    [Fact]
    public void OutcomeSplit_CountsByOutcome()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Passed, 10),
            R("b", TestOutcome.Failed, 10),
            R("c", TestOutcome.Error, 10),
            R("d", TestOutcome.Skipped, 0),
            R("e", TestOutcome.Passed, 10),
        });

        Assert.Equal(5, agg.Total);
        Assert.Equal(2, agg.Passed);
        Assert.Equal(1, agg.Failed);
        Assert.Equal(1, agg.Errored);
        Assert.Equal(1, agg.Skipped);
    }

    [Fact]
    public void DurationDistribution_OverExecuted()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Passed, 100),
            R("b", TestOutcome.Passed, 200),
            R("c", TestOutcome.Passed, 300),
        });

        Assert.Equal(600, agg.TotalTestMs);
        Assert.Equal(200, agg.AvgTestMs);
        Assert.Equal(200, agg.MedianTestMs);   // sorted[3/2] = sorted[1]
        Assert.Equal(100, agg.MinTestMs);
        Assert.Equal(300, agg.MaxTestMs);
        Assert.Equal("c", agg.SlowestTestId);
    }

    [Fact]
    public void Median_UpperMiddleForEvenCount_LikeColdStart()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Passed, 10),
            R("b", TestOutcome.Passed, 20),
            R("c", TestOutcome.Passed, 30),
            R("d", TestOutcome.Passed, 40),
        });

        Assert.Equal(30, agg.MedianTestMs);   // sorted[4/2] = sorted[2] = 30
        Assert.Equal(25, agg.AvgTestMs);
        Assert.Equal(10, agg.MinTestMs);
        Assert.Equal(40, agg.MaxTestMs);
    }

    [Fact]
    public void SkippedTests_ExcludedFromDistribution_ButCountedInTotal()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Passed, 100),
            R("b", TestOutcome.Skipped, 0),
            R("c", TestOutcome.Passed, 300),
        });

        Assert.Equal(3, agg.Total);
        Assert.Equal(2, agg.Passed);
        Assert.Equal(1, agg.Skipped);
        Assert.Equal(400, agg.TotalTestMs);
        Assert.Equal(200, agg.AvgTestMs);     // 400 / 2 ausgeführte
        Assert.Equal(300, agg.MedianTestMs);  // sorted[2/2] = sorted[1] = 300
        Assert.Equal(100, agg.MinTestMs);
        Assert.Equal(300, agg.MaxTestMs);
        Assert.Equal("c", agg.SlowestTestId);
    }

    [Fact]
    public void RecordsCreated_SumsTrackedRecords()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Passed, 100, records: 2),
            R("b", TestOutcome.Passed, 200, records: 3),
        });

        Assert.Equal(5, agg.RecordsCreated);
    }

    [Fact]
    public void SlowestTestId_PicksMaxDuration()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Passed, 300),
            R("b", TestOutcome.Passed, 100),
            R("c", TestOutcome.Passed, 200),
        });

        Assert.Equal("a", agg.SlowestTestId);
        Assert.Equal(300, agg.MaxTestMs);
    }

    [Fact]
    public void EmptyResults_AllZero_SlowestNull()
    {
        var agg = TestRunner.ComputeRunAggregate(new List<TestCaseResult>());

        Assert.Equal(0, agg.Total);
        Assert.Equal(0, agg.TotalTestMs);
        Assert.Equal(0, agg.AvgTestMs);
        Assert.Equal(0, agg.MedianTestMs);
        Assert.Equal(0, agg.MinTestMs);
        Assert.Equal(0, agg.MaxTestMs);
        Assert.Null(agg.SlowestTestId);
    }

    [Fact]
    public void AllSkipped_NoDistribution()
    {
        var agg = TestRunner.ComputeRunAggregate(new[]
        {
            R("a", TestOutcome.Skipped, 0),
            R("b", TestOutcome.Skipped, 0),
        });

        Assert.Equal(2, agg.Total);
        Assert.Equal(2, agg.Skipped);
        Assert.Equal(0, agg.TotalTestMs);
        Assert.Equal(0, agg.MedianTestMs);
        Assert.Null(agg.SlowestTestId);
    }
}
