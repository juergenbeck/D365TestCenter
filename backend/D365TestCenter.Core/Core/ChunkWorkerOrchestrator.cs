using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;

namespace D365TestCenter.Core;

/// <summary>Ergebnis eines Worker-Laufs (fuer Tests + Trace).</summary>
public enum ChunkWorkerOutcome
{
    /// <summary>Kein Trigger-Status (Laeuft/Verarbeitet/Fehler-Fire) oder OC-Claim verloren.</summary>
    Skipped,
    /// <summary>Kaputtes jbe_testids-JSON -> Chunk auf Fehler, kein Re-Trigger (C-07).</summary>
    Poison,
    /// <summary>Budget gerissen: Gruppen-Cursor persistiert, Status Fortsetzen (Self-Trigger).</summary>
    Continued,
    /// <summary>Alle Gruppen des Chunks fertig -> Chunk Verarbeitet (ggf. Lauf-Plateau).</summary>
    Processed
}

/// <summary>
/// Testbarer Kern des RunChunkWorker-Plugins (ADR-0009 Phase 3, async auf <c>jbe_testchunk</c>).
/// Fuehrt die Tests eines Chunks ab dem Gruppen-Cursor aus und schreibt die Ergebnisse idempotent.
///
/// Eigenschaften:
///   - <b>OC-Claim + Trigger-Guard (A-02/C-03):</b> Pickup Neu/Fortsetzen -> Laeuft per
///     IfRowVersionMatches; der Doppel-Fire-Verlierer skippt sauber; nur beim Trigger-Wert handeln.
///   - <b>Poison-Chunk (C-07):</b> kaputtes <c>jbe_testids</c>-JSON -> Status Fehler, KEIN Re-Trigger.
///   - <b>Gruppen-Grenzen-Continuation (Befund 3):</b> <see cref="TestRunner.RunGroupsBudgeted"/> ab
///     <c>jbe_group_cursor</c>; eine frische Worker-Instanz baut den dependsOn-Zustand gruppenintern
///     selbst auf (kein Re-Seed).
///   - <b>H1 Reihenfolge-Invariante:</b> erst ALLE Result-Upserts committen, dann Cursor + Status-Flip
///     -- sonst liest ein Re-Fire einen unvollstaendigen Zustand.
///   - <b>H3 idempotenter Upsert</b> ueber <see cref="ChunkResultWriter"/> (Alternate Key).
///   - <b>Chunk-eigene Zaehler</b> (kein Shared-Counter-OC); Run-Aggregat erst am Plateau
///     (<see cref="RunCompletionService"/>, C-04 Completion-Guard).
/// </summary>
public sealed class ChunkWorkerOrchestrator
{
    private readonly IOrganizationService _service;
    private readonly Action<string>? _log;

    public ChunkWorkerOrchestrator(IOrganizationService service, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _log = log;
    }

    public ChunkWorkerOutcome Run(Guid chunkId, int budgetSeconds, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        var startTime = now();

        var chunk = _service.Retrieve(WorkerSchema.TestChunkEntity, chunkId, new ColumnSet(
            WorkerSchema.ChunkStatus, WorkerSchema.ChunkTestIds, WorkerSchema.ChunkGroupCursor,
            WorkerSchema.ChunkTestRunId, WorkerSchema.ChunkProcessedCount, WorkerSchema.ChunkFailedCount,
            WorkerSchema.ChunkContinuations, WorkerSchema.ChunkStartedOn));

        var status = chunk.GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus)?.Value;
        if (status != WorkerSchema.ChunkNew && status != WorkerSchema.ChunkResume)
        {
            _log?.Invoke($"Worker: Chunk-Status {status} ist kein Trigger (Neu/Fortsetzen) -> skip.");
            return ChunkWorkerOutcome.Skipped;
        }

        var testRunRef = chunk.GetAttributeValue<EntityReference>(WorkerSchema.ChunkTestRunId);
        if (testRunRef == null)
        {
            _log?.Invoke("Worker: Chunk ohne jbe_testrunid -> skip.");
            return ChunkWorkerOutcome.Skipped;
        }
        var testRunId = testRunRef.Id;
        var isFirstPickup = status == WorkerSchema.ChunkNew;
        var chunkStartedOn = chunk.GetAttributeValue<DateTime?>(WorkerSchema.ChunkStartedOn) ?? startTime;

        // ── OC-Claim: Neu/Fortsetzen -> Laeuft ──────────────────────
        var claim = new Entity(WorkerSchema.TestChunkEntity, chunkId)
        {
            [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkRunning),
            // Stale-Anker (FB-46/OE-12): bei JEDEM Claim (erster Pickup + Resume) gesetzt, damit die
            // Recovery "wie lange in Laeuft" messen kann -- jbe_startedon (erster Start) taugt nicht,
            // ein gesund mehrwelliger Chunk waere sonst faelschlich stale.
            [WorkerSchema.ChunkLastClaimedOn] = startTime
        };
        if (isFirstPickup) claim[WorkerSchema.ChunkStartedOn] = startTime;
        claim.RowVersion = chunk.RowVersion;
        try
        {
            _service.Execute(new UpdateRequest
            {
                Target = claim,
                ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches
            });
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            _log?.Invoke("Worker: OC-Claim verloren (Doppel-Fire) -> skip.");
            return ChunkWorkerOutcome.Skipped;
        }

