using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using D365TestCenter.Core.Config;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;

namespace D365TestCenter.Core;

/// <summary>
/// Zentraler Orchestrator für Test-Läufe. Kapselt die drei Phasen:
///
///   1. Load:    TestCases aus Dataverse (jbe_testcase-Records) laden und filtern.
///   2. Run:     TestRunner.RunAll() auf der Core-Engine ausführen.
///   3. Persist: jbe_testrun-Record aktualisieren und jbe_testrunresult + jbe_teststep
///               Records schreiben.
///
/// Wird von der CLI (D365TestCenter.Cli) und der Custom API (RunIntegrationTestsApi)
/// identisch genutzt. Garantiert konsistentes Verhalten zwischen Browser-Aufruf und
/// Headless-Aufruf (Single-Engine-Architektur, siehe ADR-0003).
///
/// Das CRUD-Trigger-Plugin (RunTestsOnStatusChange) nutzt den Orchestrator NICHT,
/// weil es eine Batch-Cascade-Architektur hat (BatchSize=12, Self-Trigger). Das ist
/// eine Optimierung für das 2-Minuten-Sandbox-Timeout und läuft auf demselben
/// TestRunner-Core.
/// </summary>
public sealed class TestCenterOrchestrator
{
    private readonly IOrganizationService _service;
    private readonly ITestCenterConfig _config;
    private readonly Action<string>? _log;

    // Felder des jbe_testrun-Records
    private const string FldStatus = "jbe_teststatus";
    private const string FldSummary = "jbe_testsummary";
    private const string FldFilter = "jbe_testcasefilter";
    private const string FldKeepRecords = "jbe_keeprecords";
    private const string FldStartedOn = "jbe_startedon";
    private const string FldCompletedOn = "jbe_completedon";
    private const string FldTotal = "jbe_total";
    private const string FldPassed = "jbe_passed";
    private const string FldFailed = "jbe_failed";

    // Felder des jbe_testcase-Records
    private const string FldCaseTestId = "jbe_testid";
    private const string FldCaseTitle = "jbe_title";
    private const string FldCaseDefinition = "jbe_definitionjson";
    private const string FldCaseEnabled = "jbe_enabled";
    private const string FldCaseTags = "jbe_tags";
    private const string FldCaseCategory = "jbe_category";

    // Felder des jbe_testrun-Records
    private const string FldFullLog = "jbe_fulllog";

    // Felder des jbe_testrunresult-Records
    private const string FldResultTestId = "jbe_testid";
    private const string FldResultOutcome = "jbe_outcome";
    private const string FldResultDuration = "jbe_durationms";
    private const string FldResultError = "jbe_errormessage";
    private const string FldResultAssertions = "jbe_assertionresults";
    private const string FldResultTestRun = "jbe_testrunid";
    private const string FldResultTrackedRecords = "jbe_trackedrecords";

    // Felder des jbe_teststep-Records (ADR-0004: kein Phase-Feld mehr,
    // der Action-Typ steht im String-Feld jbe_action).
    private const string FldStepNumber = "jbe_stepnumber";
    private const string FldStepAction = "jbe_action";
    private const string FldStepDuration = "jbe_durationms";
    private const string FldStepError = "jbe_errormessage";
    private const string FldStepAssertionField = "jbe_assertionfield";
    private const string FldStepExpected = "jbe_expectedvalue";
    private const string FldStepActual = "jbe_actualvalue";
    private const string FldStepStatus = "jbe_stepstatus";
    private const string FldStepRunResult = "jbe_testrunresultid";
    private const string FldStepAlias = "jbe_alias";
    private const string FldStepEntity = "jbe_entity";
    private const string FldStepRecordId = "jbe_recordid";
    private const string FldStepInputData = "jbe_inputdata";
    private const string FldStepOutputData = "jbe_outputdata";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.None,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    /// <summary>
    /// KRITISCH: MetadataPropertyHandling.Ignore verhindert, dass Newtonsoft.Json
    /// das $type-Feld als TypeNameHandling-Metadata interpretiert und aus dem
    /// JObject entfernt. Ohne diesen Fix verlieren ExecuteRequest-Parameter
    /// (z.B. Merge mit $type=EntityReference) ihre Typ-Information.
    /// Siehe Changelog 2026-04-14_pluginpackage-migration.md.
    /// </summary>
    private static readonly JsonSerializerSettings JsonReadSettings = new()
    {
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore
    };

