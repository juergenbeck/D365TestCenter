using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>Ergebnis eines Recovery-Sweeps (für die Custom API + Trace).</summary>
public sealed class RecoverySweepResult
{
    /// <summary>Anzahl Läufe in Status "Läuft" (Running), die gescannt wurden.</summary>
    public int RunsScanned { get; set; }
    /// <summary>Anzahl Chunks, die von "Läuft" auf "Fortsetzen" zurückgesetzt wurden.</summary>
    public int ChunksRecovered { get; set; }
    /// <summary>Anzahl Chunks, die wegen überschrittener Recovery-Obergrenze auf "Fehler" gesetzt wurden.</summary>
    public int ChunksPoisoned { get; set; }
    /// <summary>Anzahl Läufe, die dieser Sweep am Plateau abgeschlossen hat.</summary>
    public int RunsCompleted { get; set; }

    public override string ToString() =>
        $"runs={RunsScanned}, recovered={ChunksRecovered}, poisoned={ChunksPoisoned}, completed={RunsCompleted}";
}

/// <summary>
/// Stale-"Läuft"-Chunk-Recovery (FB-46 / OE-12). Testbarer Kern der Custom API
/// <c>jbe_RecoverStaleChunks</c>, die ein Power-Automate-Recurrence-Flow taktet (ADR-0009
/// Takt-Option b, hier gezielt für Recovery).
///
/// Problem: reißt eine Worker-Welle das harte 120-s-Sandbox-Limit, wird der Async-Worker gekillt,
/// NACHDEM er den Chunk per OC-Claim auf "Läuft" gesetzt hat, aber BEVOR er flippt. Ein eingefrorener
/// Chunk bekommt nie wieder ein Trigger-Event -> das Run-Plateau wird nie erreicht -> Deadlock.
///
/// Lösung: ein wiederkehrender Sweep findet Chunks, die länger als <c>staleSeconds</c> in "Läuft"
/// stehen (provabel tot: ein lebender Worker hält "Läuft" höchstens 120 s) und setzt sie auf
/// "Fortsetzen" zurück -- der Worker resumed ab <c>jbe_group_cursor</c> (Gruppen-Grenzen-Continuation,
/// idempotente Results -> ein atomarer Gruppen-Neustart ist folgenlos).
///
/// Eigenschaften:
///   - <b>Stale-Anker:</b> <c>jbe_lastclaimedon</c> (vom Worker bei jedem OC-Claim gesetzt), Fallback
///     <c>jbe_startedon</c> für Chunks, die vor dem Feld eingefroren sind.
///   - <b>Re-Read + OC-Reset (IfRowVersionMatches):</b> direkt vor dem Reset wird der Chunk frisch
///     gelesen (frische RowVersion, Status re-bestätigt); änderte sich der Chunk doch noch (ein
///     später Worker hat geflippt), wird übersprungen -- kein Clobbern.
///   - <b>Loop-Breaker (<c>jbe_recoverycount</c>):</b> der Worker setzt ihn bei Fortschritt auf 0; die
///     Recovery erhöht ihn. Über <c>maxRecoveries</c> -> Chunk auf "Fehler" statt Endlos-Resume
///     (irreduzibler Rest: ein Einzeltest > Wellen-Budget, ADR Entscheidung 7 vertagt). Der
///     Fehler-Chunk zählt zum Plateau -> der Lauf schließt sauber ab statt zu deadlocken.
///   - <b>Plateau:</b> je gescanntem Lauf wird <see cref="RunCompletionService"/> aufgerufen -- ein
///     Poison-Chunk kann das Plateau auslösen, und ein zuvor verpasster Abschluss wird nachgeholt.
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
    /// Sweept stale "Läuft"-Chunks (länger als <paramref name="staleSeconds"/> ohne Re-Claim) in
    /// laufenden Läufen und recovert sie. Strategie: EINE Chunk-Query (Status "Läuft") + EIN
    /// Running-Run-Filter statt pro-Lauf-Iteration -- auf einer Org mit vielen Alt-Läufen, die
    /// dauerhaft in Status Running stehen (Alt-Batch-Cascade ohne Chunks), wären pro-Lauf-Queries
    /// reine Verschwendung. Nur Läufe mit tatsächlich stale Chunks werden angefasst.
    /// </summary>
    public RecoverySweepResult Sweep(int staleSeconds, int maxRecoveries, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        var result = new RecoverySweepResult();

        var runningRunIds = new HashSet<Guid>(LoadRunningRunIds());
        result.RunsScanned = runningRunIds.Count;
        if (runningRunIds.Count == 0)
        {
            _log?.Invoke("Stale-Chunk-Recovery: keine laufenden Läufe.");
            return result;
        }

        var staleChunks = LoadRunningChunks()
            .Where(c => IsStaleInRunningRun(c, runningRunIds, staleSeconds, now))
            .ToList();

        var affectedRuns = new HashSet<Guid>();
        foreach (var chunk in staleChunks)
        {
            var runId = chunk.GetAttributeValue<EntityReference>(WorkerSchema.ChunkTestRunId)!.Id;
            var (recovered, poisoned) = RecoverChunk(chunk.Id, maxRecoveries, now);
            result.ChunksRecovered += recovered;
            result.ChunksPoisoned += poisoned;
            if (recovered + poisoned > 0) affectedRuns.Add(runId);
        }

        // Plateau je betroffenem Lauf prüfen: ein Poison-Chunk kann es auslösen.
        foreach (var runId in affectedRuns)
            if (new RunCompletionService(_service, _log).TryComplete(runId, now))
                result.RunsCompleted++;

        _log?.Invoke($"Stale-Chunk-Recovery: {result}");
        return result;
    }

    private static bool IsStaleInRunningRun(
        Entity chunk, HashSet<Guid> runningRunIds, int staleSeconds, Func<DateTime> now)
    {
        var runRef = chunk.GetAttributeValue<EntityReference>(WorkerSchema.ChunkTestRunId);
        if (runRef == null || !runningRunIds.Contains(runRef.Id))
            return false; // Chunk gehört keinem laufenden Lauf -> nicht anfassen
        var anchor = chunk.GetAttributeValue<DateTime?>(WorkerSchema.ChunkLastClaimedOn)
                     ?? chunk.GetAttributeValue<DateTime?>(WorkerSchema.ChunkStartedOn);
        if (anchor == null)
            return false; // kein Zeit-Anker -> nicht messbar, in Ruhe lassen
        return (now() - anchor.Value).TotalSeconds > staleSeconds; // sonst frisch (lebender Worker)
    }

    private (int recovered, int poisoned) RecoverChunk(Guid chunkId, int maxRecoveries, Func<DateTime> now)
    {
        // Frische Sicht + RowVersion direkt vor dem OC-Reset holen.
        Entity fresh;
        try
        {
            fresh = _service.Retrieve(WorkerSchema.TestChunkEntity, chunkId,
                new ColumnSet(WorkerSchema.ChunkStatus, WorkerSchema.ChunkRecoveryCount));
        }
        catch
        {
            return (0, 0); // Chunk inzwischen gelöscht o.ae. -> überspringen
        }
        if (fresh.GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus)?.Value
            != WorkerSchema.ChunkRunning)
            return (0, 0); // inzwischen geflippt (lebender/später Worker) -> nicht anfassen

        var prevRecoveryCount = fresh.GetAttributeValue<int?>(WorkerSchema.ChunkRecoveryCount) ?? 0;
        var newRecoveryCount = prevRecoveryCount + 1;

        if (newRecoveryCount > maxRecoveries)
            return PoisonStale(fresh, prevRecoveryCount, now) ? (0, 1) : (0, 0);
        return ResetStale(fresh, newRecoveryCount) ? (1, 0) : (0, 0);
    }

    /// <summary>Reset "Läuft" -> "Fortsetzen" per OC; der Worker resumed ab jbe_group_cursor.</summary>
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

    /// <summary>Loop-Breaker: zu oft recovert ohne Fortschritt -> "Fehler" per OC (Plateau-fähig).</summary>
    private bool PoisonStale(Entity fresh, int prevRecoveryCount, Func<DateTime> now)
    {
        var msg = $"Stale-Chunk-Recovery: Chunk lief wiederholt ins Hard-Timeout ohne Fortschritt " +
                  $"({prevRecoveryCount} Recoveries). Wahrscheinlich ein Einzeltest, dessen Schrittkette " +
                  "das Wellen-Budget übersteigt. Abhilfe: kleinere jbe_chunksize oder Step-Level-" +
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
            // Der Chunk hat sich zwischen Read und Reset geändert (ein später Worker hat doch
            // noch geflippt) -> nicht clobbern, überspringen.
            _log?.Invoke($"Stale-Chunk {update.Id}: OC-Reset verloren (Chunk inzwischen geändert) -> skip.");
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

    private List<Entity> LoadRunningChunks()
    {
        var query = new QueryExpression(WorkerSchema.TestChunkEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.ChunkStatus, WorkerSchema.ChunkTestRunId, WorkerSchema.ChunkLastClaimedOn,
                WorkerSchema.ChunkStartedOn, WorkerSchema.ChunkRecoveryCount),
            NoLock = true
        };
        query.Criteria.AddCondition(
            WorkerSchema.ChunkStatus, ConditionOperator.Equal, WorkerSchema.ChunkRunning);

        return _service.RetrieveMultiple(query).Entities.ToList();
    }
}
