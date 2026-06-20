using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer <see cref="CoordinatorOrchestrator"/> (ADR-0009 Phase 2). Pinnt: Trigger-Status-Gate,
/// Fan-Out (Gruppen -> Chunks), Abhaengigkeits-Affinitaet (Gruppe bleibt in einem Chunk),
/// H2 (DeleteOldResults genau auf der ersten Welle), Re-Run loescht alte Chunks, chunks_total frueh,
/// Watchdog-Continuation mit Cursor-Persistenz, Continuation ohne erneutes Loeschen.
/// </summary>
public class CoordinatorOrchestratorTests
{
    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Func<DateTime> SeqClock(params DateTime[] times)
    {
        int i = 0;
        return () => times[Math.Min(i++, times.Length - 1)];
    }

    private static Guid SeedRun(FakeDataverse fake, int status, int cursor = 0,
        string filter = "*", int? chunkSize = null)
    {
        var run = new Entity(WorkerSchema.TestRunEntity, Guid.NewGuid());
        run[WorkerSchema.RunStatus] = new OptionSetValue(status);
        run[WorkerSchema.RunFilter] = filter;
        run[WorkerSchema.RunCoordinatorCursor] = cursor;
        if (chunkSize.HasValue) run[WorkerSchema.RunChunkSize] = chunkSize.Value;
        fake.Seed(run);
        return run.Id;
    }

    private static void SeedTestCase(FakeDataverse fake, string id,
        string[]? dependsOn = null, bool enabled = true)
    {
        var def = JsonConvert.SerializeObject(new
        {
            id,
            title = id,
            dependsOn,
            steps = new[] { new { action = "Wait", waitSeconds = 0 } }
        });
        var tc = new Entity(WorkerSchema.TestCaseEntity, Guid.NewGuid());
        tc[WorkerSchema.TcTestId] = id;
        tc[WorkerSchema.TcTitle] = id;
        tc[WorkerSchema.TcDefinition] = def;
        tc[WorkerSchema.TcEnabled] = enabled;
        fake.Seed(tc);
    }

    private static List<Entity> Chunks(FakeDataverse fake) =>
        fake.All(WorkerSchema.TestChunkEntity)
            .OrderBy(c => c.GetAttributeValue<int>(WorkerSchema.ChunkIndex))
            .ToList();

    private static List<string> ChunkIds(Entity chunk) =>
        JArray.Parse(chunk.GetAttributeValue<string>(WorkerSchema.ChunkTestIds))
            .Select(t => t.ToString()).ToList();

    private static int RunStatusOf(FakeDataverse fake, Guid runId) =>
        fake.Get(WorkerSchema.TestRunEntity, runId)
            .GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value;

