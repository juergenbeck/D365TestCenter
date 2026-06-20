using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer <see cref="ChunkResultWriter"/> (ADR-0009 H1/H3, idempotenter Result-Upsert).
/// Pinnt: erstes Schreiben legt Zeile+Steps an; Wiederholung fuer dasselbe (run,testid) erzeugt
/// KEINE Doppelzeile und KEINE Doppelschritte (Alternate-Key-Upsert + Step-Ersatz); Feld-Mapping;
/// getrennte Zeilen je Test-Id.
/// </summary>
public class ChunkResultWriterTests
{
    private static readonly Guid RunId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static TestCaseResult Result(string testId, TestOutcome outcome, long ms,
        params StepResult[] steps)
        => new TestCaseResult
        {
            TestId = testId,
            Title = testId,
            Outcome = outcome,
            DurationMs = ms,
            ErrorMessage = outcome == TestOutcome.Passed ? null : "boom",
            StepResults = steps.ToList()
        };

    private static StepResult Step(int n, string action, bool ok)
        => new StepResult { StepNumber = n, Action = action, Success = ok, Description = $"{action} {n}" };

    private static int ResultRowCount(FakeDataverse fake, string testId)
        => fake.All(WorkerSchema.TestRunResultEntity)
            .Count(r => r.GetAttributeValue<string>(WorkerSchema.ResultTestId) == testId);

    private static int StepCount(FakeDataverse fake)
        => fake.All(WorkerSchema.TestStepEntity).Count;

    [Fact]
    public void FirstWrite_CreatesResultRow_AndSteps()
    {
        var fake = new FakeDataverse();
        var writer = new ChunkResultWriter(fake);

        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Passed, 120,
            Step(1, "CreateRecord", true), Step(2, "Assert", true)));

        Assert.Equal(1, ResultRowCount(fake, "TC1"));
        Assert.Equal(2, StepCount(fake));

        var row = fake.All(WorkerSchema.TestRunResultEntity).Single();
        Assert.Equal("TC1", row.GetAttributeValue<string>(WorkerSchema.ResultTestId));
        Assert.Equal(WorkerSchema.OutcomePassed,
            row.GetAttributeValue<OptionSetValue>(WorkerSchema.ResultOutcome).Value);
        Assert.Equal(120, row.GetAttributeValue<int>(WorkerSchema.ResultDuration));
        Assert.Equal(RunId,
            row.GetAttributeValue<EntityReference>(WorkerSchema.ResultTestRun).Id);
    }

    [Fact]
    public void SecondWrite_SameRunAndTest_DoesNotDuplicateRow()
    {
        var fake = new FakeDataverse();
        var writer = new ChunkResultWriter(fake);

        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Failed, 50, Step(1, "Assert", false)));
        // Resume / Doppel-Fire: dieselbe (run,testid) erneut, jetzt Passed.
        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Passed, 90, Step(1, "Assert", true)));

        Assert.Equal(1, ResultRowCount(fake, "TC1"));

        var row = fake.All(WorkerSchema.TestRunResultEntity).Single();
        // Upsert hat die Zeile ERSETZT (jetzt Passed, 90 ms).
        Assert.Equal(WorkerSchema.OutcomePassed,
            row.GetAttributeValue<OptionSetValue>(WorkerSchema.ResultOutcome).Value);
        Assert.Equal(90, row.GetAttributeValue<int>(WorkerSchema.ResultDuration));
    }

    [Fact]
    public void SecondWrite_ReplacesSteps_NoDuplication()
    {
        var fake = new FakeDataverse();
        var writer = new ChunkResultWriter(fake);

        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Failed, 50,
            Step(1, "CreateRecord", true), Step(2, "Assert", false)));
        Assert.Equal(2, StepCount(fake));

        // Re-Lauf der Gruppe: 3 Steps. Alte 2 muessen weg sein.
        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Passed, 90,
            Step(1, "CreateRecord", true), Step(2, "UpdateRecord", true), Step(3, "Assert", true)));

        Assert.Equal(3, StepCount(fake));
        Assert.All(fake.All(WorkerSchema.TestStepEntity),
            s => Assert.Equal(WorkerSchema.StepPassed,
                s.GetAttributeValue<OptionSetValue>(WorkerSchema.StepStatus).Value));
    }

    [Fact]
    public void DifferentTestId_CreatesSeparateRows()
    {
        var fake = new FakeDataverse();
        var writer = new ChunkResultWriter(fake);

        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Passed, 10));
        writer.UpsertResult(RunId, Result("TC2", TestOutcome.Passed, 20));

        Assert.Equal(2, fake.All(WorkerSchema.TestRunResultEntity).Count);
        Assert.Equal(1, ResultRowCount(fake, "TC1"));
        Assert.Equal(1, ResultRowCount(fake, "TC2"));
    }

    [Fact]
    public void TrackedRecords_WrittenAsJson_WhenPresent()
    {
        var fake = new FakeDataverse();
        var writer = new ChunkResultWriter(fake);

        var r = Result("TC1", TestOutcome.Passed, 10);
        r.TrackedRecords = new List<TrackedRecord>
        {
            new TrackedRecord { Entity = "account", Id = Guid.NewGuid(), Name = "Acme" }
        };
        writer.UpsertResult(RunId, r);

        var row = fake.All(WorkerSchema.TestRunResultEntity).Single();
        var tracked = row.GetAttributeValue<string>(WorkerSchema.ResultTrackedRecords);
        Assert.False(string.IsNullOrEmpty(tracked));
        Assert.Contains("account", tracked);
        Assert.Contains("Acme", tracked);
    }

    [Fact]
    public void Assertions_SerializedFromAssertSteps()
    {
        var fake = new FakeDataverse();
        var writer = new ChunkResultWriter(fake);

        writer.UpsertResult(RunId, Result("TC1", TestOutcome.Passed, 10,
            Step(1, "CreateRecord", true), Step(2, "Assert", true)));

        var row = fake.All(WorkerSchema.TestRunResultEntity).Single();
        var asserts = row.GetAttributeValue<string>(WorkerSchema.ResultAssertions);
        Assert.False(string.IsNullOrEmpty(asserts));
        // Nur der Assert-Step landet im Assertions-JSON, nicht der CreateRecord-Step.
        Assert.Contains("Assert 2", asserts);
        Assert.DoesNotContain("CreateRecord 1", asserts);
    }
}
