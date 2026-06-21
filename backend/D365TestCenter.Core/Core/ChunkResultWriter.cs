using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;

namespace D365TestCenter.Core;

/// <summary>
/// Idempotente Schreibstelle für ein <see cref="TestCaseResult"/> (ADR-0009 H1/H3).
///
/// Der Worker ist nicht idempotent in der Test-AUSFÜHRUNG (eine atomar neu gestartete Gruppe
/// re-läuft ihre Tests, ein Doppel-Fire kann denselben Chunk zweimal anstoßen). Korrektheit
/// trägt darum die idempotente RESULT-Schreibung: pro <c>(jbe_testrunid, jbe_testid)</c> wird die
/// <c>jbe_testrunresult</c>-Zeile per <see cref="UpsertRequest"/> über den Alternate Key
/// <see cref="WorkerSchema.ResultAlternateKey"/> ge-upsertet (vorhandene Zeile ersetzt statt
/// dupliziert) und die zugehörigen <c>jbe_teststep</c>-Records werden vor dem Neuschreiben
/// gelöscht. So erzeugt ein Resume/Doppel-Fire weder Doppelzeilen noch Doppelschritte.
///
/// H3-Konsolidierung: EINE idempotente Schreibstelle (statt der dritten Variante neben
/// <c>RunTestsOnStatusChange.WriteResultRecords</c> und
/// <c>TestCenterOrchestrator.WriteSingleResultRecord</c>). Strukturell identisch zu jenen
/// (gleiche Felder, gleiche Step-Schleife), nur per Upsert statt blankem Create.
/// </summary>
public sealed class ChunkResultWriter
{
    private readonly IOrganizationService _service;
    private readonly Action<string>? _log;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    public ChunkResultWriter(IOrganizationService service, Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _log = log;
    }

    /// <summary>
    /// Upsertet die Result-Zeile für <paramref name="tcResult"/> unter dem Lauf
    /// <paramref name="testRunId"/> und schreibt ihre Steps idempotent neu. Gibt die Id der
    /// (neu angelegten oder ersetzten) Result-Zeile zurück.
    /// </summary>
    public Guid UpsertResult(Guid testRunId, TestCaseResult tcResult)
    {
        var testRunRef = new EntityReference(WorkerSchema.TestRunEntity, testRunId);

        var result = new Entity(WorkerSchema.TestRunResultEntity);
        // Alternate Key (jbe_testrunid, jbe_testid) identifiziert die Zeile für den Upsert.
        result.KeyAttributes[WorkerSchema.ResultTestRun] = testRunRef;
        result.KeyAttributes[WorkerSchema.ResultTestId] = tcResult.TestId;

        result[WorkerSchema.ResultTestRun] = testRunRef;
        result[WorkerSchema.ResultTestId] = tcResult.TestId;
        result[WorkerSchema.ResultOutcome] = new OptionSetValue(MapOutcome(tcResult.Outcome));
        result[WorkerSchema.ResultDuration] = (int)tcResult.DurationMs;
        result[WorkerSchema.ResultError] = Truncate(tcResult.ErrorMessage, 4000);
        result[WorkerSchema.ResultAssertions] = Truncate(BuildAssertionsJson(tcResult), 100000);

        var trackedJson = BuildTrackedJson(tcResult);
        if (!string.IsNullOrEmpty(trackedJson))
            result[WorkerSchema.ResultTrackedRecords] = Truncate(trackedJson, 100000);

        var resp = (UpsertResponse)_service.Execute(new UpsertRequest { Target = result });
        var resultId = resp.Target.Id;

        // Steps idempotent: bestehende dieser Result-Zeile löschen, dann frisch schreiben.
        // Bei einer neu angelegten Zeile (RecordCreated) gibt es keine -> Query liefert leer.
        if (!resp.RecordCreated)
            DeleteStepsOf(resultId);
        WriteSteps(resultId, tcResult);

        return resultId;
    }

    private void DeleteStepsOf(Guid resultId)
    {
        var query = new QueryExpression(WorkerSchema.TestStepEntity)
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };
        query.Criteria.AddCondition(WorkerSchema.StepRunResult, ConditionOperator.Equal, resultId);