    /// <summary>
    /// Erstellt einen neuen Orchestrator.
    /// </summary>
    /// <param name="service">Dataverse-Service (IOrganizationService)</param>
    /// <param name="config">Config mit Entity-Namen und Status-Codes</param>
    /// <param name="log">Optionaler Logger (z.B. Console.WriteLine)</param>
    public TestCenterOrchestrator(
        IOrganizationService service,
        ITestCenterConfig config,
        Action<string>? log = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log;
    }

    /// <summary>
    /// Führt einen kompletten Testlauf aus: Legt einen TestRun-Record an,
    /// lädt Tests, führt sie aus, schreibt Ergebnisse.
    ///
    /// Wird von der CLI genutzt (neuer TestRun pro Aufruf).
    /// </summary>
    /// <param name="filter">Testfall-Filter (z.B. "*", "MGR*", "tag:merge", "category:Bridge")</param>
    /// <param name="keepRecords">Testdaten nach Lauf behalten?</param>
    /// <returns>TestRunResult mit allen Details</returns>
    public TestRunResult RunNewTestRun(string filter, bool keepRecords)
    {
        Log("===================================================");
        Log($"  TestCenter Run");
        Log($"  Filter: {filter}");
        Log($"  KeepRecords: {keepRecords}");
        Log("===================================================");

        // 1. Load: TestCases aus Dataverse laden und filtern
        var cases = LoadTestCases(filter);
        Log($"  {cases.Count} Testfälle geladen (Filter: {filter})");

        // 2. TestRun-Record anlegen
        var testRunId = _service.Create(new Entity(_config.TestRunEntity)
        {
            [FldStatus] = new OptionSetValue(_config.StatusRunning),
            [FldFilter] = filter,
            [FldKeepRecords] = keepRecords,
            [FldStartedOn] = DateTime.UtcNow,
            [FldTotal] = cases.Count,
            [FldPassed] = 0,
            [FldFailed] = 0,
            [FldSummary] = "Testausführung gestartet..."
        });
        Log($"  TestRun erstellt: {testRunId}");

        // 3. Run + Persist
        return ExecuteAndPersist(testRunId, cases, keepRecords);
    }

