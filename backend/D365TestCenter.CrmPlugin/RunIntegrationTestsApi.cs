using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using D365TestCenter.Core;
using System.Text;
using Newtonsoft.Json;

namespace D365TestCenter.CrmPlugin;

/// <summary>
/// Custom API: itt_RunIntegrationTests
/// Liest einen itt_testrun-Record, deserialisiert die Testfälle,
/// filtert nach Konfiguration, führt die Tests aus und schreibt das Ergebnis zurück.
///
/// Registrierung:
///   MessageName: itt_RunIntegrationTests
///   Binding:     Unbound
///   Input:       TestRunId (EntityReference auf itt_testrun)
///   Output:      Success (bool), ResultJson (string), Summary (string)
/// </summary>
public sealed class RunIntegrationTestsApi : IPlugin
{
    private const string TestRunEntity = "itt_testrun";

    // ── Felder: Konfiguration ─────────────────────────────────────
    private const string TestRunConfigField = "itt_testconfig_json";
    private const string TestRunFilterField = "itt_testcasefilter";
    private const string TestRunEnvironmentField = "itt_environment";

    // ── Felder: Status & Ergebnis ─────────────────────────────────
    private const string TestRunStatusField = "itt_teststatus";
    private const string TestRunSummaryField = "itt_testsummary";
    private const string TestRunResultField = "itt_testresult_json";
    private const string TestRunFullLogField = "itt_fulllog";

    // ── Felder: Statistiken ───────────────────────────────────────
    private const string TestRunStartedOnField = "itt_started_on";
    private const string TestRunCompletedOnField = "itt_completed_on";
    private const string TestRunTotalField = "itt_total";
    private const string TestRunPassedField = "itt_passed";
    private const string TestRunFailedField = "itt_failed";

    // ── Status-OptionSet-Werte (Publisher-Prefix: 595300xxx) ──
    private const int StatusGeplant = 595300000;
    private const int StatusLäuft = 595300001;
    private const int StatusAbgeschlossen = 595300002;
    private const int StatusFehler = 595300003;

