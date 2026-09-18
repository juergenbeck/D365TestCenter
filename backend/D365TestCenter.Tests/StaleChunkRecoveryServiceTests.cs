using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für <see cref="StaleChunkRecoveryService"/> (FB-46 / OE-12). Pinnt: frischer Chunk wird
/// in Ruhe gelassen, stale Chunk wird auf "Fortsetzen" zurückgesetzt, Fallback-Anker
/// jbe_startedon, Nicht-Running-Läufe + Nicht-Läuft-Chunks ignoriert, OC-Race (Chunk inzwischen
/// geflippt) überspringt, Loop-Breaker poisont nach K Recoveries, und der FB-46-Deadlock wird
/// end-to-end aufgelöst (Reset -> Worker resumed -> Plateau).
/// </summary>
public class StaleChunkRecoveryServiceTests
{
    private static readonly DateTime T0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const int Stale = 180;
    private const int MaxRecov = 3;

    private static Guid SeedRun(FakeDataverse fake, int chunksTotal,
        int status = WorkerSchema.StatusRunning)
    {
        var run = new Entity(WorkerSchema.TestRunEntity, Guid.NewGuid());
        run[WorkerSchema.RunStatus] = new OptionSetValue(status);
        run[WorkerSchema.RunChunksTotal] = chunksTotal;
        run[WorkerSchema.RunStartedOn] = T0.AddHours(-1);
        run[WorkerSchema.RunKeepRecords] = false;
        fake.Seed(run);
        return run.Id;
    }

    private static Guid SeedRunningChunk(FakeDataverse fake, Guid runId,
        DateTime? lastClaimedOn, int recoveryCount = 0, int groupCursor = 0,
        string[]? testIds = null, DateTime? startedOn = null)
    {
        var chunk = new Entity(WorkerSchema.TestChunkEntity, Guid.NewGuid());
        chunk[WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        chunk[WorkerSchema.ChunkIndex] = 0;
        chunk[WorkerSchema.ChunkTestIds] = JsonConvert.SerializeObject(testIds ?? new[] { "TC1" });
        chunk[WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkRunning);
        chunk[WorkerSchema.ChunkGroupCursor] = groupCursor;
        chunk[WorkerSchema.ChunkRecoveryCount] = recoveryCount;
        if (lastClaimedOn.HasValue) chunk[WorkerSchema.ChunkLastClaimedOn] = lastClaimedOn.Value;
        if (startedOn.HasValue) chunk[WorkerSchema.ChunkStartedOn] = startedOn.Value;
        fake.Seed(chunk);
        return chunk.Id;
    }

    private static Guid SeedProcessedChunk(FakeDataverse fake, Guid runId)
    {
        var chunk = new Entity(WorkerSchema.TestChunkEntity, Guid.NewGuid());
        chunk[WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, runId);
        chunk[WorkerSchema.ChunkIndex] = 1;
        chunk[WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkProcessed);
        fake.Seed(chunk);
        return chunk.Id;
    }

    private static int ChunkStatusOf(FakeDataverse fake, Guid chunkId) =>
        fake.Get(WorkerSchema.TestChunkEntity, chunkId)
            .GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus).Value;

