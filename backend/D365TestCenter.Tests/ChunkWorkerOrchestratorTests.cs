using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer <see cref="ChunkWorkerOrchestrator"/> (ADR-0009 Phase 3). Pinnt: Trigger-Gate,
/// OC-Claim (Doppel-Fire-Verlierer skippt), Poison-Chunk (Status Fehler, kein Re-Trigger),
/// Ausfuehrung + idempotente Result-Schreibung, Chunk-Zaehler, Budget-Continuation (Self-Trigger,
/// H1: Results VOR Cursor), Resume ab Gruppen-Cursor, Plateau-Abschluss genau am letzten Chunk.
/// </summary>
public class ChunkWorkerOrchestratorTests
{
    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Func<DateTime> SeqClock(params DateTime[] times)
    {
        int i = 0;
        return () => times[Math.Min(i++, times.Length - 1)];
    }

    private static Guid SeedRun(FakeDataverse fake, int chunksTotal, int status = WorkerSchema.StatusRunning)
    {
        var run = new Entity(WorkerSchema.TestRunEntity, Guid.NewGuid());
        run[WorkerSchema.RunStatus] = new OptionSetValue(status);
        run[WorkerSchema.RunChunksTotal] = chunksTotal;
        run[WorkerSchema.RunStartedOn] = T0;
        run[WorkerSchema.RunKeepRecords] = false;
        fake.Seed(run);
        return run.Id;
    }

    private static Guid SeedChunk(FakeDataverse fake, Guid runId, int index, string[] testIds,
        int status = WorkerSchema.ChunkNew, int groupCursor = 0, int processed = 0)
    {
        var chunk = new Entity(WorkerSchema.TestChunkEntity, Guid.NewGuid());
        chunk[WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        chunk[WorkerSchema.ChunkIndex] = index;
        chunk[WorkerSchema.ChunkTestIds] = JsonConvert.SerializeObject(testIds);
        chunk[WorkerSchema.ChunkStatus] = new OptionSetValue(status);
        chunk[WorkerSchema.ChunkGroupCursor] = groupCursor;
        chunk[WorkerSchema.ChunkProcessedCount] = processed;
        chunk[WorkerSchema.ChunkFailedCount] = 0;
        chunk[WorkerSchema.ChunkContinuations] = 0;
        fake.Seed(chunk);
        return chunk.Id;
    }

    private static void SeedTestCase(FakeDataverse fake, string id, string[]? dependsOn = null)
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
        tc[WorkerSchema.TcEnabled] = true;
        fake.Seed(tc);
    }

