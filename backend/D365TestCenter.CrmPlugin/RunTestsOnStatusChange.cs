using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using D365TestCenter.Core;
using System.Text;
using Newtonsoft.Json;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Plugin on jbe_testrun: triggers test execution when status changes to "Geplant" (Planned).
///
/// Supports two scenarios:
///   1. New TestRun: PostCreate with status "Geplant" starts execution
///   2. Retrigger:   PostUpdate status change to "Geplant" deletes old results, restarts execution
///
/// Registration:
///   Entity:              jbe_testrun
///   Message:             Create (PostOperation, Async)
///   Message:             Update (PostOperation, Async)
///   FilteringAttributes: jbe_teststatus (Update only)
///
/// The plugin reads test cases from jbe_testcase records (filtered by jbe_testcasefilter),
/// executes them using TestRunner, and writes results back to jbe_testrunresult + jbe_teststep.
/// </summary>
public sealed class RunTestsOnStatusChange : IPlugin
{
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

    // ── TestRunResult Fields ─────────────────────────────────────
    private const string FldResultTestId = "jbe_testid";
    private const string FldResultOutcome = "jbe_outcome";
    private const string FldResultDuration = "jbe_durationms";
    private const string FldResultError = "jbe_errormessage";
    private const string FldResultAssertions = "jbe_assertionresults";
    private const string FldResultTracked = "jbe_trackedrecords";
    private const string FldResultTestRun = "jbe_testrunid";

    // ── TestStep Fields ──────────────────────────────────────────
    private const string FldStepNumber = "jbe_stepnumber";
    private const string FldStepAction = "jbe_action";
    private const string FldStepEntity = "jbe_entity";
    private const string FldStepAlias = "jbe_alias";
    private const string FldStepRecordId = "jbe_recordid";
    private const string FldStepRecordUrl = "jbe_recordurl";
    private const string FldStepAssertionField = "jbe_assertionfield";
    private const string FldStepAssertionOp = "jbe_assertionoperator";
    private const string FldStepExpected = "jbe_expectedvalue";
    private const string FldStepActual = "jbe_actualvalue";
    private const string FldStepDuration = "jbe_durationms";
    private const string FldStepInput = "jbe_inputdata";
    private const string FldStepOutput = "jbe_outputdata";
    private const string FldStepError = "jbe_errormessage";
    private const string FldStepPhase = "jbe_phase";
    private const string FldStepStatus = "jbe_stepstatus";
    private const string FldStepRunResult = "jbe_testrunresultid";

    // ── TestCase Fields ──────────────────────────────────────────
    private const string FldTcTestId = "jbe_testid";
    private const string FldTcTitle = "jbe_title";
    private const string FldTcDefinition = "jbe_definitionjson";
    private const string FldTcEnabled = "jbe_enabled";
    private const string FldTcTags = "jbe_tags";
    private const string FldTcCategory = "jbe_category";

    // ── Status OptionSet Values (jbe_teststatus) ─────────────────
    private const int StatusPlanned = 105710000;
    private const int StatusRunning = 105710001;
    private const int StatusCompleted = 105710002;
    private const int StatusFailed = 105710003;

    // ── Outcome OptionSet Values (jbe_testoutcome) ───────────────
    private const int OutcomePassed = 105710000;
    private const int OutcomeFailed = 105710001;
    private const int OutcomeError = 105710002;
    private const int OutcomeSkipped = 105710003;

    // ── Step Phase OptionSet (jbe_stepphase) ─────────────────────
    private const int PhasePrecondition = 105710000;
    private const int PhaseExecution = 105710001;
    private const int PhaseAssertion = 105710002;

    // ── Step Status OptionSet (jbe_stepstatus) ───────────────────
    private const int StepPassed = 105710000;
    private const int StepFailed = 105710001;
    private const int StepError = 105710002;
    private const int StepSkipped = 105710003;

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
        var service = factory.CreateOrganizationService(context.UserId);

        // ── Guard: Only fire on jbe_testrun ──────────────────────
        if (context.PrimaryEntityName != TestRunEntity)
            return;

