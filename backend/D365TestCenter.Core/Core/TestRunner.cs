using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core;

/// <summary>
/// Orchestriert die generische Integrationstestausführung:
/// Setup (Preconditions) -> Steps -> Assertions -> Cleanup.
/// </summary>
public sealed class TestRunner
{
    private readonly IOrganizationService _service;
    private readonly EntityMetadataCache _entityMetadata;
    private readonly TestDataFactory _dataFactory;
    private readonly PlaceholderEngine _placeholderEngine;
    private readonly AssertionEngine _assertionEngine;
    private readonly StringBuilder _log;

    /// <summary>
    /// Wenn true, werden die in Steps angelegten Records nach dem Testlauf
    /// nicht gelöscht. Default false (Cleanup lief historisch immer).
    /// Wird vom Orchestrator aus jbe_testrun.jbe_keeprecords gesetzt.
    /// </summary>
    public bool KeepRecords { get; set; }

    /// <summary>
    /// Wird nach jedem Testfall aufgerufen (index, total, result).
    /// Ermöglicht Fortschritts-Updates im TestRun-Record.
    /// </summary>
    public event Action<int, int, TestCaseResult>? OnTestCompleted;

    public TestRunner(IOrganizationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _entityMetadata = new EntityMetadataCache(service, msg => Log($"      {msg}"));
        _dataFactory = new TestDataFactory();
        _placeholderEngine = new PlaceholderEngine();
        _assertionEngine = new AssertionEngine();
        _log = new StringBuilder();
    }

    // Geteilte Kontexte für dependsOn/sharedContext
    private readonly Dictionary<string, TestContext> _sharedContexts
        = new Dictionary<string, TestContext>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _passedTestIds
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Führt eine vollständige Testsequenz aus.</summary>
    public TestRunResult RunAll(List<TestCase> testCases)
    {
        // Datengetriebene Tests expandieren: jede dataRow wird ein eigener Testlauf
        var expandedTests = ExpandDataDrivenTests(testCases);

        var result = new TestRunResult
        {
            StartedAt = DateTime.UtcNow,
            TotalCount = expandedTests.Count
        };

        Log("=== INTEGRATIONSTEST ===");
        Log($"Testfälle: {expandedTests.Count} (davon {expandedTests.Count - testCases.Count} aus dataRows expandiert)");
        Log($"Start: {result.StartedAt:O}");

        int index = 0;
        foreach (var (tc, dataRow) in expandedTests)
        {
            index++;

            if (!tc.Enabled)
            {
                var skipped = new TestCaseResult
                {
                    TestId = tc.Id,
                    Title = tc.Title,
                    Outcome = TestOutcome.Skipped
                };
                result.Results.Add(skipped);
                Log($"-- [{index}/{expandedTests.Count}] [{tc.Id}] {tc.Title} -> ÜBERSPRUNGEN (deaktiviert) --");
                OnTestCompleted?.Invoke(index, expandedTests.Count, skipped);
                continue;
            }

            // dependsOn: überspringen wenn eine Abhängigkeit nicht bestanden hat
            if (tc.DependsOn != null && tc.DependsOn.Count > 0)
            {
                var missingDeps = tc.DependsOn.Where(dep => !_passedTestIds.Contains(dep)).ToList();
                if (missingDeps.Count > 0)
                {
                    var depSkipped = new TestCaseResult
                    {
                        TestId = tc.Id,
                        Title = tc.Title,
                        Outcome = TestOutcome.Skipped,
                        ErrorMessage = $"Abhängigkeit nicht erfüllt: {string.Join(", ", missingDeps)}"
                    };
                    result.Results.Add(depSkipped);
                    Log($"-- [{index}/{expandedTests.Count}] [{tc.Id}] ÜBERSPRUNGEN (dependsOn: {string.Join(", ", missingDeps)}) --");
                    OnTestCompleted?.Invoke(index, expandedTests.Count, depSkipped);
                    continue;
                }
            }

            var tcResult = ExecuteSingleTest(tc, index, expandedTests.Count, dataRow);
            result.Results.Add(tcResult);

            if (tcResult.Outcome == TestOutcome.Passed)
                _passedTestIds.Add(tc.Id);

            switch (tcResult.Outcome)
            {
                case TestOutcome.Passed: result.PassedCount++; break;
                case TestOutcome.Failed: result.FailedCount++; break;
                case TestOutcome.Error: result.ErrorCount++; break;
            }

            OnTestCompleted?.Invoke(index, expandedTests.Count, tcResult);
        }

        result.CompletedAt = DateTime.UtcNow;
        Log($"=== ERGEBNIS: {result.PassedCount}/{result.TotalCount} bestanden, " +
            $"{result.FailedCount} fehlgeschlagen, {result.ErrorCount} Fehler ===");

        result.FullLog = _log.ToString();
        return result;
    }

