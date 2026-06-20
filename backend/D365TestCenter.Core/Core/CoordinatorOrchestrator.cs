using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;

namespace D365TestCenter.Core;

/// <summary>Ergebnis eines Koordinator-Laufs (fuer Tests + Trace).</summary>
public enum CoordinatorOutcome
{
    /// <summary>Nicht der Trigger-Status (Busy/Running/Completed-Fire) oder OC-Claim verloren.</summary>
    Skipped,
    /// <summary>Keine Testfaelle -> Lauf direkt auf Completed.</summary>
    NoTests,
    /// <summary>Watchdog gerissen: Cursor persistiert, Status zurueck auf Geplant (Self-Trigger).</summary>
    Continued,
    /// <summary>Alle Chunks angelegt, Lauf auf Running (Worker uebernehmen).</summary>
    ChunksCreated
}

/// <summary>
/// Testbarer Kern des RunCoordinator-Plugins (ADR-0009 Phase 2, async auf <c>jbe_testrun</c>).
/// Ersetzt die depth-begrenzte Batch-Cascade durch Fan-Out: laedt die aktiven Testfaelle, bildet
/// Abhaengigkeits-Gruppen, packt sie in Chunks und legt je Chunk einen <c>jbe_testchunk</c> (Neu) an;
/// die Worker uebernehmen die Ausfuehrung. Minimal-Regel: KEINE Test-Ausfuehrung im Koordinator.
///
/// Eigenschaften:
///   - <b>OC-Claim</b> Planned -> Splitting (IfRowVersionMatches): ein Doppel-Fire-Verlierer skippt.
///   - <b>H2 (DeleteOldResults genau einmal):</b> alte Results UND alte Chunks werden nur auf der
///     ersten Welle (coordinator_cursor == 0) geloescht, nie auf einer Continuation.
///   - <b>Eingefrorener Snapshot (C-10):</b> die (rohen) Test-IDs werden in die Chunk-Records
///     geschrieben; eine Continuation re-deriviert dieselbe Chunk-Aufteilung deterministisch.
///   - <b>chunks_total frueh</b> (vor den Chunk-Creates) gesetzt: ein fruehzeitig fertiger Worker
///     erkennt das Plateau korrekt (kein Race/Deadlock).
///   - <b>Watchdog:</b> reisst das Zeitbudget waehrend der Chunk-Creates, wird der Cursor
///     persistiert und der Status auf Geplant zurueckgesetzt (Self-Trigger), mindestens ein Create
///     pro Welle (Terminierung gesichert).
/// </summary>
public sealed class CoordinatorOrchestrator
{
    private readonly IOrganizationService _service;
    private readonly Action<string>? _log;

    public CoordinatorOrchestrator(IOrganizationService service, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _log = log;
    }

    public CoordinatorOutcome Run(Guid testRunId, int chunkSize, int budgetSeconds,
        Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);
        var startTime = now();

        var run = _service.Retrieve(WorkerSchema.TestRunEntity, testRunId, new ColumnSet(
            WorkerSchema.RunStatus, WorkerSchema.RunFilter, WorkerSchema.RunChunkSize,
            WorkerSchema.RunCoordinatorCursor, WorkerSchema.RunContinuations));

