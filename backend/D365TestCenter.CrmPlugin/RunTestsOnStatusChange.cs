using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using D365TestCenter.Core;
using System.Text;
using Newtonsoft.Json;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Plugin on jbe_testrun: executes tests in batches when status is "Planned" or "Running".
///
/// Batch architecture (to stay within the 2-minute Sandbox timeout):
///   1. Status "Planned" -> delete old results, load test cases, start first batch
///   2. Status "Running" + batchoffset > 0 -> continue with next batch
///   3. All tests done -> status "Completed"
///
/// Each batch processes up to BatchSize test cases (default: 5).
/// After each batch, the plugin updates jbe_batchoffset and jbe_teststatus,
/// which triggers the next async execution via the Update step.
///
/// Registration:
///   Entity:              jbe_testrun
///   Message:             Create (PostOperation, Async)
///   Message:             Update (PostOperation, Async)
///   FilteringAttributes: jbe_teststatus,jbe_batchoffset (Update only)
///   PreImage:            jbe_teststatus (Update only)
/// </summary>
public sealed class RunTestsOnStatusChange : IPlugin
{
    // BatchSize: Anzahl Tests pro Plugin-Execution (Self-Trigger-Cascade).
    // Dataverse erlaubt max ~8 Depth-Level pro Pipeline. Auch bei Async-Steps zaehlt
    // der Depth hoch (verifiziert auf LM DEV, Session 07). Das bedeutet:
    //   Max Tests = BatchSize x 8
    // BatchSize=12 -> max 96 Tests in 8 Cascade-Schritten.
    //
    // Risiko: Ein Batch mit 12 komplexen Tests (je 60-90s) würde das 2-Min-Sandbox-
    // Timeout reissen (FB-16). In der Praxis sind die meisten Tests aber schnell (3-15s).
    // Falls ein Batch timeout, bleibt der Run auf "Running" stehen.
    //
    // Faustregel: BatchSize so wählen dass max_test_dauer * BatchSize < 100s bleibt.
    private const int BatchSize = 12;

    // ── Entity Names ─────────────────────────────────────────────
    private const string TestRunEntity = "jbe_testrun";
    private const string TestCaseEntity = "jbe_testcase";
    private const string TestRunResultEntity = "jbe_testrunresult";
    private const string TestStepEntity = "jbe_teststep";

    // ── TestRun Fields ───────────────────────────────────────────
    private const string FldStatus = "jbe_teststatus";
    private const string FldFilter = "jbe_testcasefilter";
    private const string FldKeepRecords = "jbe_keeprecords";
    private const string FldSummary = "jbe_testsummary";
    private const string FldFullLog = "jbe_fulllog";
    private const string FldStartedOn = "jbe_startedon";
    private const string FldCompletedOn = "jbe_completedon";
    private const string FldTotal = "jbe_total";
    private const string FldPassed = "jbe_passed";
    private const string FldFailed = "jbe_failed";
    private const string FldBatchOffset = "jbe_batchoffset";

    // ── TestRunResult Fields ─────────────────────────────────────
    private const string FldResultTestId = "jbe_testid";
    private const string FldResultOutcome = "jbe_outcome";
    private const string FldResultDuration = "jbe_durationms";
    private const string FldResultError = "jbe_errormessage";
    private const string FldResultAssertions = "jbe_assertionresults";
    private const string FldResultTestRun = "jbe_testrunid";

    // ── TestStep Fields (ADR-0004: kein Phase-Feld mehr) ─────────
    private const string FldStepNumber = "jbe_stepnumber";
    private const string FldStepAction = "jbe_action";
    private const string FldStepAssertionField = "jbe_assertionfield";
    private const string FldStepExpected = "jbe_expectedvalue";
    private const string FldStepActual = "jbe_actualvalue";
    private const string FldStepDuration = "jbe_durationms";
    private const string FldStepError = "jbe_errormessage";
    private const string FldStepStatus = "jbe_stepstatus";
    private const string FldStepRunResult = "jbe_testrunresultid";
    private const string FldStepAlias = "jbe_alias";
    private const string FldStepEntity = "jbe_entity";
    private const string FldStepRecordId = "jbe_recordid";
    private const string FldStepInputData = "jbe_inputdata";
    private const string FldStepOutputData = "jbe_outputdata";