    /// <summary>
    /// Expandiert datengetriebene Testfälle: Ein Testfall mit N dataRows wird zu N Testfällen.
    /// Testfälle ohne dataRows bleiben unverändert.
    /// </summary>
    private static List<(TestCase tc, Dictionary<string, object?>? dataRow)> ExpandDataDrivenTests(
        List<TestCase> testCases)
    {
        var expanded = new List<(TestCase, Dictionary<string, object?>?)>();

        foreach (var tc in testCases)
        {
            if (tc.DataRows != null && tc.DataRows.Count > 0)
            {
                int rowIdx = 0;
                foreach (var row in tc.DataRows)
                {
                    rowIdx++;
                    // Erstelle eine Kopie des Testfalls mit angepasster ID/Titel
                    var rowTc = new TestCase
                    {
                        Id = $"{tc.Id}[{rowIdx}]",
                        Title = $"{tc.Title} [Zeile {rowIdx}]",
                        Description = tc.Description,
                        Category = tc.Category,
                        Tags = tc.Tags,
                        Enabled = tc.Enabled,
                        Steps = tc.Steps,
                        DependsOn = tc.DependsOn,
                        SharedContext = tc.SharedContext
                    };
                    expanded.Add((rowTc, row));
                }
            }
            else
            {
                expanded.Add((tc, null));
            }
        }

        return expanded;
    }

    private TestCaseResult ExecuteSingleTest(
        TestCase tc, int index, int total,
        Dictionary<string, object?>? dataRow = null)
    {
        var sw = Stopwatch.StartNew();
        var tcResult = new TestCaseResult { TestId = tc.Id, Title = tc.Title };
        TestContext? ctx = null;

        Log($"-- [{index}/{total}] [{tc.Id}] {tc.Title} --");

        try
        {
            ctx = new TestContext { TestStartUtc = DateTime.UtcNow, TestId = tc.Id };
            ctx.CurrentDataRow = dataRow;

            Log("  Teststeps ausführen...");
            ExecuteSteps(tc, ctx, tcResult);

            // Outcome-Bestimmung (ADR-0004):
            //  - Exception in einem Step mit OnError="stop" (Default fuer Non-Assert)
            //    fuehrt zu Outcome=Error (catch-Zweig unten).
            //  - Kein Step hat Success=false → Passed.
            //  - Mindestens ein Step hat Success=false → Failed. Das umfasst:
            //    Assert-Failure (OnError=continue per Default) und Non-Assert-
            //    Step-Failure mit explizitem OnError=continue.
            var anyFailed = tcResult.StepResults.Any(s => !s.Success);
            tcResult.Outcome = anyFailed ? TestOutcome.Failed : TestOutcome.Passed;
            Log($"  -> {(anyFailed ? "FEHLGESCHLAGEN" : "BESTANDEN")}");
        }
        catch (Exception ex)
        {
            tcResult.Outcome = TestOutcome.Error;
            tcResult.ErrorMessage = ex.Message;
            Log($"  -> FEHLER: {ex.Message}");
        }
        finally
        {
            if (ctx != null)
            {
                try
                {
                    Log("  Cleanup...");
                    Cleanup(ctx, tcResult);
                }
                catch (Exception ex)
                {
                    Log($"  Cleanup-Fehler (nicht kritisch): {ex.Message}");
                }
            }

            sw.Stop();
            tcResult.DurationMs = sw.ElapsedMilliseconds;
            Log($"  Dauer: {tcResult.DurationMs}ms");
        }

        return tcResult;
    }

    // ================================================================
    //  Phase 1: Setup (Preconditions)
    // ================================================================
    //  Teststeps ausführen (einheitliche Liste ab ADR-0004)
    // ================================================================