    private static int RunStatusOf(FakeDataverse fake, Guid runId) =>
        fake.Get(WorkerSchema.TestRunEntity, runId)
            .GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus).Value;

    [Fact]
    public void FreshRunningChunk_NotStale_NoReset()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-60));

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(0, r.ChunksRecovered);
        Assert.Equal(WorkerSchema.ChunkRunning, ChunkStatusOf(fake, chunkId)); // unangetastet
    }

    [Fact]
    public void StaleRunningChunk_ResetToResume_IncrementsRecoveryCount()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-300));

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(1, r.ChunksRecovered);
        Assert.Equal(0, r.ChunksPoisoned);
        Assert.Equal(WorkerSchema.ChunkResume, ChunkStatusOf(fake, chunkId));
        Assert.Equal(1, fake.Get(WorkerSchema.TestChunkEntity, chunkId)
            .GetAttributeValue<int>(WorkerSchema.ChunkRecoveryCount));
    }

    [Fact]
    public void StaleChunk_FallsBackToStartedOn_WhenLastClaimedNull()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: null,
            startedOn: T0.AddSeconds(-300));

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(1, r.ChunksRecovered);
        Assert.Equal(WorkerSchema.ChunkResume, ChunkStatusOf(fake, chunkId));
    }

    [Fact]
    public void NoAnchor_LeftAlone()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: null, startedOn: null);

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(0, r.ChunksRecovered);
        Assert.Equal(WorkerSchema.ChunkRunning, ChunkStatusOf(fake, chunkId));
    }

    [Fact]
    public void NonRunningRun_NotScanned()
    {
        var fake = new FakeDataverse();
        // Lauf in "Aufteilung läuft" mit stale Läuft-Chunk -> der Sweep scannt nur Running-Läufe.
        var runId = SeedRun(fake, 1, status: WorkerSchema.StatusSplitting);
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-300));

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(0, r.RunsScanned);
        Assert.Equal(0, r.ChunksRecovered);
        Assert.Equal(WorkerSchema.ChunkRunning, ChunkStatusOf(fake, chunkId));
    }

    [Fact]
    public void ProcessedChunk_NotTouched()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedProcessedChunk(fake, runId); // Verarbeitet, nicht Läuft

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(0, r.ChunksRecovered);
        Assert.Equal(WorkerSchema.ChunkProcessed, ChunkStatusOf(fake, chunkId));
    }

    [Fact]
    public void OcRace_ChunkFlippedBetweenReadAndReset_Skips()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-300));

        // Simuliere einen späten Worker, der den Chunk GENAU zwischen frischem Retrieve und
        // OC-Reset flippt: beim ersten Chunk-Retrieve den Store auf Verarbeitet drehen (RowVersion
        // steigt) -> der OC-Reset mit der vorher gelesenen RowVersion scheitert.
        bool bumped = false;
        fake.OnRetrieve = (logical, id) =>
        {
            if (!bumped && logical == WorkerSchema.TestChunkEntity)
            {
                bumped = true;
                fake.Update(new Entity(WorkerSchema.TestChunkEntity, id)
                {
                    [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkProcessed)
                });
            }
        };

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(0, r.ChunksRecovered);
        // Der konkurrierende Flip bleibt stehen, die Recovery hat NICHT geclobbert.
        Assert.Equal(WorkerSchema.ChunkProcessed, ChunkStatusOf(fake, chunkId));
    }

    [Fact]
    public void LoopBreaker_BelowMax_StillResets()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1);
        // recoveryCount 2, max 3 -> newCount 3 ist NICHT > 3 -> noch Reset.
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-300),
            recoveryCount: 2);

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(1, r.ChunksRecovered);
        Assert.Equal(0, r.ChunksPoisoned);
        Assert.Equal(WorkerSchema.ChunkResume, ChunkStatusOf(fake, chunkId));
        Assert.Equal(3, fake.Get(WorkerSchema.TestChunkEntity, chunkId)
            .GetAttributeValue<int>(WorkerSchema.ChunkRecoveryCount));
    }

    [Fact]
    public void LoopBreaker_ExceedsMax_Poisons_AndCompletesRunAtPlateau()
    {
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 1); // einziger Chunk -> Poison schließt das Plateau
        // recoveryCount 3, max 3 -> newCount 4 > 3 -> Poison.
        var chunkId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-300),
            recoveryCount: 3);

        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);

        Assert.Equal(0, r.ChunksRecovered);
        Assert.Equal(1, r.ChunksPoisoned);
        Assert.Equal(WorkerSchema.ChunkError, ChunkStatusOf(fake, chunkId));
        // Der Fehler-Chunk zählt zum Plateau -> Lauf abgeschlossen (kein Deadlock).
        Assert.Equal(1, r.RunsCompleted);
        Assert.Equal(WorkerSchema.StatusCompleted, RunStatusOf(fake, runId));
        Assert.Equal(1, fake.Get(WorkerSchema.TestRunEntity, runId)
            .GetAttributeValue<int>(WorkerSchema.RunChunksFailed));
    }

    [Fact]
    public void Fb46Deadlock_RecoveredEndToEnd_ResumeWorkerReachesPlateau()
    {
        // Reproduziert FB-46: ein Run mit zwei Chunks, einer Verarbeitet, einer in "Läuft"
        // eingefroren (Hard-Timeout). Ohne Recovery bleibt das Plateau (1/2) unerreicht -> Deadlock.
        var fake = new FakeDataverse();
        var runId = SeedRun(fake, 2);
        SeedProcessedChunk(fake, runId); // Chunk 1 fertig

        SeedTestCase(fake, "TC1");
        var stuckId = SeedRunningChunk(fake, runId, lastClaimedOn: T0.AddSeconds(-300),
            testIds: new[] { "TC1" }, groupCursor: 0);

        // Vorbedingung: Lauf hängt (noch Running, Plateau nicht erreicht).
        Assert.Equal(WorkerSchema.StatusRunning, RunStatusOf(fake, runId));

        // Sweep -> Stuck-Chunk auf Fortsetzen.
        var r = new StaleChunkRecoveryService(fake).Sweep(Stale, MaxRecov, () => T0);
        Assert.Equal(1, r.ChunksRecovered);
        Assert.Equal(WorkerSchema.ChunkResume, ChunkStatusOf(fake, stuckId));
        Assert.Equal(WorkerSchema.StatusRunning, RunStatusOf(fake, runId)); // noch nicht fertig

        // Der re-getriggerte Worker resumed ab group_cursor und erreicht das Plateau.
        var outcome = new ChunkWorkerOrchestrator(fake).Run(stuckId, 80, () => T0);
        Assert.Equal(ChunkWorkerOutcome.Processed, outcome);
        Assert.Equal(WorkerSchema.StatusCompleted, RunStatusOf(fake, runId)); // Deadlock aufgelöst
    }

    private static void SeedTestCase(FakeDataverse fake, string id)
    {
        var def = JsonConvert.SerializeObject(new
        {
            id,
            title = id,
            steps = new[] { new { action = "Wait", waitSeconds = 0 } }
        });
        var tc = new Entity(WorkerSchema.TestCaseEntity, Guid.NewGuid());
        tc[WorkerSchema.TcTestId] = id;
        tc[WorkerSchema.TcTitle] = id;
        tc[WorkerSchema.TcDefinition] = def;
        tc[WorkerSchema.TcEnabled] = true;
        fake.Seed(tc);
    }
}