        var status = run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus)?.Value;
        if (status != WorkerSchema.StatusPlanned)
        {
            _log?.Invoke($"Koordinator: Status {status} ist kein Trigger (nur Geplant) -> skip.");
            return CoordinatorOutcome.Skipped;
        }

        var cursor = run.GetAttributeValue<int?>(WorkerSchema.RunCoordinatorCursor) ?? 0;
        var isFirstFire = cursor == 0;
        var continuations = run.GetAttributeValue<int?>(WorkerSchema.RunContinuations) ?? 0;

        var runChunkSize = run.GetAttributeValue<int?>(WorkerSchema.RunChunkSize);
        var effectiveChunkSize = (runChunkSize.HasValue && runChunkSize.Value > 0)
            ? runChunkSize.Value : chunkSize;
        if (effectiveChunkSize < 1) effectiveChunkSize = 1;

        // ── OC-Claim: Planned -> Splitting ──────────────────────────
        var claim = new Entity(WorkerSchema.TestRunEntity, testRunId)
        {
            [WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusSplitting)
        };
        if (isFirstFire)
        {
            claim[WorkerSchema.RunStartedOn] = startTime;
            claim[WorkerSchema.RunTotal] = 0;
            claim[WorkerSchema.RunPassed] = 0;
            claim[WorkerSchema.RunFailed] = 0;
            claim[WorkerSchema.RunErrored] = 0;
            claim[WorkerSchema.RunSkipped] = 0;
            claim[WorkerSchema.RunChunksDone] = 0;
            claim[WorkerSchema.RunChunksFailed] = 0;
            claim[WorkerSchema.RunContinuations] = 0;
            continuations = 0;
        }
        claim.RowVersion = run.RowVersion;
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
            _log?.Invoke("Koordinator: OC-Claim verloren (Doppel-Fire) -> skip.");
            return CoordinatorOutcome.Skipped;
        }

        // ── H2: alte Results + Chunks NUR auf der ersten Welle loeschen ──
        if (isFirstFire)
        {
            DeleteOldResults(testRunId);
            DeleteOldChunks(testRunId);
        }

        // ── Tests laden, gruppieren, chunken (deterministisch) ──────
        var filter = run.GetAttributeValue<string>(WorkerSchema.RunFilter);
        var tests = TestCaseLoader.LoadEnabled(_service, filter, _log);
        if (tests.Count == 0)
        {
            var msg = $"Keine Testfaelle gefunden (Filter: {filter ?? "*"})";
            _service.Update(new Entity(WorkerSchema.TestRunEntity, testRunId)
            {
                [WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusCompleted),
                [WorkerSchema.RunCompletedOn] = now(),
                [WorkerSchema.RunTotal] = 0,
                [WorkerSchema.RunChunksTotal] = 0,
                [WorkerSchema.RunCoordinatorCursor] = 0,
                [WorkerSchema.RunSummary] = msg,
                [WorkerSchema.RunFullLog] = $"[RunCoordinator] {msg}"
            });
            _log?.Invoke(msg);
            return CoordinatorOutcome.NoTests;
        }

        var groups = TestRunner.BuildDependencyGroups(tests);
        var chunks = TestRunner.BuildChunks(groups, effectiveChunkSize);
        _log?.Invoke($"Koordinator: {tests.Count} Test(s) -> {groups.Count} Gruppe(n) -> " +
                     $"{chunks.Count} Chunk(s) (chunkSize {effectiveChunkSize}), ab Cursor {cursor}.");

        // chunks_total FRUEH setzen (vor den Creates), damit ein schnell fertiger Worker das
        // Plateau korrekt erkennt. Deterministisch gleich auf jeder Welle.
        _service.Update(new Entity(WorkerSchema.TestRunEntity, testRunId)
        {
            [WorkerSchema.RunChunksTotal] = chunks.Count
        });

        // ── Chunk-Records ab Cursor anlegen, Watchdog pro Create ────
        for (int i = cursor; i < chunks.Count; i++)
        {
            if (i > cursor && (now() - startTime).TotalSeconds >= budgetSeconds)
            {
                _service.Update(new Entity(WorkerSchema.TestRunEntity, testRunId)
                {
                    [WorkerSchema.RunCoordinatorCursor] = i,
                    [WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusPlanned),
                    [WorkerSchema.RunContinuations] = continuations + 1
                });
                _log?.Invoke($"Koordinator: Budget {budgetSeconds}s gerissen bei Chunk {i} -> " +
                             "Cursor persistiert, Self-Trigger.");
                return CoordinatorOutcome.Continued;
            }

            CreateChunk(testRunId, i, chunks[i]);
        }

        // ── Alle Chunks angelegt: Running ───────────────────────────
        _service.Update(new Entity(WorkerSchema.TestRunEntity, testRunId)
        {
            [WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusRunning),
            [WorkerSchema.RunChunksTotal] = chunks.Count,
            [WorkerSchema.RunCoordinatorCursor] = 0
        });
        _log?.Invoke($"Koordinator: alle {chunks.Count} Chunk(s) angelegt, Lauf auf Running.");

        // Safety-Net: falls alle Chunks bereits fertig sind (sehr schnelle Worker), Plateau pruefen.
        new RunCompletionService(_service, _log).TryComplete(testRunId, now);

        return CoordinatorOutcome.ChunksCreated;
    }

    private void CreateChunk(Guid testRunId, int index, List<TestCase> chunkTests)
    {
        var ids = chunkTests.Select(t => t.Id).ToList();
        var chunk = new Entity(WorkerSchema.TestChunkEntity)
        {
            [WorkerSchema.ChunkTestRunId] = new EntityReference(WorkerSchema.TestRunEntity, testRunId),
            [WorkerSchema.ChunkIndex] = index,
            [WorkerSchema.ChunkTestIds] = JsonConvert.SerializeObject(ids),
            [WorkerSchema.ChunkStatus] = new OptionSetValue(WorkerSchema.ChunkNew),
            [WorkerSchema.ChunkGroupCursor] = 0,
            [WorkerSchema.ChunkProcessedCount] = 0,
            [WorkerSchema.ChunkFailedCount] = 0,
            [WorkerSchema.ChunkContinuations] = 0
        };
        _service.Create(chunk);
    }

    private void DeleteOldResults(Guid testRunId)
    {
        var resultQuery = new QueryExpression(WorkerSchema.TestRunResultEntity)
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };
        resultQuery.Criteria.AddCondition(WorkerSchema.ResultTestRun, ConditionOperator.Equal, testRunId);

        var results = _service.RetrieveMultiple(resultQuery).Entities;
        foreach (var result in results)
        {
            var stepQuery = new QueryExpression(WorkerSchema.TestStepEntity)
            {
                ColumnSet = new ColumnSet(false),
                NoLock = true
            };
            stepQuery.Criteria.AddCondition(WorkerSchema.StepRunResult, ConditionOperator.Equal, result.Id);
            foreach (var step in _service.RetrieveMultiple(stepQuery).Entities)
                _service.Delete(WorkerSchema.TestStepEntity, step.Id);

            _service.Delete(WorkerSchema.TestRunResultEntity, result.Id);
        }

        if (results.Count > 0)
            _log?.Invoke($"Koordinator: {results.Count} alte Result(s) geloescht (H2, erste Welle).");
    }

    private void DeleteOldChunks(Guid testRunId)
    {
        var chunkQuery = new QueryExpression(WorkerSchema.TestChunkEntity)
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };
        chunkQuery.Criteria.AddCondition(WorkerSchema.ChunkTestRunId, ConditionOperator.Equal, testRunId);

        var chunks = _service.RetrieveMultiple(chunkQuery).Entities;
        foreach (var chunk in chunks)
            _service.Delete(WorkerSchema.TestChunkEntity, chunk.Id);

        if (chunks.Count > 0)
            _log?.Invoke($"Koordinator: {chunks.Count} alte Chunk(s) geloescht (Re-Run, erste Welle).");
    }
}