        // ── Guard: Depth check (prevent recursion from our own updates) ──
        if (context.Depth > 2)
        {
            tracingService.Trace("RunTestsOnStatusChange: Depth {0} > 2, skipping", context.Depth);
            return;
        }

        // ── Read the TestRun record ──────────────────────────────
        var testRunId = context.PrimaryEntityId;
        var testRun = service.Retrieve(TestRunEntity, testRunId,
            new ColumnSet(FldStatus, FldFilter, FldKeepRecords));

        var status = testRun.GetAttributeValue<OptionSetValue>(FldStatus);
        if (status == null || status.Value != StatusPlanned)
        {
            tracingService.Trace("RunTestsOnStatusChange: Status is not 'Planned' ({0}), skipping",
                status?.Value.ToString() ?? "null");
            return;
        }

        // ── Guard: On Update, only fire when status actually changed to Planned ──
        if (context.MessageName == "Update")
        {
            if (context.PreEntityImages.Contains("PreImage"))
            {
                var preImage = context.PreEntityImages["PreImage"];
                var oldStatus = preImage.GetAttributeValue<OptionSetValue>(FldStatus);
                if (oldStatus != null && oldStatus.Value == StatusPlanned)
                {
                    tracingService.Trace("RunTestsOnStatusChange: Status was already 'Planned', skipping");
                    return;
                }
            }
        }

        tracingService.Trace("RunTestsOnStatusChange: Triggered for TestRun {0}", testRunId);