    private static int ChunkStatusOf(FakeDataverse fake, Guid chunkId) =>
        fake.Get(WorkerSchema.TestChunkEntity, chunkId)
            .GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus).Value;

    private static int ResultCount(FakeDataverse fake) =>
        fake.All(WorkerSchema.TestRunResultEntity).Count;

    [Fact]
    public void NonTriggerStatus_Skips()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1" }, status: WorkerSchema.ChunkRunning);

        var outcome = new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        Assert.Equal(ChunkWorkerOutcome.Skipped, outcome);
        Assert.Equal(0, ResultCount(fake));
    }

    [Fact]
    public void NewChunk_RunsTests_WritesResults_Processed()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1", "TC2" });

        var outcome = new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        Assert.Equal(ChunkWorkerOutcome.Processed, outcome);
        Assert.Equal(WorkerSchema.ChunkProcessed, ChunkStatusOf(fake, chunkId));
        Assert.Equal(2, ResultCount(fake));

        var chunk = fake.Get(WorkerSchema.TestChunkEntity, chunkId);
        Assert.Equal(2, chunk.GetAttributeValue<int>(WorkerSchema.ChunkProcessedCount));
        Assert.Equal(0, chunk.GetAttributeValue<int>(WorkerSchema.ChunkFailedCount));
    }

    [Fact]
    public void LastChunkProcessed_CompletesRun_WithAggregate()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1", "TC2" });

        new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusCompleted,
            run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
        Assert.Equal(2, run.GetAttributeValue<int>(WorkerSchema.RunTotal));
        Assert.Equal(2, run.GetAttributeValue<int>(WorkerSchema.RunPassed));
        Assert.Equal(0, run.GetAttributeValue<int>(WorkerSchema.RunFailed));
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunChunksDone));
    }

    [Fact]
    public void NotLastChunk_DoesNotCompleteRun()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 2); // zwei Chunks erwartet
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        var chunk0 = SeedChunk(fake, runId, 0, new[] { "TC1" });
        SeedChunk(fake, runId, 1, new[] { "TC2" }); // bleibt Neu

        new ChunkWorkerOrchestrator(fake).Run(chunk0, 80, SeqClock(T0));

        Assert.Equal(WorkerSchema.ChunkProcessed, ChunkStatusOf(fake, chunk0));
        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusRunning,
            run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value); // noch nicht fertig
    }

    [Fact]
    public void PoisonChunk_BadJson_SetsError_NoReTrigger()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunk = new Entity(WorkerSchema.TestChunkEntity, Guid.NewGuid());
        chunk[WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        chunk[WorkerSchema.ChunkIndex] = 0;
        chunk[WorkerSchema.ChunkTestIds] = "{kaputt nicht json";
        chunk[WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkNew);
        fake.Seed(chunk);

        var outcome = new ChunkWorkerOrchestrator(fake).Run(chunk.Id, 80, SeqClock(T0));

        Assert.Equal(ChunkWorkerOutcome.Poison, outcome);
        Assert.Equal(WorkerSchema.ChunkError, ChunkStatusOf(fake, chunk.Id));
        Assert.Equal(0, ResultCount(fake));
        // Ein Fehler-Chunk zaehlt zum Plateau -> Lauf wird (mit 0 Tests) abgeschlossen.
        var run = fake.Get(WorkerSchema.TestRunEntity, runId);
        Assert.Equal(WorkerSchema.StatusCompleted,
            run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
        Assert.Equal(1, run.GetAttributeValue<int>(WorkerSchema.RunChunksFailed));
    }

    [Fact]
    public void BudgetExceeded_PersistsGroupCursor_ResumeSelfTrigger_ResultsFirst()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2"); // unabhaengig -> 2 Gruppen
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1", "TC2" });

        // Worker-startTime=T0; RunGroupsBudgeted-startTime=T0; Check vor Gruppe 2 -> T0+100 (>=80) -> stop.
        var outcome = new ChunkWorkerOrchestrator(fake)
            .Run(chunkId, 80, SeqClock(T0, T0, T0.AddSeconds(100)));

        Assert.Equal(ChunkWorkerOutcome.Continued, outcome);
        Assert.Equal(WorkerSchema.ChunkResume, ChunkStatusOf(fake, chunkId));

        var chunk = fake.Get(WorkerSchema.TestChunkEntity, chunkId);
        Assert.Equal(1, chunk.GetAttributeValue<int>(WorkerSchema.ChunkGroupCursor)); // naechste Gruppe
        Assert.Equal(1, chunk.GetAttributeValue<int>(WorkerSchema.ChunkProcessedCount));
        Assert.Equal(1, chunk.GetAttributeValue<int>(WorkerSchema.ChunkContinuations));
        // H1: das Result der gelaufenen Gruppe ist VOR dem Cursor-Flip committet (genau 1 da).
        Assert.Equal(1, ResultCount(fake));
        // Lauf noch nicht abgeschlossen (Chunk nicht Verarbeitet).
        Assert.Equal(WorkerSchema.StatusRunning,
            fake.Get(WorkerSchema.TestRunEntity, runId)
                .GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value);
    }

    [Fact]
    public void ResumePickup_ContinuesFromGroupCursor_AccumulatesCounters()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2");
        // Vorwelle hat Gruppe 0 (TC1) gelaufen: Cursor 1, processedcount 1, ein TC1-Result existiert.
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1", "TC2" },
            status: WorkerSchema.ChunkResume, groupCursor: 1, processed: 1);
        var prior = new Entity(WorkerSchema.TestRunResultEntity, Guid.NewGuid());
        prior[WorkerSchema.ResultTestRun] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        prior[WorkerSchema.ResultTestId] = "TC1";
        prior[WorkerSchema.ResultOutcome] = new OptionSetValue(WorkerSchema.OutcomePassed);
        prior[WorkerSchema.ResultDuration] = 5;
        fake.Seed(prior);

        var outcome = new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        Assert.Equal(ChunkWorkerOutcome.Processed, outcome);
        var chunk = fake.Get(WorkerSchema.TestChunkEntity, chunkId);
        Assert.Equal(2, chunk.GetAttributeValue<int>(WorkerSchema.ChunkProcessedCount)); // 1 + 1
        // TC2 jetzt zusaetzlich -> 2 Result-Zeilen insgesamt.
        Assert.Equal(2, ResultCount(fake));
    }

    [Fact]
    public void Claim_SetsLastClaimedOn_StaleAnchor()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1" });

        new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        // Der Stale-Anker (FB-46/OE-12) wird beim OC-Claim gesetzt.
        var chunk = fake.Get(WorkerSchema.TestChunkEntity, chunkId);
        Assert.Equal(T0, chunk.GetAttributeValue<DateTime>(WorkerSchema.ChunkLastClaimedOn));
    }

    [Fact]
    public void GracefulProcessed_ResetsRecoveryCount()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1" });
        // Eine vorangegangene Recovery hatte den Zaehler erhoeht.
        fake.Update(new Entity(WorkerSchema.TestChunkEntity, chunkId)
        {
            [WorkerSchema.ChunkRecoveryCount] = 2
        });

        new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        // Fortschritt -> Loop-Breaker-Zaehler zurueckgesetzt.
        Assert.Equal(0, fake.Get(WorkerSchema.TestChunkEntity, chunkId)
            .GetAttributeValue<int>(WorkerSchema.ChunkRecoveryCount));
    }

    [Fact]
    public void BudgetContinuation_ResetsRecoveryCount()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        SeedTestCase(fake, "TC2"); // 2 Gruppen
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1", "TC2" });
        fake.Update(new Entity(WorkerSchema.TestChunkEntity, chunkId)
        {
            [WorkerSchema.ChunkRecoveryCount] = 2
        });

        var outcome = new ChunkWorkerOrchestrator(fake)
            .Run(chunkId, 80, SeqClock(T0, T0, T0.AddSeconds(100)));

        Assert.Equal(ChunkWorkerOutcome.Continued, outcome);
        // Auch eine graceful Continuation ist Fortschritt -> Zaehler zurueckgesetzt.
        Assert.Equal(0, fake.Get(WorkerSchema.TestChunkEntity, chunkId)
            .GetAttributeValue<int>(WorkerSchema.ChunkRecoveryCount));
    }

    [Fact]
    public void OcClaimLost_DoubleFireLoser_Skips()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        SeedTestCase(fake, "TC1");
        var chunkId = SeedChunk(fake, runId, 0, new[] { "TC1" });

        // Simuliere einen konkurrierenden Gewinner: direkt nach dem Retrieve des Chunks uebernimmt
        // ein anderer Fire den Chunk (RowVersion steigt) -> dieser Claim mit veralteter RowVersion scheitert.
        bool bumped = false;
        fake.OnRetrieve = (logical, id) =>
        {
            if (!bumped && logical == WorkerSchema.TestChunkEntity)
            {
                bumped = true;
                fake.Update(new Entity(WorkerSchema.TestChunkEntity, id)
                {
                    [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkRunning)
                });
            }
        };

        var outcome = new ChunkWorkerOrchestrator(fake).Run(chunkId, 80, SeqClock(T0));

        Assert.Equal(ChunkWorkerOutcome.Skipped, outcome);
        Assert.Equal(0, ResultCount(fake)); // Verlierer hat nichts geschrieben
    }
}