    // ── TestCase Fields ──────────────────────────────────────────
    private const string FldTcTestId = "jbe_testid";
    private const string FldTcTitle = "jbe_title";
    private const string FldTcDefinition = "jbe_definitionjson";
    private const string FldTcEnabled = "jbe_enabled";

    // ── Status OptionSet Values ──────────────────────────────────
    private const int StatusPlanned = 105710000;
    private const int StatusRunning = 105710001;
    private const int StatusCompleted = 105710002;
    private const int StatusFailed = 105710003;

    // ── Outcome OptionSet Values ─────────────────────────────────
    private const int OutcomePassed = 105710000;
    private const int OutcomeFailed = 105710001;
    private const int OutcomeError = 105710002;
    private const int OutcomeSkipped = 105710003;

    // ── Step Status OptionSet (ADR-0004: Phase entfaellt) ────────
    private const int StepPassed = 105710000;
    private const int StepFailed = 105710001;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider
            .GetService(typeof(IPluginExecutionContext));
        var tracingService = (ITracingService)serviceProvider
            .GetService(typeof(ITracingService));
        var factory = (IOrganizationServiceFactory)serviceProvider
            .GetService(typeof(IOrganizationServiceFactory));
        // Use SYSTEM context (null) to ensure read access to all test cases
        // regardless of the triggering user's security role
        var service = factory.CreateOrganizationService(null);

        if (context.PrimaryEntityName != TestRunEntity)
            return;

        // Depth-Check: Safety-Limit gegen echte Rekursion. Dataverse-Max ist 8.
        // WICHTIG: Die Self-Trigger-Kaskade (jeder Test updated jbe_batchoffset,
        // was das Update-Plugin triggert) bleibt oft in derselben Pipeline und
        // erhöht Depth bei jedem Schritt. Bei BatchSize=1 und N Tests braucht
        // die Kaskade bis zu N+1 Depth-Level. Guard auf 8 (Dataverse-Max) setzen
        // damit die Kaskade nicht vorzeitig abbricht.
        if (context.Depth > 8)
        {
            tracingService.Trace("RunTests: Depth {0} > 8, skipping (Dataverse-Max erreicht)", context.Depth);
            return;
        }

        var testRunId = context.PrimaryEntityId;
        var testRun = service.Retrieve(TestRunEntity, testRunId,
            new ColumnSet(FldStatus, FldFilter, FldKeepRecords, FldBatchOffset,
                          FldPassed, FldFailed, FldTotal));

        var status = testRun.GetAttributeValue<OptionSetValue>(FldStatus);
        if (status == null)
            return;

        var batchOffset = testRun.GetAttributeValue<int?>(FldBatchOffset) ?? 0;

        tracingService.Trace("RunTests: Status={0}, Offset={1}, Message={2}",
            status.Value, batchOffset, context.MessageName);

        // ── SCENARIO 1: Status = Planned -> Start new run ────────
        if (status.Value == StatusPlanned)
        {
            // Guard: On Update, check PreImage to prevent re-fire
            if (context.MessageName == "Update" &&
                context.PreEntityImages.Contains("PreImage"))
            {
                var preStatus = context.PreEntityImages["PreImage"]
                    .GetAttributeValue<OptionSetValue>(FldStatus);
                if (preStatus != null && preStatus.Value == StatusPlanned)
                {
                    tracingService.Trace("Status was already Planned, skipping");
                    return;
                }
            }

            StartNewRun(service, tracingService, testRunId, testRun);
            return;
        }

        // ── SCENARIO 2: Status = Running + Offset > 0 -> Continue batch ──
        if (status.Value == StatusRunning && batchOffset > 0)
        {
            ContinueBatch(service, tracingService, testRunId, testRun, batchOffset);
            return;
        }