        try
        {
            // ── Set status to Running ────────────────────────────
            var runningUpdate = new Entity(TestRunEntity, testRunId)
            {
                [FldStatus] = new OptionSetValue(StatusRunning),
                [FldSummary] = "Testausführung gestartet...",
                [FldStartedOn] = DateTime.UtcNow,
                [FldTotal] = 0,
                [FldPassed] = 0,
                [FldFailed] = 0
            };
            service.Update(runningUpdate);

            // ── Delete old results (retrigger scenario) ──────────
            DeleteOldResults(service, testRunId, tracingService);

            // ── Load test cases from jbe_testcase ────────────────
            var filter = testRun.GetAttributeValue<string>(FldFilter);
            var testCases = LoadTestCases(service, filter, tracingService);

            if (testCases.Count == 0)
            {
                var msg = $"Keine Testfälle gefunden (Filter: {filter ?? "*"})";
                tracingService.Trace(msg);
                var emptyUpdate = new Entity(TestRunEntity, testRunId)
                {
                    [FldStatus] = new OptionSetValue(StatusCompleted),
                    [FldSummary] = msg,
                    [FldCompletedOn] = DateTime.UtcNow,
                    [FldTotal] = 0
                };
                service.Update(emptyUpdate);
                return;
            }

            tracingService.Trace("Loaded {0} test cases", testCases.Count);

            // ── Execute tests ────────────────────────────────────
            var runner = new TestRunner(service);

            runner.OnTestCompleted += (index, total, tcResult) =>
            {
                try
                {
                    var progress = $"[{index}/{total}] {tcResult.TestId}: {tcResult.Outcome}";
                    var progressUpdate = new Entity(TestRunEntity, testRunId)
                    {
                        [FldSummary] = $"Wird ausgeführt... {progress}"
                    };
                    service.Update(progressUpdate);
                }
                catch
                {
                    // Progress update is non-critical
                }
            };

            var result = runner.RunAll(testCases);

            // ── Write results to jbe_testrunresult + jbe_teststep ──
            WriteResultRecords(service, testRunId, result, tracingService);

            // ── Update TestRun with final results ────────────────
            var summary = BuildSummary(result);
            var finalUpdate = new Entity(TestRunEntity, testRunId)
            {
                [FldStatus] = new OptionSetValue(StatusCompleted),
                [FldSummary] = Truncate(summary, 4000),
                [FldFullLog] = Truncate(result.FullLog, 1000000),
                [FldCompletedOn] = DateTime.UtcNow,
                [FldTotal] = result.TotalCount,
                [FldPassed] = result.PassedCount,
                [FldFailed] = result.FailedCount
            };
            service.Update(finalUpdate);

            tracingService.Trace("RunTestsOnStatusChange completed: {0}",
                Truncate(summary, 500));
        }
        catch (Exception ex)
        {
            tracingService.Trace("RunTestsOnStatusChange error: {0}", ex.Message);

            try
            {
                var errorUpdate = new Entity(TestRunEntity, testRunId)
                {
                    [FldStatus] = new OptionSetValue(StatusFailed),
                    [FldSummary] = Truncate($"Fehler: {ex.Message}", 4000),
                    [FldCompletedOn] = DateTime.UtcNow
                };
                service.Update(errorUpdate);
            }
            catch
            {
                // Error update is non-critical
            }

            // Do NOT rethrow - async plugins that throw show ugly system errors.
            // The error is captured in the TestRun record.
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Load test cases from jbe_testcase entity
    // ════════════════════════════════════════════════════════════════

    private static List<TestCase> LoadTestCases(
        IOrganizationService service, string? filter, ITracingService trace)
    {
        var query = new QueryExpression(TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                FldTcTestId, FldTcTitle, FldTcDefinition,
                FldTcEnabled, FldTcTags, FldTcCategory),
            NoLock = true
        };
        query.Criteria.AddCondition(FldTcEnabled, ConditionOperator.Equal, true);

        var records = service.RetrieveMultiple(query);
        trace.Trace("Found {0} enabled test case records", records.Entities.Count);

        var testCases = new List<TestCase>();

        foreach (var record in records.Entities)
        {
            var testId = record.GetAttributeValue<string>(FldTcTestId);
            var definitionJson = record.GetAttributeValue<string>(FldTcDefinition);

            if (string.IsNullOrWhiteSpace(definitionJson))
            {
                trace.Trace("Skipping {0}: no definition JSON", testId);
                continue;
            }

            try
            {
                var tc = JsonConvert.DeserializeObject<TestCase>(definitionJson);
                if (tc != null)
                {
                    // Ensure ID and title from record fields (authoritative)
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

        // Apply filter
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
            return enabled
                .Where(tc => tc.Tags.Any(t =>
                    t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (trimmed.StartsWith("category:", StringComparison.OrdinalIgnoreCase))
        {
            var category = trimmed.Substring("category:".Length).Trim();
            return enabled
                .Where(tc => (tc.Category ?? "")
                    .Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Comma-separated test case IDs or wildcard patterns: "STD*", "TC01,TC02"
        var ids = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();

        return enabled
            .Where(tc => ids.Any(pattern =>
                MatchesPattern(tc.Id, pattern)))
            .ToList();
    }

    private static bool MatchesPattern(string testId, string pattern)
    {
        if (pattern.EndsWith("*"))
        {
            var prefix = pattern.Substring(0, pattern.Length - 1);
            return testId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return testId.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    // ════════════════════════════════════════════════════════════════
    //  Delete old results (for retrigger scenario)
    // ════════════════════════════════════════════════════════════════

    private static void DeleteOldResults(
        IOrganizationService service, Guid testRunId, ITracingService trace)
    {
        // Delete jbe_teststep records first (child of testrunresult)
        var stepQuery = new QueryExpression(TestStepEntity)
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };
        var stepLink = stepQuery.AddLink(
            TestRunResultEntity, FldStepRunResult, "jbe_testrunresultid");
        stepLink.LinkCriteria.AddCondition(
            FldResultTestRun, ConditionOperator.Equal, testRunId);

        var steps = service.RetrieveMultiple(stepQuery);
        foreach (var step in steps.Entities)
        {
            service.Delete(TestStepEntity, step.Id);
        }

        if (steps.Entities.Count > 0)
            trace.Trace("Deleted {0} old test step records", steps.Entities.Count);

        // Delete jbe_testrunresult records
        var resultQuery = new QueryExpression(TestRunResultEntity)
        {
            ColumnSet = new ColumnSet(false),
            NoLock = true
        };
        resultQuery.Criteria.AddCondition(
            FldResultTestRun, ConditionOperator.Equal, testRunId);

        var results = service.RetrieveMultiple(resultQuery);
        foreach (var result in results.Entities)
        {
            service.Delete(TestRunResultEntity, result.Id);
        }

        if (results.Entities.Count > 0)
            trace.Trace("Deleted {0} old test run result records", results.Entities.Count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Write result records (jbe_testrunresult + jbe_teststep)
    // ════════════════════════════════════════════════════════════════

    private static void WriteResultRecords(
        IOrganizationService service, Guid testRunId,
        TestRunResult result, ITracingService trace)
    {
        var testRunRef = new EntityReference(TestRunEntity, testRunId);

        foreach (var tcResult in result.Results)
        {
            // Create jbe_testrunresult
            var resultRecord = new Entity(TestRunResultEntity)
            {
                [FldResultTestId] = tcResult.TestId,
                [FldResultOutcome] = new OptionSetValue(MapOutcome(tcResult.Outcome)),
                [FldResultDuration] = (int)tcResult.DurationMs,
                [FldResultError] = Truncate(tcResult.ErrorMessage, 4000),
                [FldResultAssertions] = Truncate(
                    JsonConvert.SerializeObject(tcResult.Assertions, JsonSettings), 100000),
                [FldResultTestRun] = testRunRef
            };

            var resultId = service.Create(resultRecord);
            var resultRef = new EntityReference(TestRunResultEntity, resultId);

            // Create jbe_teststep records for each step result
            foreach (var stepResult in tcResult.StepResults)
            {
                var stepRecord = new Entity(TestStepEntity)
                {
                    [FldStepNumber] = stepResult.StepNumber,
                    [FldStepAction] = Truncate(stepResult.Description, 500),
                    [FldStepDuration] = (int)stepResult.DurationMs,
                    [FldStepError] = Truncate(stepResult.Message, 4000),
                    [FldStepPhase] = new OptionSetValue(PhaseExecution),
                    [FldStepStatus] = new OptionSetValue(
                        stepResult.Success ? StepPassed : StepFailed),
                    [FldStepRunResult] = resultRef
                };
                service.Create(stepRecord);
            }

            // Create jbe_teststep records for each assertion result
            int assertionIndex = 0;
            foreach (var assertion in tcResult.Assertions)
            {
                assertionIndex++;
                var assertionStep = new Entity(TestStepEntity)
                {
                    [FldStepNumber] = 1000 + assertionIndex,
                    [FldStepAction] = "Assertion",
                    [FldStepAssertionField] = Truncate(assertion.Description, 500),
                    [FldStepExpected] = Truncate(assertion.ExpectedDisplay, 4000),
                    [FldStepActual] = Truncate(assertion.ActualDisplay, 4000),
                    [FldStepError] = Truncate(assertion.Message, 4000),
                    [FldStepPhase] = new OptionSetValue(PhaseAssertion),
                    [FldStepStatus] = new OptionSetValue(
                        assertion.Passed ? StepPassed : StepFailed),
                    [FldStepRunResult] = resultRef
                };
                service.Create(assertionStep);
            }
        }

        trace.Trace("Created {0} result records with steps", result.Results.Count);
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private static int MapOutcome(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => OutcomePassed,
        TestOutcome.Failed => OutcomeFailed,
        TestOutcome.Error => OutcomeError,
        TestOutcome.Skipped => OutcomeSkipped,
        _ => OutcomeError
    };

    private static string BuildSummary(TestRunResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalSeconds;
        var sb = new StringBuilder();

        sb.AppendLine(
            $"{result.PassedCount}/{result.TotalCount} bestanden, " +
            $"{result.FailedCount} fehlgeschlagen, " +
            $"{result.ErrorCount} Fehler ({duration:F1}s)");

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

            sb.AppendLine($"{icon} {tc.TestId}: {tc.Title} ({tc.DurationMs}ms)");

            if (!string.IsNullOrWhiteSpace(tc.ErrorMessage))
                sb.AppendLine($"  Fehler: {tc.ErrorMessage}");
        }

        return sb.ToString();
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