    private static readonly JsonSerializerSettings JsonWriteSettings = new()
    {
        Formatting = Formatting.Indented,
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

        tracingService.Trace("RunIntegrationTestsApi: Start");

        Guid testRunId = Guid.Empty;

        try
        {
            // ── Input lesen ───────────────────────────────────────
            if (!context.InputParameters.Contains("TestRunId"))
                throw new InvalidPluginExecutionException(
                    "Input-Parameter 'TestRunId' fehlt.");

            var testRunRef = context.InputParameters["TestRunId"]
                as EntityReference
                ?? throw new InvalidPluginExecutionException(
                    "TestRunId muss ein EntityReference sein.");

            testRunId = testRunRef.Id;
            tracingService.Trace("TestRun-ID: {0}", testRunId);

            // ── TestRun-Record laden ──────────────────────────────
            var testRunRecord = service.Retrieve(
                TestRunEntity, testRunId,
                new ColumnSet(
                    TestRunConfigField, TestRunStatusField,
                    TestRunFilterField, TestRunEnvironmentField));

            var configJson = testRunRecord
                .GetAttributeValue<string>(TestRunConfigField);
            if (string.IsNullOrWhiteSpace(configJson))
                throw new InvalidPluginExecutionException(
                    "Testkonfiguration (JSON) ist leer.");

            var filter = testRunRecord
                .GetAttributeValue<string>(TestRunFilterField);
            var environment = testRunRecord
                .GetAttributeValue<OptionSetValue>(TestRunEnvironmentField);

            tracingService.Trace("Filter: {0}, Umgebung: {1}",
                filter ?? "*",
                environment?.Value.ToString() ?? "nicht gesetzt");

            // ── Status auf "Läuft" setzen ─────────────────────────
            UpdateTestRunStatus(service, testRunId, StatusLäuft,
                "Testausführung gestartet...", DateTime.UtcNow);

            // ── Testfälle deserialisieren und filtern ──────────────
            var testCases = DeserializeAndFilter(
                configJson, filter, tracingService);

            tracingService.Trace(
                "Testfälle nach Filter: {0}", testCases.Count);

            if (testCases.Count == 0)
            {
                var msg = $"Keine Testfälle gefunden (Filter: {filter ?? "*"})";
                WriteTestRunResult(service, testRunId,
                    StatusAbgeschlossen, msg, "{}", "", 0, 0, 0, 0);
                SetOutput(context, true, "{}", msg);
                return;
            }

            // ── Tests ausführen ───────────────────────────────────
            var runner = new TestRunner(service);

            runner.OnTestCompleted += (index, total, tcResult) =>
            {
                try
                {
                    var progress =
                        $"[{index}/{total}] {tcResult.TestId}: {tcResult.Outcome}";
                    UpdateTestRunProgress(
                        service, testRunId, progress, index, total);
                }
                catch
                {
                    // Fortschritts-Update ist nicht kritisch
                }
            };

            var result = runner.RunAll(testCases);

            // ── Ergebnis zurückschreiben ───────────────────────────
            var resultJson = JsonConvert.SerializeObject(
                result, JsonWriteSettings);
            var summary = BuildSummary(result);

            WriteTestRunResult(service, testRunId,
                StatusAbgeschlossen, summary, resultJson, result.FullLog,
                result.TotalCount, result.PassedCount,
                result.FailedCount, result.ErrorCount);

            // ── Output-Parameter ──────────────────────────────────
            SetOutput(context, true, resultJson, summary);

            tracingService.Trace(
                "RunIntegrationTestsApi abgeschlossen: {0}", summary);
        }
        catch (InvalidPluginExecutionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            tracingService.Trace(
                "RunIntegrationTestsApi Fehler: {0}", ex.Message);

            if (testRunId != Guid.Empty)
            {
                try
                {
                    UpdateTestRunStatus(service, testRunId,
                        StatusFehler, $"Fehler: {ex.Message}");
                }
                catch
                {
                    // Fehler beim Status-Update nicht propagieren
                }
            }

            SetOutput(context, false, null, $"Fehler: {ex.Message}");

            throw new InvalidPluginExecutionException(
                "Fehler bei der Testausführung: " + ex.Message, ex);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Deserialisierung und Filterung
    // ════════════════════════════════════════════════════════════════

    private static List<TestCase> DeserializeAndFilter(
        string json, string? filter, ITracingService trace)
    {
        List<TestCase> testCases;

        try
        {
            json = json.TrimStart();

            if (json.StartsWith("["))
            {
                // Flache Liste von Testfällen
                testCases = JsonConvert.DeserializeObject<List<TestCase>>(
                    json) ?? new List<TestCase>();
                trace.Trace(
                    "Testfälle als Array deserialisiert: {0}",
                    testCases.Count);
            }
            else
            {
                // TestSuiteDefinition-Format: { suiteName, testCases: [...] }
                var suite = JsonConvert.DeserializeObject<TestSuiteDefinition>(
                    json);

                if (suite?.TestCases?.Count > 0)
                {
                    testCases = suite.TestCases;
                    trace.Trace(
                        "TestSuite deserialisiert: {0} ({1} Testfälle)",
                        suite.SuiteName ?? "?", testCases.Count);
                }
                else
                {
                    testCases = new List<TestCase>();
                    trace.Trace("TestSuite enthält keine Testfälle");
                }
            }
        }
        catch (JsonReaderException ex)
        {
            trace.Trace(
                "JSON-Deserialisierung fehlgeschlagen: {0}", ex.Message);
            throw new InvalidPluginExecutionException(
                $"Ungültiges Testfall-JSON: {ex.Message}", ex);
        }

        return ApplyFilter(testCases, filter);
    }

    /// <summary>
    /// Filtert Testfälle anhand des itt_testcasefilter-Felds.
    /// Unterstützte Formate:
    ///   "*" oder leer    → alle aktivierten Tests
    ///   "TC01,TC02"      → nur diese IDs
    ///   "tag:LUW"        → alle mit Tag "LUW"
    ///   "category:Bridge" → alle in Kategorie "Bridge"
    /// </summary>
    private static List<TestCase> ApplyFilter(
        List<TestCase> testCases, string? filter)
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

        // Komma-getrennte Testfall-IDs: "TC01,TC02,BTC05"
        var ids = trimmed.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();

        return enabled
            .Where(tc => ids.Any(id =>
                tc.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════
    //  TestRun-Record aktualisieren
    // ════════════════════════════════════════════════════════════════

    private static void UpdateTestRunStatus(
        IOrganizationService service, Guid testRunId,
        int status, string summary, DateTime? startedOn = null)
    {
        var update = new Entity(TestRunEntity, testRunId)
        {
            [TestRunStatusField] = new OptionSetValue(status),
            [TestRunSummaryField] = Truncate(summary, 4000)
        };

        if (startedOn.HasValue)
            update[TestRunStartedOnField] = startedOn.Value;

        service.Update(update);
    }

    private static void UpdateTestRunProgress(
        IOrganizationService service, Guid testRunId,
        string progressText, int current, int total)
    {
        var update = new Entity(TestRunEntity, testRunId)
        {
            [TestRunSummaryField] = Truncate(
                $"Läuft... {progressText}", 4000)
        };

        service.Update(update);
    }

    private static void WriteTestRunResult(
        IOrganizationService service, Guid testRunId,
        int status, string summary, string resultJson, string fullLog,
        int total, int passed, int failed, int errors)
    {
        var update = new Entity(TestRunEntity, testRunId)
        {
            [TestRunStatusField] = new OptionSetValue(status),
            [TestRunSummaryField] = Truncate(summary, 4000),
            [TestRunResultField] = Truncate(resultJson, 1000000),
            [TestRunFullLogField] = Truncate(fullLog, 1000000),
            [TestRunCompletedOnField] = DateTime.UtcNow,
            [TestRunTotalField] = total,
            [TestRunPassedField] = passed,
            [TestRunFailedField] = failed
        };

        service.Update(update);
    }

    // ════════════════════════════════════════════════════════════════
    //  Zusammenfassung erstellen
    // ════════════════════════════════════════════════════════════════

    private static string BuildSummary(TestRunResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalSeconds;
        var sb = new StringBuilder();

        sb.AppendLine(
            $"{result.PassedCount}/{result.TotalCount} bestanden, " +
            $"{result.FailedCount} fehlgeschlagen, " +
            $"{result.ErrorCount} Fehler ({duration:F1}s)");
        sb.AppendLine();

        foreach (var tc in result.Results)
        {
            var icon = tc.Outcome switch
            {
                TestOutcome.Passed => "✓",
                TestOutcome.Failed => "✗",
                TestOutcome.Error => "⚠",
                TestOutcome.Skipped => "○",
                _ => "?"
            };

            sb.AppendLine(
                $"{icon} [{tc.TestId}] {tc.Title} — " +
                $"{tc.Outcome} ({tc.DurationMs}ms)");

            if (!string.IsNullOrWhiteSpace(tc.ErrorMessage))
                sb.AppendLine($"  Fehler: {tc.ErrorMessage}");

            foreach (var a in tc.Assertions.Where(a => !a.Passed))
                sb.AppendLine($"  ✗ {a.Description}: {a.Message}");
        }

        return sb.ToString();
    }

    // ════════════════════════════════════════════════════════════════
    //  Output-Parameter und Hilfsmethoden
    // ════════════════════════════════════════════════════════════════

    private static void SetOutput(
        IPluginExecutionContext context,
        bool success, string? resultJson, string summary)
    {
        if (context.OutputParameters.Contains("Success"))
            context.OutputParameters["Success"] = success;
        if (context.OutputParameters.Contains("ResultJson"))
            context.OutputParameters["ResultJson"] = resultJson ?? "";
        if (context.OutputParameters.Contains("Summary"))
            context.OutputParameters["Summary"] = summary;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