    /// <summary>
    /// Ausfuehrung aller Steps in JSON-Reihenfolge. Pro Step wird ein
    /// StepResult angehaengt (Erfolg oder Fehler). Fehlerverhalten:
    /// onError="stop" (Default fuer Non-Assert) wirft weiter und beendet
    /// den Test mit Outcome=Error. onError="continue" (Default fuer Assert,
    /// override sonst) schluckt die Exception — Test laeuft weiter und ist
    /// am Ende Failed, wenn mindestens ein Step nicht Success=true war.
    /// </summary>
    private void ExecuteSteps(TestCase tc, TestContext ctx, TestCaseResult tcResult)
    {
        foreach (var step in tc.Steps)
        {
            Log($"    Step {step.StepNumber}: {step.Description} [{step.Action}]");

            // StepResult erzeugen bevor die Action läuft, damit auch bei Fehler
            // ein Eintrag in tcResult.StepResults landet. Der Orchestrator
            // persistiert diese Liste pro Step als jbe_teststep-Record.
            var stepResult = new StepResult
            {
                StepNumber = step.StepNumber,
                Action = step.Action,
                Alias = step.Alias,
                Entity = step.Entity,
                Description = string.IsNullOrEmpty(step.Description)
                    ? step.Action
                    : step.Description
            };
            var stepSw = Stopwatch.StartNew();

            try
            {
                var resolvedFields = ResolveFieldValues(
                    _dataFactory.ResolveTemplateData(step.Fields, ctx), ctx);

                switch (step.Action.ToUpperInvariant())
                {
                    case "CREATERECORD":
                        StepCreateGenericRecord(step, ctx, resolvedFields);
                        break;

                    case "UPDATERECORD":
                        StepUpdateGenericRecord(step, ctx, resolvedFields);
                        break;

                    case "DELETERECORD":
                        StepDeleteGenericRecord(step, ctx);
                        break;

                    case "FINDRECORD":
                    case "WAITFORRECORD":
                        StepWaitForRecord(step, ctx);
                        break;

                    case "WAITFORFIELDVALUE":
                        StepWaitForFieldValue(step, ctx);
                        break;

                    case "CALLCUSTOMAPI":
                        StepCallCustomApi(step, ctx, resolvedFields);
                        break;

                    case "ASSERTENVIRONMENT":
                        StepAssertEnvironment(step, ctx);
                        break;

                    case "EXECUTEREQUEST":
                        StepExecuteRequest(step, ctx, resolvedFields);
                        break;

                    case "RETRIEVERECORD":
                        StepRetrieveRecord(step, ctx);
                        break;

                    case "ASSERT":
                        StepAssert(step, ctx, stepResult);
                        break;

                    case "WAIT":
                        var waitSecs = step.WaitSeconds ?? step.TimeoutSeconds;
                        Log($"      Warte {waitSecs}s...");
                        Thread.Sleep(waitSecs * 1000);
                        break;

                    case "DELAY":
                        var delayMs = step.DelayMs ?? 500;
                        Log($"      Delay {delayMs}ms...");
                        Thread.Sleep(delayMs);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Unbekannte Step-Action: {step.Action}");
                }

                // Wenn der Assert-Handler stepResult.Success bereits gesetzt hat
                // (Fail-Case), nicht ueberschreiben.
                if (!step.Action.Equals("Assert", StringComparison.OrdinalIgnoreCase))
                {
                    stepResult.Success = true;
                }

                // Enrich mit RecordId aus ctx (falls Alias bekannt wurde)
                if (!string.IsNullOrEmpty(stepResult.Alias) && ctx.Records.TryGetValue(stepResult.Alias, out var rec))
                {
                    stepResult.RecordId = rec.Id;
                    if (string.IsNullOrEmpty(stepResult.Entity))
                        stepResult.Entity = rec.EntityName;
                }
            }
            catch (Exception ex)
            {
                stepResult.Success = false;
                stepResult.Message = ex.Message;

                // OnError: Default per Action-Typ
                //  - Assert: "continue" (Failure ist normales Ergebnis)
                //  - Alle anderen: "stop" (Exception beendet den Test als Error)
                var onError = step.OnError?.ToLowerInvariant();
                var defaultStop = !step.Action.Equals("Assert", StringComparison.OrdinalIgnoreCase);
                var shouldStop = onError switch
                {
                    "continue" => false,
                    "stop" => true,
                    _ => defaultStop
                };

                if (shouldStop) throw;
                Log($"      Fehler (onError=continue): {ex.Message}");
            }
            finally
            {
                stepSw.Stop();
                stepResult.DurationMs = stepSw.ElapsedMilliseconds;
                tcResult.StepResults.Add(stepResult);
            }
        }
    }

    // ================================================================
    //  Assert als Action (ADR-0004)
    // ================================================================

    private void StepAssert(TestStep step, TestContext ctx, StepResult stepResult)
    {
        // TestStep → internes TestAssertion-Objekt fuer die AssertionEngine.
        // Entity ist im JSON typischerweise als EntitySetName (Plural, Web-API-Form)
        // angegeben, z.B. "markant_fg_contactsources". Die AssertionEngine arbeitet
        // mit QueryExpression/IOrganizationService und braucht den LogicalName
        // (Singular). Deshalb hier auf LogicalName ummapppen.
        var resolvedEntity = string.IsNullOrEmpty(step.Entity)
            ? step.Entity
            : ResolveEntity(step.Entity);

        var assertion = new TestAssertion
        {
            Target = step.Target ?? "Query",
            Field = step.Field ?? "",
            Entity = resolvedEntity,
            RecordRef = step.RecordRef,
            Filter = step.Filter,
            Operator = step.Operator ?? "Equals",
            Value = step.Value,
            Description = step.Description
        };

        var assertResult = _assertionEngine.Evaluate(assertion, ctx, _service);

        // Ergebnis in StepResult uebertragen
        stepResult.Success = assertResult.Passed;
        stepResult.Message = assertResult.Message;
        stepResult.AssertField = assertion.Field;
        stepResult.ExpectedDisplay = assertResult.ExpectedDisplay;
        stepResult.ActualDisplay = assertResult.ActualDisplay;
        if (!string.IsNullOrEmpty(assertion.Description))
            stepResult.Description = assertion.Description;

        var icon = assertResult.Passed ? "(OK)" : "(FAIL)";
        Log($"      Assert {icon} {assertion.Field} {assertion.Operator} {assertion.Value}");
    }

    // ================================================================
    //  Generische Actions
    // ================================================================

    private static readonly GenericRecordWaiter _recordWaiter = new GenericRecordWaiter();

    /// <summary>Löst EntitySetName (Plural, Web API) zu LogicalName (Singular, SDK) auf.</summary>
    private string ResolveEntity(string? entityNameFromJson)
    {
        if (string.IsNullOrWhiteSpace(entityNameFromJson))
            throw new InvalidOperationException("Entity-Name fehlt.");
        return _entityMetadata.ResolveLogicalName(entityNameFromJson);
    }

    private void StepCreateGenericRecord(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var entityName = ResolveEntity(step.Entity);
        var alias = step.Alias ?? $"record_{step.StepNumber}";

        var entity = new Entity(entityName);
        ApplyFields(entity, resolvedFields);

        var id = _service.Create(entity);
        ctx.RegisterRecord(alias, entityName, id);
        Log($"      CreateRecord [{alias}] in '{entityName}': {id}");

        // Auto-Retrieve: wenn columns definiert, Server-generierte Felder laden
        if (step.Columns != null && step.Columns.Count > 0)
        {
            var retrieved = _service.Retrieve(entityName, id, new ColumnSet(step.Columns.ToArray()));
            ctx.FoundRecords[alias] = retrieved;
            Log($"      Auto-Retrieve: {step.Columns.Count} Spalten für [{alias}] geladen");
        }
    }