        // ── Poison-Chunk-Guard (C-07): jbe_testids parsen ───────────
        List<string> ids;
        try
        {
            ids = JsonConvert.DeserializeObject<List<string>>(
                chunk.GetAttributeValue<string>(WorkerSchema.ChunkTestIds) ?? "")
                ?? new List<string>();
        }
        catch (Exception ex)
        {
            SetChunkError(chunkId, $"Kaputtes jbe_testids-JSON: {ex.Message}");
            // Ein Fehler-Chunk zaehlt zum Plateau (done+failed == total) -> Lauf-Abschluss pruefen.
            new RunCompletionService(_service, _log).TryComplete(testRunId, now);
            _log?.Invoke("Worker: Poison-Chunk -> Status Fehler, kein Re-Trigger.");
            return ChunkWorkerOutcome.Poison;
        }

        // ── Definitionen laden, Gruppen (re-)derivieren ─────────────
        var tests = TestCaseLoader.LoadByIds(_service, ids, _log);
        var groups = TestRunner.BuildDependencyGroups(tests);
        var groupCursor = chunk.GetAttributeValue<int?>(WorkerSchema.ChunkGroupCursor) ?? 0;

        var keepRecords = ReadKeepRecords(testRunId);
        var runner = new TestRunner(_service)
        {
            KeepRecords = keepRecords,
            // B.3 / OE-10: der async Worker unterliegt der Sandbox-Waechter-Regel nicht und darf
            // die Primary-Namen der angelegten Records per try/catch-Retrieve erfassen.
            CaptureRecordNames = true
        };

        var budget = runner.RunGroupsBudgeted(groups, groupCursor, budgetSeconds, now);
        var wave = budget.Run;

        // ── H1: ERST alle Result-Upserts committen ──────────────────
        var writer = new ChunkResultWriter(_service, _log);
        foreach (var tc in wave.Results)
            writer.UpsertResult(testRunId, tc);

        // ── DANN Chunk-Zaehler + Cursor + Status-Flip ───────────────
        var prevProcessed = chunk.GetAttributeValue<int?>(WorkerSchema.ChunkProcessedCount) ?? 0;
        var prevFailed = chunk.GetAttributeValue<int?>(WorkerSchema.ChunkFailedCount) ?? 0;
        var prevContinuations = chunk.GetAttributeValue<int?>(WorkerSchema.ChunkContinuations) ?? 0;
        var waveProcessed = wave.Results.Count;
        var waveFailed = wave.Results.Count(r =>
            r.Outcome == TestOutcome.Failed || r.Outcome == TestOutcome.Error);

        if (budget.Done)
        {
            var completedOn = now();
            var update = new Entity(WorkerSchema.TestChunkEntity, chunkId)
            {
                [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkProcessed),
                [WorkerSchema.ChunkGroupCursor] = budget.NextGroupIndex,
                [WorkerSchema.ChunkProcessedCount] = prevProcessed + waveProcessed,
                [WorkerSchema.ChunkFailedCount] = prevFailed + waveFailed,
                [WorkerSchema.ChunkCompletedOn] = completedOn,
                [WorkerSchema.ChunkDurationMs] =
                    (int)Math.Max(0, (completedOn - chunkStartedOn).TotalMilliseconds),
                // Fortschritt -> Loop-Breaker-Zaehler zuruecksetzen (OE-12): nur wiederholte
                // Recoveries OHNE Fortschritt sollen Richtung Poison akkumulieren.
                [WorkerSchema.ChunkRecoveryCount] = 0
            };
            _service.Update(update);
            _log?.Invoke($"Worker: Chunk verarbeitet ({prevProcessed + waveProcessed} Test(s), " +
                         $"{prevFailed + waveFailed} fehlgeschlagen).");

            // Plateau: wenn dies der letzte fertige Chunk war, Lauf abschliessen (Completion-Guard).
            new RunCompletionService(_service, _log).TryComplete(testRunId, now);
            return ChunkWorkerOutcome.Processed;
        }

        // Budget gerissen -> Self-Trigger (Fortsetzen). Mindestens eine Gruppe lief (Budget-Check
        // vor jeder Gruppe) -> Fortschritt -> Loop-Breaker-Zaehler zuruecksetzen (OE-12).
        _service.Update(new Entity(WorkerSchema.TestChunkEntity, chunkId)
        {
            [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkResume),
            [WorkerSchema.ChunkGroupCursor] = budget.NextGroupIndex,
            [WorkerSchema.ChunkProcessedCount] = prevProcessed + waveProcessed,
            [WorkerSchema.ChunkFailedCount] = prevFailed + waveFailed,
            [WorkerSchema.ChunkContinuations] = prevContinuations + 1,
            [WorkerSchema.ChunkRecoveryCount] = 0
        });
        _log?.Invoke($"Worker: Budget {budgetSeconds}s gerissen bei Gruppe {budget.NextGroupIndex} " +
                     "-> Cursor persistiert, Self-Trigger (Fortsetzen).");
        return ChunkWorkerOutcome.Continued;
    }

    private bool ReadKeepRecords(Guid testRunId)
    {
        try
        {
            var run = _service.Retrieve(WorkerSchema.TestRunEntity, testRunId,
                new ColumnSet(WorkerSchema.RunKeepRecords));
            return run.GetAttributeValue<bool>(WorkerSchema.RunKeepRecords);
        }
        catch
        {
            return false;
        }
    }

    private void SetChunkError(Guid chunkId, string message)
    {
        try
        {
            _service.Update(new Entity(WorkerSchema.TestChunkEntity, chunkId)
            {
                [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkError),
                [WorkerSchema.ChunkErrorDetails] = Truncate(message, 100000),
                [WorkerSchema.ChunkCompletedOn] = DateTime.UtcNow
            });
        }
        catch
        {
            /* non-critical */
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value!.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
