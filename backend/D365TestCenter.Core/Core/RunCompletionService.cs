using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>
/// Plateau-Erkennung + Run-Abschluss (ADR-0009 B.2 / C-04). Wird sowohl vom Worker (nach jedem
/// fertigen Chunk) als auch vom Koordinator (nach dem Setzen von <c>jbe_chunks_total</c>) aufgerufen
/// -- wer als Letzter beobachtet, dass alle Chunks Verarbeitet/Fehler sind, rechnet das
/// Run-Aggregat EINMAL aus den Result-Records und setzt <c>jbe_testrun</c> auf Completed.
///
/// Completion-Guard: das finale Update läuft mit <see cref="ConcurrencyBehavior.IfRowVersionMatches"/>
/// -- bei einem Plateau-Race gewinnt genau ein Aufrufer, die Verlierer erhalten eine
/// Concurrency-Fault und kehren ohne Doppelabschluss zurück (verhindert Lost-Update / 0x8009000c).
/// Run-Zähler werden NICHT inkrementell geführt (kein Shared-Counter-OC), sondern am Plateau
/// aus den Records aggregiert (<see cref="TestRunner.ComputeRunAggregate"/>).
/// </summary>
public sealed class RunCompletionService
{
    private readonly IOrganizationService _service;
    private readonly Action<string>? _log;

    public RunCompletionService(IOrganizationService service, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _log = log;
    }

    /// <summary>
    /// Prüft, ob alle Chunks des Laufs Verarbeitet/Fehler sind (Plateau), und schließt den Lauf
    /// in dem Fall genau einmal ab. Gibt true zurück, wenn DIESER Aufruf den Lauf abgeschlossen hat.
    /// </summary>
    public bool TryComplete(Guid testRunId, Func<DateTime>? clock = null)
    {
        var now = clock ?? (() => DateTime.UtcNow);

        var run = _service.Retrieve(WorkerSchema.TestRunEntity, testRunId, new ColumnSet(
            WorkerSchema.RunStatus, WorkerSchema.RunChunksTotal, WorkerSchema.RunStartedOn));

        var status = run.GetAttributeValue<OptionSetValue>(WorkerSchema.RunStatus)?.Value;
        if (status == WorkerSchema.StatusCompleted || status == WorkerSchema.StatusError)
            return false; // bereits abgeschlossen

        var total = run.GetAttributeValue<int?>(WorkerSchema.RunChunksTotal) ?? 0;
        if (total <= 0)
            return false; // Koordinator hat chunks_total noch nicht gesetzt -> kein Plateau

        // Chunk-Status-Verteilung zählen.
        var chunks = LoadChunks(testRunId);
        var done = chunks.Count(c => c == WorkerSchema.ChunkProcessed);
        var failed = chunks.Count(c => c == WorkerSchema.ChunkError);
        if (done + failed < total)
            return false; // noch nicht alle Chunks fertig

        // ── Plateau erreicht: Aggregat aus den Result-Records rechnen ──
        var results = LoadResults(testRunId);
        var agg = TestRunner.ComputeRunAggregate(results);

        var startedOn = run.GetAttributeValue<DateTime?>(WorkerSchema.RunStartedOn);
        var completedOn = now();
        var durationMs = startedOn.HasValue
            ? (int)Math.Max(0, (completedOn - startedOn.Value).TotalMilliseconds)
            : 0;

        var summary = $"{agg.Passed}/{agg.Total} bestanden, {agg.Failed + agg.Errored} fehlgeschlagen" +
                      (agg.Skipped > 0 ? $", {agg.Skipped} übersprungen" : "") +
                      $" ({done} Chunks" + (failed > 0 ? $", {failed} fehlerhaft" : "") + ")";

        var update = new Entity(WorkerSchema.TestRunEntity, testRunId)
        {
            [WorkerSchema.RunStatus] = new OptionSetValue(WorkerSchema.StatusCompleted),
            [WorkerSchema.RunCompletedOn] = completedOn,
            [WorkerSchema.RunTotal] = agg.Total,
            [WorkerSchema.RunPassed] = agg.Passed,
            // jbe_failed bleibt rückwärts-kompatibel die Summe failed+errored (UI liest es so);
            // der Outcome-Split steht zusätzlich in jbe_errored / jbe_skipped.
            [WorkerSchema.RunFailed] = agg.Failed + agg.Errored,
            [WorkerSchema.RunErrored] = agg.Errored,
            [WorkerSchema.RunSkipped] = agg.Skipped,
            [WorkerSchema.RunChunksDone] = done,
            [WorkerSchema.RunChunksFailed] = failed,
            [WorkerSchema.RunDurationMs] = durationMs,
            [WorkerSchema.RunTotalTestMs] = (int)Math.Min(int.MaxValue, agg.TotalTestMs),
            [WorkerSchema.RunAvgTestMs] = agg.AvgTestMs,
            [WorkerSchema.RunMedianTestMs] = agg.MedianTestMs,
            [WorkerSchema.RunMinTestMs] = agg.MinTestMs,
            [WorkerSchema.RunMaxTestMs] = agg.MaxTestMs,
            [WorkerSchema.RunRecordsCreated] = agg.RecordsCreated,
            [WorkerSchema.RunSummary] = Truncate(summary, 4000)
        };
        if (!string.IsNullOrEmpty(agg.SlowestTestId))
            update[WorkerSchema.RunSlowestTestId] = Truncate(agg.SlowestTestId, 100);

        update.RowVersion = run.RowVersion;
        try
        {
            _service.Execute(new UpdateRequest
            {
                Target = update,
                ConcurrencyBehavior = ConcurrencyBehavior.IfRowVersionMatches
            });
            _log?.Invoke($"Plateau: Lauf abgeschlossen ({summary})");
            return true;
        }
        catch (System.ServiceModel.FaultException<OrganizationServiceFault>)
        {
            // Plateau-Race-Verlierer: ein anderer Aufrufer hat den Lauf bereits abgeschlossen.
            _log?.Invoke("Plateau: bereits von einem anderen Worker abgeschlossen (OC-Verlierer).");
            return false;
        }
    }

