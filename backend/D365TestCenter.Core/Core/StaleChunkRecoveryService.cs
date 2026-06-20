using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>Ergebnis eines Recovery-Sweeps (fuer die Custom API + Trace).</summary>
public sealed class RecoverySweepResult
{
    /// <summary>Anzahl Laeufe in Status "Laeuft" (Running), die gescannt wurden.</summary>
    public int RunsScanned { get; set; }
    /// <summary>Anzahl Chunks, die von "Laeuft" auf "Fortsetzen" zurueckgesetzt wurden.</summary>
    public int ChunksRecovered { get; set; }
    /// <summary>Anzahl Chunks, die wegen ueberschrittener Recovery-Obergrenze auf "Fehler" gesetzt wurden.</summary>
    public int ChunksPoisoned { get; set; }
    /// <summary>Anzahl Laeufe, die dieser Sweep am Plateau abgeschlossen hat.</summary>
    public int RunsCompleted { get; set; }

    public override string ToString() =>
        $"runs={RunsScanned}, recovered={ChunksRecovered}, poisoned={ChunksPoisoned}, completed={RunsCompleted}";
}

/// <summary>
/// Stale-"Laeuft"-Chunk-Recovery (FB-46 / OE-12). Testbarer Kern der Custom API
/// <c>jbe_RecoverStaleChunks</c>, die ein Power-Automate-Recurrence-Flow taktet (ADR-0009
/// Takt-Option b, hier gezielt fuer Recovery).
///
/// Problem: reisst eine Worker-Welle das harte 120-s-Sandbox-Limit, wird der Async-Worker gekillt,
/// NACHDEM er den Chunk per OC-Claim auf "Laeuft" gesetzt hat, aber BEVOR er flippt. Ein eingefrorener
/// Chunk bekommt nie wieder ein Trigger-Event -> das Run-Plateau wird nie erreicht -> Deadlock.
///
/// Loesung: ein wiederkehrender Sweep findet Chunks, die laenger als <c>staleSeconds</c> in "Laeuft"
/// stehen (provabel tot: ein lebender Worker haelt "Laeuft" hoechstens 120 s) und setzt sie auf
/// "Fortsetzen" zurueck -- der Worker resumed ab <c>jbe_group_cursor</c> (Gruppen-Grenzen-Continuation,
/// idempotente Results -> ein atomarer Gruppen-Neustart ist folgenlos).
///
/// Eigenschaften:
///   - <b>Stale-Anker:</b> <c>jbe_lastclaimedon</c> (vom Worker bei jedem OC-Claim gesetzt), Fallback
///     <c>jbe_startedon</c> fuer Chunks, die vor dem Feld eingefroren sind.
///   - <b>Re-Read + OC-Reset (IfRowVersionMatches):</b> direkt vor dem Reset wird der Chunk frisch
///     gelesen (frische RowVersion, Status re-bestaetigt); aenderte sich der Chunk doch noch (ein
///     spaeter Worker hat geflippt), wird uebersprungen -- kein Clobbern.
///   - <b>Loop-Breaker (<c>jbe_recoverycount</c>):</b> der Worker setzt ihn bei Fortschritt auf 0; die
///     Recovery erhoeht ihn. Ueber <c>maxRecoveries</c> -> Chunk auf "Fehler" statt Endlos-Resume
///     (irreduzibler Rest: ein Einzeltest > Wellen-Budget, ADR Entscheidung 7 vertagt). Der
///     Fehler-Chunk zaehlt zum Plateau -> der Lauf schliesst sauber ab statt zu deadlocken.
///   - <b>Plateau:</b> je gescanntem Lauf wird <see cref="RunCompletionService"/> aufgerufen -- ein
///     Poison-Chunk kann das Plateau ausloesen, und ein zuvor verpasster Abschluss wird nachgeholt.
/// </summary>
public sealed class StaleChunkRecoveryService
{
    private readonly IOrganizationService _service;
    private readonly Action<string>? _log;

    public StaleChunkRecoveryService(IOrganizationService service, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _log = log;
    }

    /// <summary>
    /// Sweept alle Laeufe in Status "Laeuft" (Running) und recovert deren stale "Laeuft"-Chunks.
    /// </summary>
    public RecoverySweepResult Sweep(int staleSeconds, int maxRecoveries, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        var result = new RecoverySweepResult();

        var runIds = LoadRunningRunIds();
        result.RunsScanned = runIds.Count;

        foreach (var runId in runIds)
        {
            var (recovered, poisoned) = RecoverRun(runId, staleSeconds, maxRecoveries, now);
            result.ChunksRecovered += recovered;
            result.ChunksPoisoned += poisoned;

            // Je Lauf das Plateau pruefen: ein Poison-Chunk kann es ausloesen, und ein zuvor
            // verpasster Abschluss (Race/Fehler in einem frueheren Sweep) wird nachgeholt.
            if (new RunCompletionService(_service, _log).TryComplete(runId, now))
                result.RunsCompleted++;
        }

        _log?.Invoke($"Stale-Chunk-Recovery: {result}");
        return result;
    }