    [Fact]
    public void NonPlannedStatus_Skips()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, WorkerSchema.StatusRunning);

        var outcome = new CoordinatorOrchestrator(fake).Run(runId, 5, 80, SeqClock(T0));

        Assert.Equal(CoordinatorOutcome.Skipped, outcome);
        Assert.Empty(fake.All(WorkerSchema.TestChunkEntity));
    }

    [Fact]
    public void FirstFire_IndependentTests_OneChunk_RunningWithTotal()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned, chunkSize: 5);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        SeedTestCase(fake, "TC3");

        var outcome = new CoordinatorOrchestrator(fake).Run(runId, 5, 80, SeqClock(T0));

        Assert.Equal(CoordinatorOutcome.ChunksCreated, outcome);
        var chunks = Chunks(fake);
        Assert.Single(chunks);
        Assert.Equal(new[] { "TC1", "TC2", "TC3" }, ChunkIds(chunks[0]).OrderBy(x => x));
        Assert.Equal(WorkerSchema.ChunkNew,
            chunks[0].GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus).Value);

        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusRunning,
            run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunChunksTotal));
        Assert.Equal(0, run.GetAttributeValue<int>(WorkerSchema.RunCoordinatorCursor));
    }

    [Fact]
    public void NoTests_CompletesRun()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned);

        var outcome = new CoordinatorOrchestrator(fake).Run(runId, 5, 80, SeqClock(T0));

        Assert.Equal(CoordinatorOutcome.NoTests, outcome);
        Assert.Equal(WorkerSchema.StatusCompleted, RunStatusOf(fake, runId));
        Assert.Empty(fake.All(WorkerSchema.TestChunkEntity));
    }

    [Fact]
    public void DependentTests_StayInSameChunk()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned, chunkSize: 5);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2", dependsOn: new[] { "TC1" }); // Gruppe [TC1, TC2]
        SeedTestCase(fake, "TC3");

        new CoordinatorOrchestrator(fake).Run(runId, 5, 80, SeqClock(T0));

        // Eine Gruppe darf NICHT ueber Chunks getrennt werden; bei chunkSize 5 passt alles in 1 Chunk.
        var chunks = Chunks(fake);
        Assert.Single(chunks);
        var ids = ChunkIds(chunks[0]);
        // TC1 und TC2 muessen im selben Chunk sein.
        Assert.Contains("TC1", ids);
        Assert.Contains("TC2", ids);
    }

    [Fact]
    public void Watchdog_BudgetExceeded_PersistsCursor_Continues()
    {
        var fake = new FakeDataverse();
        // chunkSize 1 -> 4 unabhaengige Tests = 4 Chunks.
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned, chunkSize: 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        SeedTestCase(fake, "TC3");
        SeedTestCase(fake, "TC4");

        // startTime=T0; Check vor Chunk 1 -> T0+100 (>=80) -> stop nach genau 1 Create.
        var outcome = new CoordinatorOrchestrator(fake)
            .Run(runId, 1, 80, SeqClock(T0, T0.AddSeconds(100)));

        Assert.Equal(CoordinatorOutcome.Continued, outcome);
        Assert.Single(fake.All(WorkerSchema.TestChunkEntity)); // nur 1 Chunk diese Welle

        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusPlanned,
            run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value); // Self-Trigger
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunCoordinatorCursor));
        Assert.Equal(4, run.GetAttributeValue<int>(WorkerSchema.RunChunksTotal)); // frueh gesetzt
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunContinuations));
    }

    [Fact]
    public void Continuation_CreatesRemaining_DoesNotDeleteExistingResults()
    {
        var fake = new FakeDataverse();
        // 4 Tests, chunkSize 1 -> 4 Chunks. Continuation ab Cursor 1.
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned, cursor: 1, chunkSize: 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        SeedTestCase(fake, "TC3");
        SeedTestCase(fake, "TC4");

        // Ein bereits geschriebenes Result (frische Welle) darf NICHT geloescht werden.
        var keep = new Entity(WorkerSchema.TestRunResultEntity, Guid.NewGuid());
        keep[WorkerSchema.ResultTestRun] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        keep[WorkerSchema.ResultTestId] = "TC1";
        fake.Seed(keep);

        var outcome = new CoordinatorOrchestrator(fake).Run(runId, 1, 1000, SeqClock(T0));

        Assert.Equal(CoordinatorOutcome.ChunksCreated, outcome);
        // Chunks 1,2,3 neu angelegt (Chunk 0 lief in der Vorwelle, hier nicht geseedet).
        Assert.Equal(3, fake.CountCreated(WorkerSchema.TestChunkEntity));
        // Das bestehende Result lebt noch (kein H2-Delete auf Continuation).
        Assert.True(fake.Exists(WorkerSchema.TestRunResultEntity, keep.Id));
    }

    [Fact]
    public void FirstFire_DeletesOldResultsAndChunks()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned, chunkSize: 5);
        SeedTestCase(fake, "TC1");

        // Alt-Bestand aus einem frueheren Lauf.
        var oldResult = new Entity(WorkerSchema.TestRunResultEntity, Guid.NewGuid());
        oldResult[WorkerSchema.ResultTestRun] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        oldResult[WorkerSchema.ResultTestId] = "OLD";
        fake.Seed(oldResult);

        var oldStep = new Entity(WorkerSchema.TestStepEntity, Guid.NewGuid());
        oldStep[WorkerSchema.StepRunResult] = new EntityReference(WorkerSchema.TestRunResultEntity, oldResult.Id);
        fake.Seed(oldStep);

        var oldChunk = new Entity(WorkerSchema.TestChunkEntity, Guid.NewGuid());
        oldChunk[WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        oldChunk[WorkerSchema.ChunkIndex] = 0;
        oldChunk[WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkProcessed);
        fake.Seed(oldChunk);

        new CoordinatorOrchestrator(fake).Run(runId, 5, 80, SeqClock(T0));

        Assert.False(fake.Exists(WorkerSchema.TestRunResultEntity, oldResult.Id));
        Assert.False(fake.Exists(WorkerSchema.TestStepEntity, oldStep.Id));
        Assert.False(fake.Exists(WorkerSchema.TestChunkEntity, oldChunk.Id));
        // Genau ein frischer Chunk (fuer TC1).
        Assert.Equal(1, fake.CountCreated(WorkerSchema.TestChunkEntity));
    }

    [Fact]
    public void SpuriousFireWhileRunning_Skips()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, WorkerSchema.StatusPlanned, chunkSize: 5);
        SeedTestCase(fake, "TC1");

        var coord = new CoordinatorOrchestrator(fake);
        coord.Run(runId, 5, 80, SeqClock(T0)); // -> Running
        // Der Status-Flip-Self-Fire (jetzt Running) darf nichts mehr tun.
        var second = coord.Run(runId, 5, 80, SeqClock(T0));

        Assert.Equal(CoordinatorOutcome.Skipped, second);
        Assert.Equal(1, fake.CountCreated(WorkerSchema.TestChunkEntity)); // kein zweiter Chunk
    }
}
