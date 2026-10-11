using System;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// ADR-2026-10-11-2041 Entscheidungen 2 und 3: a run whose chunk was given up must not look
/// like an empty, successful run. Every test id of a failed chunk without a result becomes an
/// Error result carrying the chunk's cause, and the run ends with status Error.
/// </summary>
public class RunCompletionLostTestsTests
{
    private static readonly DateTime T0 = new DateTime(2026, 10, 11, 17, 0, 0, DateTimeKind.Utc);

    private static Guid SeedRun(FakeDataverse fake, int chunksTotal)
    {
        var run = new Entity(WorkerSchema.TestRunEntity, Guid.NewGuid());
        run[WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusRunning);
        run[WorkerSchema.RunChunksTotal] = chunksTotal;
        run[WorkerSchema.RunStartedOn] = T0;
        fake.Seed(run);
        return run.Id;
    }

    private static void SeedChunk(FakeDataverse fake, Guid runId, int status, string[] testIds,
        string? errorDetails = null)
    {
        var chunk = new Entity(WorkerSchema.TestChunkEntity, Guid.NewGuid());
        chunk[WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        chunk[WorkerSchema.ChunkTestIds] = JsonConvert.SerializeObject(testIds);
        chunk[WorkerSchema.ChunkStatus] = new OptionSetValue(status);
        if (errorDetails != null) chunk[WorkerSchema.ChunkErrorDetails] = errorDetails;
        fake.Seed(chunk);
    }

    private static void SeedResult(FakeDataverse fake, Guid runId, string testId, int outcome)
    {
        var r = new Entity(WorkerSchema.TestRunResultEntity, Guid.NewGuid());
        r[WorkerSchema.ResultTestRun] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        r[WorkerSchema.ResultTestId] = testId;
        r[WorkerSchema.ResultOutcome] = new OptionSetValue(outcome);
        r[WorkerSchema.ResultDuration] = 10;
        fake.Seed(r);
    }

    [Fact]
    public void FailedChunk_MissingTestsBecomeErrorResults_RunStatusError()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 2);
        SeedChunk(fake, runId, WorkerSchema.ChunkProcessed, new[] { "A" });
        SeedChunk(fake, runId, WorkerSchema.ChunkError, new[] { "B", "C" },
            errorDetails: "Hard-Timeout ab Gruppe 0");
        SeedResult(fake, runId, "A", WorkerSchema.OutcomePassed);
        SeedResult(fake, runId, "C[1]", WorkerSchema.OutcomePassed); // data-driven row of C

        var completed = new RunCompletionService(fake).TryComplete(runId, () => T0.AddMinutes(5));

        Assert.True(completed);
        var results = fake.All(WorkerSchema.TestRunResultEntity);
        Assert.Equal(3, results.Count); // A, C[1], plus the lost B -- C is not reported as lost
        var lost = Assert.Single(results, r => r.GetAttributeValue<string>(WorkerSchema.ResultTestId) == "B");
        Assert.Equal(WorkerSchema.OutcomeError, lost.GetAttributeValue<OptionSetValue>(WorkerSchema.ResultOutcome).Value);
        Assert.Contains("Hard-Timeout ab Gruppe 0", lost.GetAttributeValue<string>(WorkerSchema.ResultError));

        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusError, run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
        Assert.Equal(3, run.GetAttributeValue<int>(WorkerSchema.RunTotal));
        Assert.Equal(2, run.GetAttributeValue<int>(WorkerSchema.RunPassed));
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunFailed));
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunChunksFailed));
        Assert.StartsWith("1 Chunk aufgegeben", run.GetAttributeValue<string>(WorkerSchema.RunSummary));
    }

    [Fact]
    public void NoFailedChunk_StatusCompleted_NoExtraResults()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedChunk(fake, runId, WorkerSchema.ChunkProcessed, new[] { "A", "B" });
        SeedResult(fake, runId, "A", WorkerSchema.OutcomePassed);
        SeedResult(fake, runId, "B", WorkerSchema.OutcomeFailed);

        var completed = new RunCompletionService(fake).TryComplete(runId, () => T0.AddMinutes(5));

        Assert.True(completed);
        Assert.Equal(2, fake.All(WorkerSchema.TestRunResultEntity).Count);
        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusCompleted, run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
        Assert.Equal(2, run.GetAttributeValue<int>(WorkerSchema.RunTotal));
        Assert.DoesNotContain("aufgegeben", run.GetAttributeValue<string>(WorkerSchema.RunSummary));
    }

    [Fact]
    public void FailedChunk_AllTestsHaveResults_StatusErrorButNoSyntheticResults()
    {
        // The chunk was given up after its last group had already been persisted (possible
        // since Entscheidung 1): nothing is missing, but the run is still not regular.
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedChunk(fake, runId, WorkerSchema.ChunkError, new[] { "A" }, errorDetails: "x");
        SeedResult(fake, runId, "A", WorkerSchema.OutcomePassed);

        new RunCompletionService(fake).TryComplete(runId, () => T0.AddMinutes(5));

        Assert.Single(fake.All(WorkerSchema.TestRunResultEntity));
        Assert.Equal(WorkerSchema.StatusError, fake.Get(WorkerSchema.TestRunEntity, runId)
            .GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
    }
}