    private (int recovered, int poisoned) RecoverRun(
        Guid runId, int staleSeconds, int maxRecoveries, Func<DateTime> now)
    {
        int recovered = 0, poisoned = 0;

        foreach (var candidate in LoadRunningChunks(runId))
        {
            var anchor = candidate.GetAttributeValue<DateTime?>(WorkerSchema.ChunkLastClaimedOn)
                         ?? candidate.GetAttributeValue<DateTime?>(WorkerSchema.ChunkStartedOn);
            if (anchor == null)
                continue; // kein Zeit-Anker -> nicht messbar, in Ruhe lassen
            if ((now() - anchor.Value).TotalSeconds <= staleSeconds)
                continue; // noch frisch -> ein lebender Worker haelt den Claim

            // Frische Sicht + RowVersion direkt vor dem OC-Reset holen.
            Entity fresh;
            try
            {
                fresh = _service.Retrieve(WorkerSchema.TestChunkEntity, candidate.Id,
                    new ColumnSet(WorkerSchema.ChunkStatus, WorkerSchema.ChunkRecoveryCount));
            }
            catch
            {
                continue; // Chunk inzwischen geloescht o.ae. -> ueberspringen
            }
            if (fresh.GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus)?.Value
                != WorkerSchema.ChunkRunning)
                continue; // inzwischen geflippt (lebender/spaeter Worker) -> nicht anfassen

            var prevRecoveryCount = fresh.GetAttributeValue<int?>(WorkerSchema.ChunkRecoveryCount) ?? 0;
            var newRecoveryCount = prevRecoveryCount + 1;

            if (newRecoveryCount > maxRecoveries)
            {
                if (PoisonStale(fresh, prevRecoveryCount, now)) poisoned++;
            }
            else
            {
                if (ResetStale(fresh, newRecoveryCount)) recovered++;
            }
        }

        return (recovered, poisoned);
    }

    /// <summary>Reset "Laeuft" -> "Fortsetzen" per OC; der Worker resumed ab jbe_group_cursor.</summary>
    private bool ResetStale(Entity fresh, int newRecoveryCount)
    {
        var update = new Entity(WorkerSchema.TestChunkEntity, fresh.Id)
        {
            [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkResume),
            [WorkerSchema.ChunkRecoveryCount] = newRecoveryCount
        };
        update.RowVersion = fresh.RowVersion;
        return TryOcUpdate(update,
            $"Stale-Chunk {fresh.Id} -> Fortsetzen (Recovery {newRecoveryCount}).");
    }

    /// <summary>Loop-Breaker: zu oft recovert ohne Fortschritt -> "Fehler" per OC (Plateau-faehig).</summary>
    private bool PoisonStale(Entity fresh, int prevRecoveryCount, Func<DateTime> now)
    {
        var msg = $"Stale-Chunk-Recovery: Chunk lief wiederholt ins Hard-Timeout ohne Fortschritt " +
                  $"({prevRecoveryCount} Recoveries). Wahrscheinlich ein Einzeltest, dessen Schrittkette " +
                  "das Wellen-Budget uebersteigt. Abhilfe: kleinere jbe_chunksize oder Step-Level-" +
                  "Checkpointing (ADR-0009 Entscheidung 7, vertagt).";
        var update = new Entity(WorkerSchema.TestChunkEntity, fresh.Id)
        {
            [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkError),
            [WorkerSchema.ChunkErrorDetails] = msg,
            [WorkerSchema.ChunkCompletedOn] = now()
        };
        update.RowVersion = fresh.RowVersion;
        return TryOcUpdate(update, $"Stale-Chunk {fresh.Id} -> Fehler (Loop-Breaker).");
    }

    private bool TryOcUpdate(Entity update, string logMessage)
    {
        try
        {
            _service.Execute(new UpdateRequest
            {
                Target = update,
                ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches
            });
            _log?.Invoke(logMessage);
            return true;
        }
        catch (FaultException<OrganizationServiceFault>)
        {
            // Der Chunk hat sich zwischen Read und Reset geaendert (ein spaeter Worker hat doch
            // noch geflippt) -> nicht clobbern, ueberspringen.
            _log?.Invoke($"Stale-Chunk {update.Id}: OC-Reset verloren (Chunk inzwischen geaendert) -> skip.");
            return false;
        }
    }

    private List<Guid> LoadRunningRunIds()
    {
        var query = new QueryExpression(WorkerSchema.TestRunEntity)
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };
        query.Criteria.AddCondition(
            WorkerSchema.RunStatus, ConditionOperator.Equal, WorkerSchema.StatusRunning);

        return _service.RetrieveMultiple(query).Entities.Select(e => e.Id).ToList();
    }

    private List<Entity> LoadRunningChunks(Guid runId)
    {
        var query = new QueryExpression(WorkerSchema.TestChunkEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.ChunkStatus, WorkerSchema.ChunkLastClaimedOn,
                WorkerSchema.ChunkStartedOn, WorkerSchema.ChunkRecoveryCount),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.ChunkTestRunId, ConditionOperator.Equal, runId);
        query.Criteria.AddCondition(
            WorkerSchema.ChunkStatus, ConditionOperator.Equal, WorkerSchema.ChunkRunning);

        return _service.RetrieveMultiple(query).Entities.ToList();
    }
}