        var steps = _service.RetrieveMultiple(query);
        foreach (var step in steps.Entities)
            _service.Delete(WorkerSchema.TestStepEntity, step.Id);

        if (steps.Entities.Count > 0)
            _log?.Invoke($"      ResultWriter: {steps.Entities.Count} alte Step(s) ersetzt");
    }

    private void WriteSteps(Guid resultId, TestCaseResult tcResult)
    {
        var resultRef = new EntityReference(WorkerSchema.TestRunResultEntity, resultId);

        foreach (var stepResult in tcResult.StepResults)
        {
            try
            {
                var step = new Entity(WorkerSchema.TestStepEntity)
                {
                    [WorkerSchema.StepNumber] = stepResult.StepNumber,
                    [WorkerSchema.StepAction] = Truncate(stepResult.Action ?? "", 100),
                    [WorkerSchema.StepDuration] = (int)stepResult.DurationMs,
                    [WorkerSchema.StepError] = Truncate(stepResult.Message, 4000),
                    [WorkerSchema.StepStatus] = new OptionSetValue(WorkerSchema.MapStepStatus(stepResult)),
                    [WorkerSchema.StepRunResult] = resultRef
                };
                if (!string.IsNullOrEmpty(stepResult.Alias))
                    step[WorkerSchema.StepAlias] = Truncate(stepResult.Alias, 100);
                if (!string.IsNullOrEmpty(stepResult.Entity))
                    step[WorkerSchema.StepEntity] = Truncate(stepResult.Entity, 100);
                if (stepResult.RecordId.HasValue)
                    step[WorkerSchema.StepRecordId] = stepResult.RecordId.Value.ToString();
                if (!string.IsNullOrEmpty(stepResult.AssertField))
                    step[WorkerSchema.StepAssertionField] = Truncate(stepResult.AssertField, 500);
                if (!string.IsNullOrEmpty(stepResult.ExpectedDisplay))
                    step[WorkerSchema.StepExpected] = Truncate(stepResult.ExpectedDisplay, 4000);
                if (!string.IsNullOrEmpty(stepResult.ActualDisplay))
                    step[WorkerSchema.StepActual] = Truncate(stepResult.ActualDisplay, 4000);
                if (!string.IsNullOrEmpty(stepResult.InputData))
                    step[WorkerSchema.StepInputData] = Truncate(stepResult.InputData, 100000);
                if (!string.IsNullOrEmpty(stepResult.OutputData))
                    step[WorkerSchema.StepOutputData] = Truncate(stepResult.OutputData, 100000);
                _service.Create(step);
            }
            catch
            {
                /* non-critical: ein Fehler bei einem Step-Log soll den Test-Run nicht abbrechen. */
            }
        }
    }

    private static string BuildAssertionsJson(TestCaseResult tcResult)
    {
        try
        {
            var assertSteps = tcResult.StepResults
                .Where(s => string.Equals(s.Action, "Assert", StringComparison.OrdinalIgnoreCase))
                .Select(s => new
                {
                    description = s.Description,
                    passed = s.Success && !s.Skipped,
                    skipped = s.Skipped,
                    message = s.Message,
                    expectedDisplay = s.ExpectedDisplay,
                    actualDisplay = s.ActualDisplay
                })
                .ToList();
            return JsonConvert.SerializeObject(assertSteps, JsonSettings);
        }
        catch
        {
            return "[]";
        }
    }

    private static string? BuildTrackedJson(TestCaseResult tcResult)
    {
        try
        {
            return tcResult.TrackedRecords.Count > 0
                ? JsonConvert.SerializeObject(tcResult.TrackedRecords, JsonSettings)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static int MapOutcome(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => WorkerSchema.OutcomePassed,
        TestOutcome.Failed => WorkerSchema.OutcomeFailed,
        TestOutcome.Error => WorkerSchema.OutcomeError,
        TestOutcome.Skipped => WorkerSchema.OutcomeSkipped,
        _ => WorkerSchema.OutcomeError
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value!.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