    /// <summary>
    /// Führt Tests für einen existierenden TestRun-Record aus (Browser-Flow).
    /// Der Browser legt zuerst einen TestRun-Record an (Status "Geplant"), dann
    /// ruft er die Custom API auf, die diese Methode aufruft.
    /// </summary>
    /// <param name="testRunId">ID des jbe_testrun-Records</param>
    /// <returns>TestRunResult mit allen Details</returns>
    public TestRunResult RunExistingTestRun(Guid testRunId)
    {
        if (testRunId == Guid.Empty)
            throw new ArgumentException("testRunId darf nicht leer sein", nameof(testRunId));

        // TestRun-Record lesen (Filter + KeepRecords)
        var testRun = _service.Retrieve(
            _config.TestRunEntity, testRunId,
            new ColumnSet(FldFilter, FldKeepRecords));

        var filter = testRun.GetAttributeValue<string>(FldFilter) ?? "*";
        var keepRecords = testRun.GetAttributeValue<bool>(FldKeepRecords);

        Log("===================================================");
        Log($"  TestCenter Run (existierender TestRun)");
        Log($"  TestRunId: {testRunId}");
        Log($"  Filter: {filter}");
        Log($"  KeepRecords: {keepRecords}");
        Log("===================================================");

        // Status auf "Wird ausgeführt" setzen
        _service.Update(new Entity(_config.TestRunEntity, testRunId)
        {
            [FldStatus] = new OptionSetValue(_config.StatusRunning),
            [FldStartedOn] = DateTime.UtcNow,
            [FldSummary] = "Testausführung gestartet..."
        });

        var cases = LoadTestCases(filter);
        Log($"  {cases.Count} Testfälle geladen (Filter: {filter})");

        // Total-Count aktualisieren
        _service.Update(new Entity(_config.TestRunEntity, testRunId)
        {
            [FldTotal] = cases.Count
        });

        return ExecuteAndPersist(testRunId, cases, keepRecords);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Kern: TestRunner aufrufen, Ergebnisse schreiben
    // ════════════════════════════════════════════════════════════════════

    private TestRunResult ExecuteAndPersist(
        Guid testRunId,
        List<TestCase> cases,
        bool keepRecords)
    {
        // 3a. Leere Tests: sofort abschliessen
        if (cases.Count == 0)
        {
            _service.Update(new Entity(_config.TestRunEntity, testRunId)
            {
                [FldStatus] = new OptionSetValue(_config.StatusCompleted),
                [FldCompletedOn] = DateTime.UtcNow,
                [FldSummary] = "Keine Testfälle gefunden."
            });
            return new TestRunResult
            {
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                TotalCount = 0
            };
        }

        // 3b. TestRunner ausführen mit Live-Progress-Updates.
        // Fataler Fehler (RV-01): Wenn die Engine selbst eine Exception wirft,
        // MUSS der TestRun auf StatusFailed gesetzt werden, sonst bleibt der
        // Run für die Browser-Live-View unbegrenzt auf "Wird ausgeführt" hängen.
        try
        {
            return ExecuteAndPersistInternal(testRunId, cases, keepRecords);
        }
        catch (Exception ex)
        {
            Log("");
            Log($"  FATALER FEHLER: {ex.Message}");
            try
            {
                _service.Update(new Entity(_config.TestRunEntity, testRunId)
                {
                    [FldStatus] = new OptionSetValue(_config.StatusFailed),
                    [FldCompletedOn] = DateTime.UtcNow,
                    [FldSummary] = Truncate($"Fataler Fehler: {ex.Message}", 4000)
                });
            }
            catch { /* Status-Update selbst darf nicht blocken */ }
            throw;
        }
    }

    private TestRunResult ExecuteAndPersistInternal(Guid testRunId, List<TestCase> cases, bool keepRecords)
    {
        var runner = new TestRunner(_service) { KeepRecords = keepRecords };
        var progressCount = 0;

        runner.OnTestCompleted += (index, total, tcResult) =>
        {
            progressCount++;

            // Log zum Konsolen-Output
            var icon = tcResult.Outcome switch
            {
                TestOutcome.Passed => "[OK]",
                TestOutcome.Failed => "[FAIL]",
                TestOutcome.Error => "[ERR]",
                TestOutcome.Skipped => "[SKIP]",
                _ => "[?]"
            };
            Log($"  [{index}/{total}] {icon} {tcResult.TestId}: {tcResult.Title} ({tcResult.DurationMs}ms)");

            // Fortschritts-Update auf jbe_testrun (non-critical)
            try
            {
                UpdateTestRunProgress(testRunId, index, total, tcResult);
            }
            catch
            {
                // Progress-Update ist nicht kritisch
            }

            // Result-Record sofort schreiben (damit Browser-Live-View es sehen kann)
            try
            {
                WriteSingleResultRecord(testRunId, tcResult);
            }
            catch (Exception ex)
            {
                Log($"      Result-Write fehlgeschlagen: {ex.Message}");
            }
        };

        var result = runner.RunAll(cases);

        // 3c. Final-Update: Status + Summary + Counts + FullLog (B4)
        var summary = BuildSummary(result);
        _service.Update(new Entity(_config.TestRunEntity, testRunId)
        {
            [FldStatus] = new OptionSetValue(_config.StatusCompleted),
            [FldSummary] = Truncate(summary, 4000),
            [FldCompletedOn] = DateTime.UtcNow,
            [FldTotal] = result.TotalCount,
            [FldPassed] = result.PassedCount,
            [FldFailed] = result.FailedCount + result.ErrorCount,
            // B4-Fix: jbe_fulllog wurde bisher nie geschrieben, obwohl die
            // Engine das gesamte Log inkl. Plugin-Trace-Logs (A7) sammelt.
            // Memo-Feld erlaubt sehr grosse Strings; wir kappen bei 100k Zeichen
            // damit das Update nicht blockiert.
            [FldFullLog] = Truncate(result.FullLog ?? "", 100000)
        });

        Log("");
        Log($"  ============================================================");
        Log($"  ERGEBNIS: {result.PassedCount} PASSED | {result.FailedCount} FAILED | {result.ErrorCount} ERROR | {result.TotalCount} TOTAL");
        Log($"  ============================================================");

        return result;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Load: TestCases aus Dataverse
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Lädt aktivierte TestCases aus der jbe_testcase-Entity und wendet den
    /// Filter an. Unterstützte Filter-Formate (identisch zum Custom-API-Filter):
    ///
    ///   "*" oder leer     -> alle aktivierten Tests
    ///   "TC01"            -> exakt diese ID
    ///   "TC*"             -> Wildcard-Prefix (MGR*, STD*)
    ///   "TC01,TC02,TC03"  -> Liste
    ///   "tag:merge"       -> alle mit Tag
    ///   "category:Bridge" -> alle in Kategorie
    /// </summary>
    public List<TestCase> LoadTestCases(string filter)
    {
        var q = new QueryExpression(_config.TestCaseEntity)
        {
            ColumnSet = new ColumnSet(
                FldCaseTestId, FldCaseTitle, FldCaseDefinition,
                FldCaseEnabled, FldCaseTags, FldCaseCategory),
            Criteria = {
                Conditions = {
                    new ConditionExpression(FldCaseEnabled, ConditionOperator.Equal, true)
                }
            },
            Orders = { new OrderExpression(FldCaseTestId, OrderType.Ascending) },
            TopCount = 2000
        };

        var entities = _service.RetrieveMultiple(q).Entities.ToList();

        // JSON-Definitionen parsen in TestCase-Objekte
        var testCases = new List<TestCase>();
        foreach (var e in entities)
        {
            var defJson = e.GetAttributeValue<string>(FldCaseDefinition);
            if (string.IsNullOrWhiteSpace(defJson)) continue;

            try
            {
                TestCase? tc;
                var trimmed = defJson.TrimStart();
                if (trimmed.StartsWith("{"))
                {
                    // Mit MetadataPropertyHandling.Ignore (siehe JsonReadSettings)
                    // bleibt das $type-Feld in JObjects erhalten für ExecuteRequest.
                    tc = JsonConvert.DeserializeObject<TestCase>(defJson, JsonReadSettings);
                }
                else
                {
                    // Fallback: kein valides JSON-Objekt
                    Log($"      JSON fehlerhaft für {e.GetAttributeValue<string>(FldCaseTestId)}");
                    continue;
                }

                if (tc == null) continue;

                // Metadaten aus Record übernehmen falls im JSON fehlend.
                // Robust gegen Typ-Varianten (jbe_category kann String ODER OptionSet sein).
                if (string.IsNullOrWhiteSpace(tc.Id))
                    tc.Id = SafeGetString(e, FldCaseTestId) ?? "?";
                if (string.IsNullOrWhiteSpace(tc.Title))
                    tc.Title = SafeGetString(e, FldCaseTitle) ?? "";
                if (tc.Tags.Count == 0)
                {
                    var tags = SafeGetString(e, FldCaseTags);
                    if (!string.IsNullOrWhiteSpace(tags))
                        tc.Tags = tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                }
                if (string.IsNullOrWhiteSpace(tc.Category))
                    tc.Category = SafeGetString(e, FldCaseCategory);

                testCases.Add(tc);
            }
            catch (Exception ex)
            {
                Log($"      JSON-Parse-Fehler für {e.GetAttributeValue<string>(FldCaseTestId)}: {ex.Message}");
            }
        }

        return ApplyFilter(testCases, filter);
    }

    /// <summary>
    /// Filtert TestCases (identisch zum Custom-API-Filter).
    /// </summary>
    public static List<TestCase> ApplyFilter(List<TestCase> testCases, string? filter)
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

        // Komma-getrennte IDs mit Wildcard-Support: "MGR*,STD01,TC03"
        var patterns = trimmed
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        return enabled.Where(tc => MatchesAny(tc.Id, patterns)).ToList();
    }

    private static bool MatchesAny(string testId, string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Contains("*"))
            {
                // Wildcard-Match: Prefix/Suffix
                if (pattern.StartsWith("*") && pattern.EndsWith("*"))
                {
                    var mid = pattern.Trim('*');
                    if (testId.IndexOf(mid, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                else if (pattern.StartsWith("*"))
                {
                    var suffix = pattern.TrimStart('*');
                    if (testId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.EndsWith("*"))
                {
                    var prefix = pattern.TrimEnd('*');
                    if (testId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    // *mitte*
                    var parts = pattern.Split('*');
                    if (parts.Length == 2
                        && testId.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                        && testId.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            else
            {
                if (testId.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Persist: Result-Records schreiben
    // ════════════════════════════════════════════════════════════════════

    private void UpdateTestRunProgress(Guid testRunId, int index, int total, TestCaseResult tcResult)
    {
        var text = $"Läuft... [{index}/{total}] {tcResult.TestId}: {tcResult.Outcome}";

        // Counter incremental aktualisieren (leichtgewichtig)
        var progressUpdate = new Entity(_config.TestRunEntity, testRunId)
        {
            [FldSummary] = Truncate(text, 4000)
        };
        _service.Update(progressUpdate);
    }

    private void WriteSingleResultRecord(Guid testRunId, TestCaseResult tcResult)
    {
        var testRunRef = new EntityReference(_config.TestRunEntity, testRunId);

        var resultRecord = new Entity(_config.TestRunResultEntity)
        {
            [FldResultTestId] = tcResult.TestId,
            [FldResultOutcome] = new OptionSetValue(MapOutcome(tcResult.Outcome)),
            [FldResultDuration] = (int)tcResult.DurationMs,
            [FldResultError] = Truncate(tcResult.ErrorMessage ?? "", 4000),
            [FldResultTestRun] = testRunRef
        };

        // AssertionResults als JSON (aus den Assert-StepResults gefiltert,
        // Kompat fuer UI-Code der jbe_assertionresults parst).
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
            resultRecord[FldResultAssertions] = Truncate(
                JsonConvert.SerializeObject(assertSteps, JsonSettings), 100000);
        }
        catch { /* Feld existiert vielleicht nicht auf allen Umgebungen */ }

        // B5-Fix: TrackedRecords als JSON in jbe_trackedrecords. Wurde bisher
        // nie geschrieben — Test-Autoren konnten nicht sehen welche Records
        // ein Test angelegt hatte, insbesondere bei keepRecords=true.
        try
        {
            if (tcResult.TrackedRecords.Count > 0)
            {
                resultRecord[FldResultTrackedRecords] = Truncate(
                    JsonConvert.SerializeObject(tcResult.TrackedRecords, JsonSettings), 100000);
            }
        }
        catch { /* Feld existiert vielleicht nicht auf allen Umgebungen */ }

        var resultId = _service.Create(resultRecord);
        var resultRef = new EntityReference(_config.TestRunResultEntity, resultId);

        // ADR-0004: eine einheitliche Persistenz-Schleife. Jeder StepResult
        // wird zu genau einem jbe_teststep-Record. Action-Typ im String-Feld
        // jbe_action, keine Phase-OptionSet mehr.
        foreach (var stepResult in tcResult.StepResults)
        {
            try
            {
                var step = new Entity(_config.TestStepEntity)
                {
                    [FldStepNumber] = stepResult.StepNumber,
                    [FldStepAction] = Truncate(stepResult.Action ?? "", 100),
                    [FldStepDuration] = (int)stepResult.DurationMs,
                    [FldStepError] = Truncate(stepResult.Message ?? "", 4000),
                    [FldStepStatus] = new OptionSetValue(stepResult.Success ? _config.OutcomePassed : _config.OutcomeFailed),
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
                _service.Create(step);
            }
            catch { /* non-critical */ }
        }
    }

    private int MapOutcome(TestOutcome outcome) => outcome switch
    {
        TestOutcome.Passed => _config.OutcomePassed,
        TestOutcome.Failed => _config.OutcomeFailed,
        TestOutcome.Error => _config.OutcomeError,
        TestOutcome.Skipped => _config.OutcomeSkipped,
        _ => _config.OutcomeError
    };

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    private static string BuildSummary(TestRunResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalSeconds;
        var sb = new StringBuilder();

        sb.AppendLine(
            $"{result.PassedCount}/{result.TotalCount} bestanden, " +
            $"{result.FailedCount} fehlgeschlagen, " +
            $"{result.ErrorCount} Fehler ({duration:F1}s)");
        sb.AppendLine();

        foreach (var tc in result.Results.Take(50))  // max 50 im Summary (4000 chars Limit)
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
            if (tc.Outcome != TestOutcome.Passed && !string.IsNullOrEmpty(tc.ErrorMessage))
                sb.AppendLine($"  -> {Truncate(tc.ErrorMessage ?? "", 200)}");
        }
        if (result.Results.Count > 50)
            sb.AppendLine($"... und {result.Results.Count - 50} weitere (siehe jbe_testrunresults)");

        return sb.ToString();
    }

    /// <summary>
    /// Liest ein Attribute als String unabhängig vom tatsaechlichen Typ
    /// (String, OptionSetValue, Number, ...). Verhindert InvalidCastExceptions
    /// bei heterogenen Feld-Typen zwischen Umgebungen.
    /// </summary>
    private static string? SafeGetString(Entity e, string attr)
    {
        if (!e.Contains(attr)) return null;
        var val = e[attr];
        return val switch
        {
            null => null,
            string s => s,
            OptionSetValue osv => osv.Value.ToString(),
            EntityReference er => er.Id.ToString(),
            bool b => b.ToString(),
            int i => i.ToString(),
            _ => val.ToString()
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }

    private void Log(string msg) => _log?.Invoke(msg);
}