    private List<int> LoadChunks(Guid testRunId)
    {
        var query = new QueryExpression(WorkerSchema.TestChunkEntity)
        {
            ColumnSet = new ColumnSet(WorkerSchema.ChunkStatus),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.ChunkTestRunId, ConditionOperator.Equal, testRunId);

        return _service.RetrieveMultiple(query).Entities
            .Select(e => e.GetAttributeValue<OptionSetValue>(WorkerSchema.ChunkStatus)?.Value ?? -1)
            .ToList();
    }

    private List<TestCaseResult> LoadResults(Guid testRunId)
    {
        var query = new QueryExpression(WorkerSchema.TestRunResultEntity)
        {
            ColumnSet = new ColumnSet(
                WorkerSchema.ResultTestId, WorkerSchema.ResultOutcome,
                WorkerSchema.ResultDuration, WorkerSchema.ResultTrackedRecords),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.ResultTestRun, ConditionOperator.Equal, testRunId);

        var list = new List<TestCaseResult>();
        foreach (var e in _service.RetrieveMultiple(query).Entities)
        {
            list.Add(new TestCaseResult
            {
                TestId = e.GetAttributeValue<string>(WorkerSchema.ResultTestId) ?? "",
                Outcome = MapOutcome(e.GetAttributeValue<OptionSetValue>(WorkerSchema.ResultOutcome)?.Value),
                DurationMs = e.GetAttributeValue<int>(WorkerSchema.ResultDuration),
                TrackedRecords = BuildTrackedPlaceholder(
                    e.GetAttributeValue<string>(WorkerSchema.ResultTrackedRecords))
            });
        }
        return list;
    }

    /// <summary>
    /// ComputeRunAggregate zählt nur die ANZAHL der TrackedRecords (RecordsCreated). Aus dem
    /// persistierten JSON-Array wird darum nur die Länge rekonstruiert (Platzhalter-Einträge).
    /// </summary>
    private static List<TrackedRecord> BuildTrackedPlaceholder(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TrackedRecord>();
        try
        {
            var arr = Newtonsoft.Json.Linq.JArray.Parse(json);
            return Enumerable.Range(0, arr.Count).Select(_ => new TrackedRecord()).ToList();
        }
        catch
        {
            return new List<TrackedRecord>();
        }
    }

    private static TestOutcome MapOutcome(int? optionSetValue) => optionSetValue switch
    {
        WorkerSchema.OutcomePassed => TestOutcome.Passed,
        WorkerSchema.OutcomeFailed => TestOutcome.Failed,
        WorkerSchema.OutcomeError => TestOutcome.Error,
        WorkerSchema.OutcomeSkipped => TestOutcome.Skipped,
        _ => TestOutcome.Error
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value!.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