    private void StepUpdateGenericRecord(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException("UpdateRecord benötigt 'recordRef' oder 'alias'.");

        // {RECORD:alias}-Platzhalter in recordRef auflösen
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);

        var entity = new Entity(entityName, recordId);
        ApplyFields(entity, resolvedFields, allowNull: true);
        _service.Update(entity);
        Log($"      UpdateRecord [{alias}] in '{entityName}': {recordId}");
    }

    private void StepDeleteGenericRecord(TestStep step, TestContext ctx)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException("DeleteRecord benötigt 'recordRef' oder 'alias'.");

        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);

        _service.Delete(entityName, recordId);
        Log($"      DeleteRecord [{alias}] in '{entityName}': {recordId}");
    }

    private void StepWaitForRecord(TestStep step, TestContext ctx)
    {
        var entityName = ResolveEntity(step.Entity);
        var filters = step.Filter
            ?? throw new InvalidOperationException("WaitForRecord benötigt 'filter'.");
        var alias = step.Alias ?? $"found_{step.StepNumber}";

        // Platzhalter in Filter-Werten auflösen
        var resolvedFilters = new List<FilterCondition>();
        foreach (var f in filters)
        {
            var resolvedValue = f.Value;
            if (resolvedValue is string s)
                resolvedValue = _placeholderEngine.Resolve(s, ctx);
            resolvedFilters.Add(new FilterCondition { Field = f.Field, Operator = f.Operator, Value = resolvedValue });
        }

        var columns = step.Columns?.ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var found = _recordWaiter.WaitForRecord(
            _service, entityName, resolvedFilters, columns,
            step.TimeoutSeconds, step.PollingIntervalMs,
            msg => Log($"      {msg}"));

        sw.Stop();

        if (found == null)
            throw new InvalidOperationException(
                $"WaitForRecord: Kein Record in '{entityName}' gefunden (Timeout: {step.TimeoutSeconds}s).");

        ctx.RegisterRecord(alias, entityName, found.Id);
        ctx.FoundRecords[alias] = found;
        Log($"      WaitForRecord [{alias}] gefunden: {found.Id} ({sw.ElapsedMilliseconds}ms)");

        // Performance-Assertion
        if (step.MaxDurationMs.HasValue && sw.ElapsedMilliseconds > step.MaxDurationMs.Value)
            throw new InvalidOperationException(
                $"WaitForRecord [{alias}]: Dauer {sw.ElapsedMilliseconds}ms überschreitet maxDurationMs={step.MaxDurationMs.Value}ms");
    }

    private void StepWaitForFieldValue(TestStep step, TestContext ctx)
    {
        var alias = step.RecordRef ?? step.Alias
            ?? throw new InvalidOperationException("WaitForFieldValue benötigt 'recordRef' oder 'alias'.");

        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity != null ? ResolveEntity(step.Entity) : ctx.ResolveRecordEntityName(alias);
        var fieldName = step.Fields.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("WaitForFieldValue: 'field' in fields fehlt.");
        var expectedValue = step.ExpectedValue
            ?? throw new InvalidOperationException("WaitForFieldValue benötigt 'expectedValue'.");

        // Platzhalter im erwarteten Wert auflösen
        if (expectedValue is string s)
            expectedValue = _placeholderEngine.Resolve(s, ctx);

        var success = _recordWaiter.WaitForFieldValue(
            _service, entityName, recordId, fieldName, expectedValue,
            step.TimeoutSeconds, step.PollingIntervalMs,
            msg => Log($"      {msg}"));

        if (!success)
            throw new InvalidOperationException(
                $"WaitForFieldValue: '{fieldName}' hat den erwarteten Wert nicht erreicht (Timeout: {step.TimeoutSeconds}s).");

        Log($"      WaitForFieldValue [{alias}].{fieldName} = {expectedValue} erreicht");
    }

    private void StepCallCustomApi(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var apiName = step.Entity
            ?? throw new InvalidOperationException("CallCustomApi benötigt 'entity' (API-Name).");

        var request = new OrganizationRequest(apiName);
        foreach (var kvp in resolvedFields)
        {
            if (kvp.Value == null) continue;
            request[kvp.Key] = ConvertValue(kvp.Value);
        }

        _service.Execute(request);
        Log($"      CallCustomApi '{apiName}' aufgerufen");
    }

    // ================================================================
    //  ExecuteRequest: Generische SDK-Message
    // ================================================================

    private void StepExecuteRequest(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var requestName = step.RequestName
            ?? throw new InvalidOperationException(
                "ExecuteRequest braucht 'requestName' (z.B. \"Merge\", \"SetState\", \"Assign\").");

        var request = new OrganizationRequest(requestName);

        // Felder als typisierte Parameter aufloesen
        foreach (var kvp in resolvedFields)
        {
            if (kvp.Value == null) continue;
            request[kvp.Key] = ResolveTypedValue(kvp.Value, ctx);
        }

        var response = _service.Execute(request);
        Log($"      ExecuteRequest '{requestName}' ausgeführt");
        HandleExecuteRequestResponse(step, ctx, response);
        WaitAfterExecuteRequest(step);
    }

    private void HandleExecuteRequestResponse(TestStep step, TestContext ctx, OrganizationResponse response)
    {
        // Response-Werte im Kontext speichern (wenn Alias gesetzt)
        if (!string.IsNullOrEmpty(step.Alias) && response.Results.Count > 0)
        {
            foreach (var kvp in response.Results)
            {
                var strVal = kvp.Value switch
                {
                    EntityReference er => er.Id.ToString(),
                    OptionSetValue osv => osv.Value.ToString(),
                    EntityCollection ec => ec.Entities.Count.ToString(),
                    Guid g => g.ToString(),
                    _ => kvp.Value?.ToString() ?? ""
                };
                ctx.GeneratedValues[$"{step.Alias}.response.{kvp.Key}"] = strVal;
            }
            Log($"      Response: {response.Results.Count} Werte unter [{step.Alias}] gespeichert");
        }
    }

    private void WaitAfterExecuteRequest(TestStep step)
    {
        if (step.WaitSeconds.HasValue && step.WaitSeconds.Value > 0)
        {
            Log($"      Warte {step.WaitSeconds.Value}s nach ExecuteRequest...");
            Thread.Sleep(step.WaitSeconds.Value * 1000);
        }
    }

    /// <summary>
    /// Löst einen Feldwert auf. Wenn der Wert ein JObject mit "$type" ist,
    /// wird der entsprechende SDK-Typ erzeugt (EntityReference, OptionSetValue, etc.).
    /// Primitive Werte werden per ConvertValue konvertiert.
    /// </summary>
    private object ResolveTypedValue(object value, TestContext ctx)
    {
        if (value is string s) return s;
        if (value is not JToken token) return ConvertValue(value);

        // Primitive JTokens
        switch (token.Type)
        {
            case JTokenType.String:
                return _placeholderEngine.Resolve(token.Value<string>()!, ctx);
            case JTokenType.Integer:
                return token.Value<int>();
            case JTokenType.Boolean:
                return token.Value<bool>();
            case JTokenType.Float:
                return token.Value<decimal>();
            case JTokenType.Null:
                return null!;
        }

        // Typisierte Objekte: JObject mit $type
        if (token is JObject obj && obj.ContainsKey("$type"))
        {
            var typeName = obj["$type"]!.Value<string>()!;
            return typeName.ToUpperInvariant() switch
            {
                "ENTITYREFERENCE" => ResolveEntityReferenceParam(obj, ctx),
                "GUID" => ResolveGuidParam(obj, ctx),
                "GUIDARRAY" => ResolveGuidArrayParam(obj, ctx),
                "OPTIONSETVALUE" => new OptionSetValue(obj["value"]!.Value<int>()),
                "MONEY" => new Money(obj["value"]!.Value<decimal>()),
                "ENTITY" => ResolveEntityParam(obj, ctx),
                "ENTITYCOLLECTION" => ResolveEntityCollectionParam(obj, ctx),
                _ => throw new InvalidOperationException(
                    $"Unbekannter $type: '{typeName}'. " +
                    $"Erlaubt: EntityReference, Guid, GuidArray, OptionSetValue, Money, Entity, EntityCollection")
            };
        }

        return ConvertValue(token);
    }

    private EntityReference ResolveEntityReferenceParam(JObject obj, TestContext ctx)
    {
        var entityName = obj["entity"]!.Value<string>()!;
        entityName = _entityMetadata.ResolveLogicalName(entityName);
        Guid id;
        if (obj.ContainsKey("ref"))
        {
            var refAlias = _placeholderEngine.Resolve(obj["ref"]!.Value<string>()!, ctx);
            id = ctx.ResolveRecordId(refAlias);
        }
        else if (obj.ContainsKey("id"))
        {
            var idStr = _placeholderEngine.Resolve(obj["id"]!.Value<string>()!, ctx);
            id = Guid.Parse(idStr);
        }
        else
            throw new InvalidOperationException(
                "EntityReference braucht 'ref' (Alias) oder 'id' (GUID).");
        return new EntityReference(entityName, id);
    }

    private Guid ResolveGuidParam(JObject obj, TestContext ctx)
    {
        if (obj.ContainsKey("ref"))
            return ctx.ResolveRecordId(obj["ref"]!.Value<string>()!);
        var valStr = _placeholderEngine.Resolve(obj["value"]!.Value<string>()!, ctx);
        return Guid.Parse(valStr);
    }

    private Guid[] ResolveGuidArrayParam(JObject obj, TestContext ctx)
    {
        var refs = obj["refs"] as JArray
            ?? throw new InvalidOperationException("GuidArray braucht 'refs' (Array von Alias-Namen).");
        return refs.Select(r => ctx.ResolveRecordId(r.Value<string>()!)).ToArray();
    }

    private Entity ResolveEntityParam(JObject obj, TestContext ctx)
    {
        var entityName = obj["entity"]!.Value<string>()!;
        entityName = _entityMetadata.ResolveLogicalName(entityName);
        var entity = new Entity(entityName);
        if (obj.ContainsKey("fields") && obj["fields"] is JObject fieldsObj)
        {
            foreach (var prop in fieldsObj.Properties())
            {
                var val = ResolveTypedValue(prop.Value, ctx);
                entity[prop.Name] = val;
            }
        }
        return entity;
    }

    private EntityCollection ResolveEntityCollectionParam(JObject obj, TestContext ctx)
    {
        var collection = new EntityCollection();
        if (obj.ContainsKey("entities") && obj["entities"] is JArray items)
        {
            foreach (var item in items)
                collection.Entities.Add((Entity)ResolveTypedValue(item, ctx));
        }
        return collection;
    }

    // ================================================================
    //  RetrieveRecord: Record neu laden (für {alias.fields.x} in Steps)
    // ================================================================

    private void StepRetrieveRecord(TestStep step, TestContext ctx)
    {
        var alias = step.Alias ?? step.RecordRef
            ?? throw new InvalidOperationException(
                "RetrieveRecord braucht 'alias' (eines bereits erstellten Records).");

        // {RECORD:alias}-Wrapper entfernen falls vorhanden
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        if (!ctx.Records.TryGetValue(alias, out var record))
            throw new InvalidOperationException(
                $"RetrieveRecord: Alias '{alias}' nicht im Kontext gefunden. " +
                $"Verfuegbar: [{string.Join(", ", ctx.Records.Keys)}]");

        var columns = step.Columns;
        var columnSet = (columns != null && columns.Count > 0)
            ? new ColumnSet(columns.ToArray())
            : new ColumnSet(true);

        var retrieved = _service.Retrieve(record.EntityName, record.Id, columnSet);
        ctx.FoundRecords[alias] = retrieved;

        Log($"      RetrieveRecord [{alias}] in '{record.EntityName}': " +
            $"{retrieved.Attributes.Count} Attribute geladen");
    }

    // ================================================================
    //  Pre-Flight-Diagnostics
    // ================================================================

    private void StepAssertEnvironment(TestStep step, TestContext ctx)
    {
        var checks = step.Filter
            ?? throw new InvalidOperationException("AssertEnvironment benötigt 'filter' mit Prüfungen.");

        var failures = new List<string>();

        foreach (var check in checks)
        {
            var checkType = check.Operator?.ToLowerInvariant() ?? "";
            var checkValue = check.Value?.ToString() ?? "";

            switch (checkType)
            {
                case "entityexists":
                    try
                    {
                        var testQuery = new QueryExpression(check.Field) { TopCount = 1, ColumnSet = new ColumnSet(false) };
                        _service.RetrieveMultiple(testQuery);
                        Log($"      OK: Entity '{check.Field}' existiert");
                    }
                    catch
                    {
                        failures.Add($"Entity '{check.Field}' existiert nicht in dieser Umgebung");
                    }
                    break;

                case "fieldexists":
                    try
                    {
                        var fieldQuery = new QueryExpression(check.Field) { TopCount = 1 };
                        fieldQuery.ColumnSet = new ColumnSet(checkValue);
                        _service.RetrieveMultiple(fieldQuery);
                        Log($"      OK: Feld '{checkValue}' auf '{check.Field}' existiert");
                    }
                    catch
                    {
                        failures.Add($"Feld '{checkValue}' auf Entity '{check.Field}' existiert nicht");
                    }
                    break;

                case "toggleactive":
                    var envVarFetch = $@"<fetch top='1'>
                        <entity name='environmentvariabledefinition'>
                            <attribute name='schemaname' />
                            <attribute name='defaultvalue' />
                            <filter>
                                <condition attribute='schemaname' operator='eq' value='{checkValue}' />
                            </filter>
                            <link-entity name='environmentvariablevalue' from='environmentvariabledefinitionid'
                                to='environmentvariabledefinitionid' link-type='outer' alias='val'>
                                <attribute name='value' />
                            </link-entity>
                        </entity>
                    </fetch>";
                    var envResult = _service.RetrieveMultiple(new FetchExpression(envVarFetch));
                    if (envResult.Entities.Count == 0)
                    {
                        failures.Add($"Umgebungsvariable '{checkValue}' nicht gefunden");
                    }
                    else
                    {
                        var defaultVal = envResult.Entities[0].GetAttributeValue<string>("defaultvalue") ?? "";
                        var currentVal = envResult.Entities[0].Contains("val.value")
                            ? ((AliasedValue)envResult.Entities[0]["val.value"]).Value?.ToString() ?? ""
                            : "";
                        var effective = !string.IsNullOrEmpty(currentVal) ? currentVal : defaultVal;
                        var isActive = IsToggleActive(effective);
                        if (isActive)
                            Log($"      OK: Toggle '{checkValue}' ist aktiv (Wert: {effective})");
                        else
                            failures.Add($"Toggle '{checkValue}' ist NICHT aktiv (Wert: {effective})");
                    }
                    break;

                case "pluginregistered":
                    var pluginFetch = $@"<fetch top='1'>
                        <entity name='sdkmessageprocessingstep'>
                            <attribute name='name' />
                            <attribute name='statecode' />
                            <link-entity name='plugintype' from='plugintypeid' to='plugintypeid' link-type='inner' alias='pt'>
                                <filter>
                                    <condition attribute='name' operator='like' value='%{checkValue}%' />
                                </filter>
                            </link-entity>
                        </entity>
                    </fetch>";
                    var pluginResult = _service.RetrieveMultiple(new FetchExpression(pluginFetch));
                    if (pluginResult.Entities.Count > 0)
                    {
                        var state = pluginResult.Entities[0].GetAttributeValue<OptionSetValue>("statecode");
                        if (state?.Value == 0)
                            Log($"      OK: Plugin '{checkValue}' registriert und aktiv");
                        else
                            failures.Add($"Plugin '{checkValue}' ist registriert aber DEAKTIVIERT");
                    }
                    else
                    {
                        failures.Add($"Plugin '{checkValue}' ist NICHT registriert");
                    }
                    break;

                default:
                    failures.Add($"Unbekannter Check-Typ: '{checkType}'");
                    break;
            }
        }

        if (failures.Count > 0)
        {
            var message = $"Pre-Flight fehlgeschlagen:\n  - {string.Join("\n  - ", failures)}";
            Log($"      FAIL: {message}");
            throw new InvalidOperationException(message);
        }

        Log($"      Alle {checks.Count} Pre-Flight-Checks bestanden");
    }

    private static bool IsToggleActive(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var lower = value.Trim().ToLowerInvariant();
        return lower == "1" || lower == "ja" || lower == "yes" || lower == "true"
            || lower == "aktiv" || lower == "active" || lower == "enabled"
            || lower == "ein" || lower == "on" || lower == "wahr";
    }

    // ================================================================
    //  Cleanup
    // ================================================================

    private void Cleanup(TestContext ctx, TestCaseResult tcResult)
    {
        var toDelete = ctx.CreatedEntities.AsEnumerable().Reverse().ToList();
        if (toDelete.Count == 0) return;

        // 1 StepResult pro Test-Cleanup-Phase (nicht pro Record), damit der
        // Steps-Tab die Cleanup-Zusammenfassung als eine Zeile zeigt statt
        // einer potentiell langen Liste von Delete-Eintraegen.
        var cleanupResult = new StepResult
        {
            StepNumber = 9000,
            Action = "Cleanup",
            Description = "Cleanup"
        };
        var sw = Stopwatch.StartNew();

        // KeepRecords=true: Testdaten bewusst behalten. Cleanup-StepResult
        // trotzdem schreiben, damit die Cleanup-Zeile im Steps-Tab sichtbar
        // bleibt und dokumentiert, warum nichts geloescht wurde.
        if (KeepRecords)
        {
            sw.Stop();
            Log($"    Cleanup übersprungen (keeprecords=true, {toDelete.Count} Records behalten)");
            cleanupResult.Description = $"Cleanup übersprungen: {toDelete.Count} Records behalten (keeprecords=true)";
            cleanupResult.Success = true;
            cleanupResult.DurationMs = sw.ElapsedMilliseconds;
            tcResult.StepResults.Add(cleanupResult);
            return;
        }

        int deleted = 0, failed = 0;
        var firstError = "";

        foreach (var item in toDelete)
        {
            try
            {
                _service.Delete(item.EntityName, item.Id);
                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                if (string.IsNullOrEmpty(firstError)) firstError = $"{item.EntityName} {item.Id}: {ex.Message}";
                Log($"    Löschen fehlgeschlagen: {item.EntityName} {item.Id} -- {ex.Message}");
            }
        }

        sw.Stop();
        Log($"    Cleanup: {deleted} gelöscht, {failed} fehlgeschlagen");

        cleanupResult.Description = $"Cleanup: {deleted} gelöscht, {failed} fehlgeschlagen";
        cleanupResult.Success = failed == 0;
        cleanupResult.Message = failed > 0 ? firstError : null;
        cleanupResult.DurationMs = sw.ElapsedMilliseconds;
        tcResult.StepResults.Add(cleanupResult);
    }

    // ================================================================
    //  Hilfsmethoden
    // ================================================================

    private void ApplyFields(
        Entity entity, Dictionary<string, object?> fields,
        bool allowNull = false)
    {
        foreach (var kvp in fields)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            if (value == null)
            {
                if (allowNull) entity[key] = null;
                continue;
            }

            // Web API @odata.bind Lookup-Syntax: "feldname_target@odata.bind" = "/entitysets(guid)"
            if (key.EndsWith("@odata.bind", StringComparison.OrdinalIgnoreCase))
            {
                var strVal = ConvertValue(value)?.ToString() ?? "";
                var bindResult = ParseODataBind(key, strVal);
                if (bindResult.HasValue)
                {
                    entity[bindResult.Value.FieldName] =
                        new EntityReference(bindResult.Value.TargetEntity, bindResult.Value.Id);
                    continue;
                }
            }

            var converted = ConvertValue(value);

            // Metadata-basierte Typerkennung für alle Felder
            var attrType = _entityMetadata.GetAttributeType(entity.LogicalName, key);
            if (attrType != null)
            {
                switch (attrType.Value)
                {
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Lookup:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Customer:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Owner:
                        if (converted is string sv && Guid.TryParse(sv, out var g))
                        {
                            var target = _entityMetadata.GetLookupTarget(entity.LogicalName, key);
                            if (target != null)
                                converted = new EntityReference(target, g);
                        }
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Money:
                        converted = converted switch
                        {
                            decimal d => new Money(d),
                            int i2 => new Money(i2),
                            double d2 => new Money((decimal)d2),
                            string ms when decimal.TryParse(ms,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var md)
                                => new Money(md),
                            _ => converted
                        };
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Decimal:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Double:
                        converted = converted switch
                        {
                            int i4 => (decimal)i4,
                            double d3 => (decimal)d3,
                            string ds when decimal.TryParse(ds,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var dd)
                                => dd,
                            _ => converted
                        };
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.DateTime:
                        if (converted is string ds2)
                        {
                            // Versuche verschiedene DateTime-Formate
                            if (DateTime.TryParse(ds2, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal, out var dtVal))
                                converted = dtVal;
                            else if (ds2.Contains("_") && ds2.Length >= 15)
                            {
                                // ITT TIMESTAMP-Format: "20260406_143000_123"
                                var parts = ds2.Split('_');
                                if (parts.Length >= 2 && parts[0].Length == 8 && parts[1].Length >= 6)
                                {
                                    var dtStr = $"{parts[0].Substring(0,4)}-{parts[0].Substring(4,2)}-{parts[0].Substring(6,2)}T{parts[1].Substring(0,2)}:{parts[1].Substring(2,2)}:{parts[1].Substring(4,2)}Z";
                                    if (DateTime.TryParse(dtStr, System.Globalization.CultureInfo.InvariantCulture,
                                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var dtVal2))
                                        converted = dtVal2;
                                }
                            }
                        }
                        break;

                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Picklist:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.State:
                    case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Status:
                        if (converted is not OptionSetValue)
                        {
                            converted = converted switch
                            {
                                int i3 => new OptionSetValue(i3),
                                long l => new OptionSetValue((int)l),
                                decimal dec => new OptionSetValue((int)dec),
                                string ps when int.TryParse(ps, out var pi)
                                    => new OptionSetValue(pi),
                                _ => converted
                            };
                        }
                        break;
                }
            }

            entity[key] = converted;
        }
    }

    /// <summary>
    /// Parst Web API @odata.bind Syntax: "parentcustomerid_account@odata.bind" = "/accounts(guid)"
    /// Gibt den SDK-Feldnamen, die Ziel-Entity und die GUID zurück.
    /// </summary>
    private (string FieldName, string TargetEntity, Guid Id)? ParseODataBind(string bindKey, string bindValue)
    {
        // Feldnamen extrahieren: @odata.bind-Suffix entfernen
        var fieldPart = bindKey.Substring(0, bindKey.Length - "@odata.bind".Length);

        // Strategie: Ziel-Entity immer aus dem Bind-VALUE extrahieren (am zuverlässigsten)
        // Der Wert hat das Format "/accounts(guid)" oder "/lm_bestellunges(guid)"
        var entitySetFromValue = ExtractEntityFromBindValue(bindValue);

        string fieldName;
        string targetEntity;

        if (entitySetFromValue != null)
        {
            // EntitySetName aus dem Wert zu LogicalName auflösen
            targetEntity = _entityMetadata.ResolveLogicalName(entitySetFromValue);

            // Feldname: versuche das Ziel-Entity-Suffix vom Feldteil abzuschneiden
            // "parentcustomerid_account" -> "parentcustomerid" ("_account" entfernen)
            // "lm_bestellungid" -> "lm_bestellungid" (kein Standard-Entity-Suffix)
            var suffix = "_" + targetEntity;
            if (fieldPart.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                fieldName = fieldPart.Substring(0, fieldPart.Length - suffix.Length);
            else
                fieldName = fieldPart; // Custom Entity Lookup: Feldname IST der vollständige Feldteil
        }
        else
        {
            // Fallback: am letzten Unterstrich aufteilen
            var lastUnderscore = fieldPart.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                fieldName = fieldPart.Substring(0, lastUnderscore);
                targetEntity = fieldPart.Substring(lastUnderscore + 1);
            }
            else
            {
                fieldName = fieldPart;
                targetEntity = fieldPart;
            }
        }

        // EntitySetName zu LogicalName auflösen
        targetEntity = _entityMetadata.ResolveLogicalName(targetEntity);

        // GUID aus dem Wert extrahieren: "/accounts(5c013fbf-...)"
        var match = System.Text.RegularExpressions.Regex.Match(
            bindValue, @"\(([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)");
        if (match.Success && Guid.TryParse(match.Groups[1].Value, out var id))
        {
            return (fieldName, targetEntity, id);
        }

        return null;
    }

    private static string? ExtractEntityFromBindValue(string bindValue)
    {
        // Entity-Set-Name aus dem "/accounts(guid)"-Pattern extrahieren
        var match = System.Text.RegularExpressions.Regex.Match(bindValue, @"^/?(\w+)\(");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static object ConvertValue(object value)
    {
        if (value is JToken jt)
        {
            return jt.Type switch
            {
                JTokenType.String => jt.Value<string>()!,
                JTokenType.Integer => jt.Value<int>(),
                JTokenType.Float => (object)jt.Value<decimal>(),
                JTokenType.Boolean => jt.Value<bool>(),
                JTokenType.Null => null!,
                _ => jt.ToString()
            };
        }

        // Int64 -> Int32 für Dataverse-Kompatibilität
        if (value is long l) return (int)l;

        return value;
    }

    private Dictionary<string, object?> ResolveFieldValues(
        Dictionary<string, object?> fields, TestContext ctx)
    {
        var resolved = new Dictionary<string, object?>(
            fields, StringComparer.OrdinalIgnoreCase);

        foreach (var key in resolved.Keys.ToList())
        {
            string? strVal = resolved[key] switch
            {
                string s => s,
                JToken { Type: JTokenType.String } jt => jt.Value<string>(),
                _ => null
            };

            if (strVal == null) continue;

            // Platzhalter auflösen über die PlaceholderEngine
            strVal = _placeholderEngine.Resolve(strVal, ctx);

            resolved[key] = strVal;
        }

        return resolved;
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }
}