        // All other status values: do nothing
    }

    // ════════════════════════════════════════════════════════════════
    //  Start new run: delete old results, load tests, run first batch
    // ════════════════════════════════════════════════════════════════

    private void StartNewRun(
        IOrganizationService service, ITracingService trace,
        Guid testRunId, Entity testRun)
    {
        try
        {
            // Set status to Running
            var startUpdate = new Entity(TestRunEntity, testRunId)
            {
                [FldStatus] = new OptionSetValue(StatusRunning),
                [FldSummary] = "Testausführung gestartet...",
                [FldStartedOn] = DateTime.UtcNow,
                [FldBatchOffset] = 0,
                [FldTotal] = 0,
                [FldPassed] = 0,
                [FldFailed] = 0
            };
            service.Update(startUpdate);

            // Delete old results
            DeleteOldResults(service, testRunId, trace);

            // Load and run first batch
            var filter = testRun.GetAttributeValue<string>(FldFilter);
            var testCases = LoadTestCases(service, filter, trace);

            if (testCases.Count == 0)
            {
                var emptyUpdate = new Entity(TestRunEntity, testRunId)
                {
                    [FldStatus] = new OptionSetValue(StatusCompleted),
                    [FldSummary] = $"Keine Testfälle gefunden (Filter: {filter ?? "*"})",
                    [FldCompletedOn] = DateTime.UtcNow,
                    [FldTotal] = 0
                };
                service.Update(emptyUpdate);
                return;
            }

            trace.Trace("Loaded {0} test cases, starting batch 0-{1}", testCases.Count, Math.Min(BatchSize, testCases.Count));

            var keepRecords = testRun.GetAttributeValue<bool>(FldKeepRecords);
            RunBatch(service, trace, testRunId, testCases, 0, keepRecords);
        }
        catch (Exception ex)
        {
            trace.Trace("StartNewRun error: {0}", ex.Message);
            SetError(service, testRunId, ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Continue batch: load tests, run next batch from offset
    // ════════════════════════════════════════════════════════════════

    private void ContinueBatch(
        IOrganizationService service, ITracingService trace,
        Guid testRunId, Entity testRun, int offset)
    {
        try
        {
            var filter = testRun.GetAttributeValue<string>(FldFilter);
            var testCases = LoadTestCases(service, filter, trace);

            if (offset >= testCases.Count)
            {
                // All done
                FinishRun(service, trace, testRunId);
                return;
            }

            trace.Trace("Continuing batch from offset {0}, total {1}", offset, testCases.Count);
            var keepRecords = testRun.GetAttributeValue<bool>(FldKeepRecords);
            RunBatch(service, trace, testRunId, testCases, offset, keepRecords);
        }
        catch (Exception ex)
        {
            trace.Trace("ContinueBatch error: {0}", ex.Message);
            SetError(service, testRunId, ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Run a batch of test cases
    // ════════════════════════════════════════════════════════════════

    private void RunBatch(
        IOrganizationService service, ITracingService trace,
        Guid testRunId, List<TestCase> allTests, int offset, bool keepRecords)
    {
        var batch = allTests.Skip(offset).Take(BatchSize).ToList();
        var runner = new TestRunner(service) { KeepRecords = keepRecords };
        var result = runner.RunAll(batch);

        // Write result records
        WriteResultRecords(service, testRunId, result, trace);

        // Accumulate counters (read current, add batch)
        var current = service.Retrieve(TestRunEntity, testRunId,
            new ColumnSet(FldPassed, FldFailed, FldTotal));
        var prevPassed = current.GetAttributeValue<int?>(FldPassed) ?? 0;
        var prevFailed = current.GetAttributeValue<int?>(FldFailed) ?? 0;
        var prevTotal = current.GetAttributeValue<int?>(FldTotal) ?? 0;

        var newOffset = offset + batch.Count;
        var isLastBatch = newOffset >= allTests.Count;

        var summary = new StringBuilder();
        summary.AppendLine($"Batch {offset + 1}-{newOffset} von {allTests.Count}:");
        summary.AppendLine($"  Dieser Batch: {result.PassedCount}/{batch.Count} bestanden");
        summary.AppendLine($"  Gesamt bisher: {prevPassed + result.PassedCount}/{prevTotal + result.TotalCount}");

        foreach (var tc in result.Results)
        {
            var icon = tc.Outcome switch
            {
                TestOutcome.Passed => "[OK]",
                TestOutcome.Failed => "[FAIL]",
                TestOutcome.Error => "[ERR]",
                TestOutcome.Skipped => "[SKIP]",
                _ => "[?]"
            };
            summary.AppendLine($"{icon} {tc.TestId}: {tc.Title} ({tc.DurationMs}ms)");
        }

        if (isLastBatch)
        {
            // All done: set Completed
            var totalPassed = prevPassed + result.PassedCount;
            var totalFailed = prevFailed + result.FailedCount + result.ErrorCount;
            var totalCount = prevTotal + result.TotalCount;

            var finalUpdate = new Entity(TestRunEntity, testRunId)
            {
                [FldStatus] = new OptionSetValue(StatusCompleted),
                [FldSummary] = Truncate($"{totalPassed}/{totalCount} bestanden, {totalFailed} fehlgeschlagen\n\n{summary}", 4000),
                [FldCompletedOn] = DateTime.UtcNow,
                [FldTotal] = totalCount,
                [FldPassed] = totalPassed,
                [FldFailed] = totalFailed,
                [FldBatchOffset] = 0
            };
            service.Update(finalUpdate);
            trace.Trace("All batches complete: {0}/{1} passed", totalPassed, totalCount);
        }
        else
        {
            // More batches: update counters and trigger next batch
            var progressUpdate = new Entity(TestRunEntity, testRunId)
            {
                [FldSummary] = Truncate($"Wird ausgeführt... {summary}", 4000),
                [FldTotal] = prevTotal + result.TotalCount,
                [FldPassed] = prevPassed + result.PassedCount,
                [FldFailed] = prevFailed + result.FailedCount + result.ErrorCount,
                [FldBatchOffset] = newOffset  // This triggers the Update step for next batch
            };
            service.Update(progressUpdate);
            trace.Trace("Batch done, next offset: {0}", newOffset);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Finish run: build final summary from all results
    // ════════════════════════════════════════════════════════════════

    private void FinishRun(
        IOrganizationService service, ITracingService trace, Guid testRunId)
    {
        var current = service.Retrieve(TestRunEntity, testRunId,
            new ColumnSet(FldPassed, FldFailed, FldTotal));

        var finalUpdate = new Entity(TestRunEntity, testRunId)
        {
            [FldStatus] = new OptionSetValue(StatusCompleted),
            [FldCompletedOn] = DateTime.UtcNow,
            [FldBatchOffset] = 0
        };
        service.Update(finalUpdate);
        trace.Trace("Run finished");
    }

    // ════════════════════════════════════════════════════════════════
    //  Load test cases
    // ════════════════════════════════════════════════════════════════

    private static List<TestCase> LoadTestCases(
        IOrganizationService service, string? filter, ITracingService trace)
    {
        var query = new QueryExpression(TestCaseEntity)
        {
            ColumnSet = new ColumnSet(FldTcTestId, FldTcTitle, FldTcDefinition, FldTcEnabled),
            NoLock = true
        };
        query.Criteria.AddCondition(FldTcEnabled, ConditionOperator.Equal, true);

        var records = service.RetrieveMultiple(query);
        var testCases = new List<TestCase>();

        foreach (var record in records.Entities)
        {
            var testId = record.GetAttributeValue<string>(FldTcTestId);
            var defJson = record.GetAttributeValue<string>(FldTcDefinition);
            if (string.IsNullOrWhiteSpace(defJson)) continue;

            try
            {
                // MetadataPropertyHandling.Ignore: verhindert dass Newtonsoft.Json
                // "$type" Properties als Type-Hints behandelt und aus JObjects entfernt.
                // Notwendig für das ExecuteRequest $type-System.
                var settings = new JsonSerializerSettings
                {
                    MetadataPropertyHandling = MetadataPropertyHandling.Ignore
                };
                var tc = JsonConvert.DeserializeObject<TestCase>(defJson, settings);
                if (tc != null)
                {
                    tc.Id = testId ?? tc.Id;
                    tc.Title = record.GetAttributeValue<string>(FldTcTitle) ?? tc.Title;
                    testCases.Add(tc);
                }
            }
            catch (Exception ex)
            {
                trace.Trace("Error deserializing {0}: {1}", testId, ex.Message);
            }
        }

        return ApplyFilter(testCases, filter);
    }

    private static List<TestCase> ApplyFilter(List<TestCase> testCases, string? filter)
    {
        var enabled = testCases.Where(tc => tc.Enabled);

        if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "*")
            return enabled.ToList();

        var trimmed = filter.Trim();

        if (trimmed.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
        {
            var tag = trimmed.Substring("tag:".Length).Trim();
            return enabled.Where(tc => tc.Tags.Any(t =>
                t.Equals(tag, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        if (trimmed.StartsWith("category:", StringComparison.OrdinalIgnoreCase))
        {
            var cat = trimmed.Substring("category:".Length).Trim();
            return enabled.Where(tc => (tc.Category ?? "")
                .Equals(cat, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var ids = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).ToArray();
        return enabled.Where(tc => ids.Any(p => MatchesPattern(tc.Id, p))).ToList();
    }

    private static bool MatchesPattern(string testId, string pattern)
    {
        if (pattern.EndsWith("*"))
            return testId.StartsWith(pattern.Substring(0, pattern.Length - 1),
                StringComparison.OrdinalIgnoreCase);
        return testId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    // ════════════════════════════════════════════════════════════════
    //  Delete old results
    // ════════════════════════════════════════════════════════════════

    private static void DeleteOldResults(
        IOrganizationService service, Guid testRunId, ITracingService trace)
    {
        // Delete steps first (child of results)
        var stepQuery = new QueryExpression(TestStepEntity) { ColumnSet = new ColumnSet(false), NoLock = true };
        var stepLink = stepQuery.AddLink(TestRunResultEntity, FldStepRunResult, "jbe_testrunresultid");
        stepLink.LinkCriteria.AddCondition(FldResultTestRun, ConditionOperator.Equal, testRunId);

        var steps = service.RetrieveMultiple(stepQuery);
        foreach (var step in steps.Entities)
            service.Delete(TestStepEntity, step.Id);

        // Delete results
        var resultQuery = new QueryExpression(TestRunResultEntity) { ColumnSet = new ColumnSet(false), NoLock = true };
        resultQuery.Criteria.AddCondition(FldResultTestRun, ConditionOperator.Equal, testRunId);

        var results = service.RetrieveMultiple(resultQuery);
        foreach (var result in results.Entities)
            service.Delete(TestRunResultEntity, result.Id);

        if (steps.Entities.Count > 0 || results.Entities.Count > 0)
            trace.Trace("Deleted {0} steps + {1} results", steps.Entities.Count, results.Entities.Count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Write result records
    // ════════════════════════════════════════════════════════════════

    private static void WriteResultRecords(
        IOrganizationService service, Guid testRunId,
        TestRunResult result, ITracingService trace)
    {
        var testRunRef = new EntityReference(TestRunEntity, testRunId);

        foreach (var tcResult in result.Results)
        {
            // jbe_assertionresults aus den Assert-StepResults generieren
            // (Kompat fuer UI-Code der den JSON-Blob parst).
            string assertionsJson = "[]";
            try
            {
                var assertSteps = tcResult.StepResults
                    .Where(s => string.Equals(s.Action, "Assert", StringComparison.OrdinalIgnoreCase))
                    .Select(s => new
                    {
                        description = s.Description,
                        passed = s.Success,
                        message = s.Message,
                        expectedDisplay = s.ExpectedDisplay,
                        actualDisplay = s.ActualDisplay
                    })
                    .ToList();
                assertionsJson = JsonConvert.SerializeObject(assertSteps, JsonSettings);
            }
            catch { /* fallback: leer */ }

            var resultRecord = new Entity(TestRunResultEntity)
            {
                [FldResultTestId] = tcResult.TestId,
                [FldResultOutcome] = new OptionSetValue(MapOutcome(tcResult.Outcome)),
                [FldResultDuration] = (int)tcResult.DurationMs,
                [FldResultError] = Truncate(tcResult.ErrorMessage, 4000),
                [FldResultAssertions] = Truncate(assertionsJson, 100000),
                [FldResultTestRun] = testRunRef
            };

            var resultId = service.Create(resultRecord);
            var resultRef = new EntityReference(TestRunResultEntity, resultId);

            // ADR-0004: einheitliche Persistenz-Schleife, ein jbe_teststep
            // pro StepResult.
            foreach (var stepResult in tcResult.StepResults)
            {
                try
                {
                    var step = new Entity(TestStepEntity)
                    {
                        [FldStepNumber] = stepResult.StepNumber,
                        [FldStepAction] = Truncate(stepResult.Action ?? "", 100),
                        [FldStepDuration] = (int)stepResult.DurationMs,
                        [FldStepError] = Truncate(stepResult.Message, 4000),
                        [FldStepStatus] = new OptionSetValue(stepResult.Success ? StepPassed : StepFailed),
                        [FldStepRunResult] = resultRef
                    };
                    if (!string.IsNullOrEmpty(stepResult.Alias))
                        step[FldStepAlias] = Truncate(stepResult.Alias, 100);
                    if (!string.IsNullOrEmpty(stepResult.Entity))
                        step[FldStepEntity] = Truncate(stepResult.Entity, 100);
                    if (stepResult.RecordId.HasValue)
                        step[FldStepRecordId] = stepResult.RecordId.Value.ToString();
                    if (!string.IsNullOrEmpty(stepResult.AssertField))
                        step[FldStepAssertionField] = Truncate(stepResult.AssertField, 500);
                    if (!string.IsNullOrEmpty(stepResult.ExpectedDisplay))
                        step[FldStepExpected] = Truncate(stepResult.ExpectedDisplay, 4000);
                    if (!string.IsNullOrEmpty(stepResult.ActualDisplay))
                        step[FldStepActual] = Truncate(stepResult.ActualDisplay, 4000);
                    if (!string.IsNullOrEmpty(stepResult.InputData))
                        step[FldStepInputData] = Truncate(stepResult.InputData, 100000);
                    if (!string.IsNullOrEmpty(stepResult.OutputData))
                        step[FldStepOutputData] = Truncate(stepResult.OutputData, 100000);
                    service.Create(step);
                }
                catch { /* non-critical: ein Fehler bei einem Step-Log soll den
                           Test-Run nicht abbrechen. */ }
            }
        }

        trace.Trace("Wrote {0} result records", result.Results.Count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private static void SetError(IOrganizationService service, Guid testRunId, string message)
    {
        try
        {
            service.Update(new Entity(TestRunEntity, testRunId)
            {
                [FldStatus] = new OptionSetValue(StatusFailed),
                [FldSummary] = Truncate($"Fehler: {message}", 4000),
                [FldCompletedOn] = DateTime.UtcNow,
                [FldBatchOffset] = 0
            });
        }
        catch { /* Error update is non-critical */ }
    }

    private static int MapOutcome(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => OutcomePassed,
        TestOutcome.Failed => OutcomeFailed,
        TestOutcome.Error => OutcomeError,
        TestOutcome.Skipped => OutcomeSkipped,
        _ => OutcomeError
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
