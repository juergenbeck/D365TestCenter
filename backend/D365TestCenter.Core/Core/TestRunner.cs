using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace itt.IntegrationTests.Core;

/// <summary>
/// Orchestriert die Integrationstestausführung:
/// Setup → Steps → CDH-Logs → Assertions → Cleanup.
/// Voll kompatibel mit dem FGTestTool-JSON-Format.
/// </summary>
public sealed class TestRunner
{
    private readonly IOrganizationService _service;
    private readonly TestDataFactory _dataFactory;
    private readonly PlaceholderEngine _placeholderEngine;
    private readonly AsyncPluginWaiter _waiter;
    private readonly AssertionEngine _assertionEngine;
    private readonly StringBuilder _log;

    private const string ContactEntity = "contact";
    private const string AccountEntity = "account";
    private const string ContactSourceEntity = "markant_fg_contactsource";
    private const string MembershipSourceEntity = "markant_fg_membershipsource";
    private const string CdhLoggingEntity = "markant_fg_logging";
    private const string PlatformBridgeEntity = "markant_bridge_pf_record";

    private const string CsContactLookup = "markant_contactid";
    private const string CsSourceSystem = "markant_fg_sourcesystemcode";
    private const string CsExternalId = "markant_externalid";
    private const string CsExternalIdModifiedOn = "markant_externalid_modifiedon";

    private const string LogContactLookup = "markant_contactid";
    private const string LogContactSourceLookup = "markant_fg_contactsourceid";
    private const string LogNameField = "markant_name";
    private const string LogCreatedOn = "createdon";

    private static readonly HashSet<string> OptionSetFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "markant_fg_sourcesystemcode", "markant_gender", "gendercode",
        "markant_gendercode", "markant_bridgestatuscode", "markant_eventtype",
        "markant_membershipstatuscode", "statecode", "statuscode"
    };

    private static readonly Dictionary<string, string> LookupFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["markant_parentcustomerid"] = AccountEntity,
        ["parentcustomerid"] = AccountEntity,
        ["markant_accountid"] = AccountEntity,
        ["markant_communicationlanguageid"] = "markant_communicationlanguage"
    };

    // DateField-Mapping: Q2-Feldnamen auf markant_fg_contactsource
    private static readonly Dictionary<string, string> DateFieldMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["markant_firstname"] = "markant_firstname_modifiedon",
        ["markant_lastname"] = "markant_lastname_modifiedon",
        ["markant_emailaddress1"] = "markant_emailaddress1_modifiedon",
        ["markant_gendercode"] = "markant_gender_modifiedon",
        ["markant_jobtitle"] = "markant_jobtitle_modifiedon",
        ["markant_telephone1"] = "markant_telephone1_modifiedon",
        ["markant_telephone2"] = "markant_telephone2_modifiedon",
        ["markant_mobilephone"] = "markant_mobilephone_modifiedon",
        ["markant_middlename"] = "markant_middlename_modifiedon",
        ["markant_academictitle"] = "markant_academictitle_modifiedon",
        ["markant_parentcustomerid"] = "markant_parentcustomerid_modifiedon",
        ["markant_communicationlanguageid"] = "markant_communicationlanguage_modifiedon"
    };

    /// <summary>
    /// Wird nach jedem Testfall aufgerufen (index, total, result).
    /// Ermöglicht Fortschritts-Updates im TestRun-Record.
    /// </summary>
    public event Action<int, int, TestCaseResult>? OnTestCompleted;

    public TestRunner(IOrganizationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dataFactory = new TestDataFactory();
        _placeholderEngine = new PlaceholderEngine();
        _waiter = new AsyncPluginWaiter();
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
        // Expand data-driven tests: each dataRow becomes a separate test run
        var expandedTests = ExpandDataDrivenTests(testCases);

        var result = new TestRunResult
        {
            StartedAt = DateTime.UtcNow,
            TotalCount = expandedTests.Count
        };

        Log("=== FIELD GOVERNANCE INTEGRATIONSTEST ===");
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

            if (tc.NotImplemented)
            {
                var notImpl = new TestCaseResult
                {
                    TestId = tc.Id,
                    Title = tc.Title,
                    Outcome = TestOutcome.NotImplemented
                };
                result.Results.Add(notImpl);
                Log($"-- [{index}/{expandedTests.Count}] [{tc.Id}] {tc.Title} -> NOT_IMPLEMENTED --");
                OnTestCompleted?.Invoke(index, expandedTests.Count, notImpl);
                continue;
            }

            // dependsOn: skip if a required test has not passed
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
                        NotImplemented = tc.NotImplemented,
                        DataMode = tc.DataMode,
                        CleanupAfterTest = tc.CleanupAfterTest,
                        AsyncWaitOverrideSeconds = tc.AsyncWaitOverrideSeconds,
                        Preconditions = tc.Preconditions,
                        Steps = tc.Steps,
                        Assertions = tc.Assertions,
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
            Log("  Phase 1: Setup (Preconditions)...");
            ctx = SetupPreconditions(tc);
            ctx.CurrentDataRow = dataRow;

            Log("  Phase 2: Teststeps ausführen...");
            ExecuteSteps(tc, ctx);

            Log("  Phase 3: CDH-Logs auswerten...");
            ReadCdhLogs(ctx);

            Log("  Phase 4: Assertions prüfen...");
            var allPassed = EvaluateAssertions(tc, ctx, tcResult);

            tcResult.Outcome = allPassed ? TestOutcome.Passed : TestOutcome.Failed;
            Log($"  -> {(allPassed ? "BESTANDEN" : "FEHLGESCHLAGEN")}");
        }
        catch (Exception ex)
        {
            tcResult.Outcome = TestOutcome.Error;
            tcResult.ErrorMessage = ex.Message;
            Log($"  -> FEHLER: {ex.Message}");
        }
        finally
        {
            if (ctx != null && tc.CleanupAfterTest)
            {
                try
                {
                    Log("  Phase 5: Cleanup...");
                    Cleanup(ctx);
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

    // ════════════════════════════════════════════════════════════════
    //  Phase 1: Setup (Preconditions)
    // ════════════════════════════════════════════════════════════════

    private TestContext SetupPreconditions(TestCase tc)
    {
        var ctx = new TestContext { TestStartUtc = DateTime.UtcNow, TestId = tc.Id };
        var pre = tc.Preconditions;
        var dataMode = tc.DataMode ?? "template";

        // Account erstellen (FGTestTool: createAccount Flag)
        if (pre.CreateAccount)
        {
            var accountData = dataMode == "bogus"
                ? _dataFactory.GenerateBogusAccountData()
                : _dataFactory.ResolveTemplateData(pre.AccountData, ctx);

            var entity = new Entity(AccountEntity);
            ApplyFields(entity, accountData);
            ctx.AccountId = _service.Create(entity);
            ctx.CreatedEntities.Add((AccountEntity, ctx.AccountId.Value));
            Log($"    Account erstellt: {ctx.AccountId.Value}");
        }

        // Contact erstellen (FGTestTool: createContact Flag)
        if (pre.CreateContact)
        {
            var contactData = dataMode == "bogus"
                ? _dataFactory.GenerateBogusContactData()
                : _dataFactory.ResolveTemplateData(pre.ContactData, ctx);

            var entity = new Entity(ContactEntity);
            ApplyFields(entity, contactData);
            if (ctx.AccountId.HasValue)
                entity["parentcustomerid"] = new EntityReference(AccountEntity, ctx.AccountId.Value);
            ctx.ContactId = _service.Create(entity);
            ctx.CreatedEntities.Add((ContactEntity, ctx.ContactId.Value));
            Log($"    Contact erstellt: {ctx.ContactId.Value}");
        }

        // ContactSources anlegen (ExistingContactSources = FGTestTool-Name)
        foreach (var csSetup in pre.ExistingContactSources)
        {
            var csFields = ResolveFieldValues(
                _dataFactory.ResolveTemplateData(csSetup.Fields, ctx), ctx);
            AutoSetDateFields(csFields);

            var externalId = csSetup.ExternalId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
            var sourceCode = csSetup.SourceSystemCode;
            var alias = !string.IsNullOrEmpty(csSetup.Alias)
                ? csSetup.Alias
                : sourceCode.ToString();

            var csEntity = new Entity(ContactSourceEntity);
            csEntity[CsSourceSystem] = new OptionSetValue(sourceCode);
            csEntity[CsExternalId] = externalId;
            csEntity[CsExternalIdModifiedOn] = DateTime.UtcNow;
            if (csSetup.LinkToContact && ctx.ContactId.HasValue)
                csEntity[CsContactLookup] = new EntityReference(ContactEntity, ctx.ContactId.Value);
            ApplyFields(csEntity, csFields);

            var csId = _service.Create(csEntity);
            ctx.ContactSourceIds[alias] = csId;
            ctx.CreatedEntities.Add((ContactSourceEntity, csId));
            Log($"    ContactSource [{alias}, SourceSystem={sourceCode}] erstellt: {csId} " +
                $"(ExternalId={externalId})");

            if (csSetup.WaitForGovernance && ctx.ContactId.HasValue)
            {
                Log($"    Warte auf Governance für [{alias}]...");
                var timeout = csSetup.AsyncWaitOverrideSeconds ?? 120;
                _waiter.WaitForGovernanceCompletionBySource(
                    _service, ctx.ContactId.Value, csId, DateTime.UtcNow, timeout);
                Log($"    Governance für [{alias}] abgeschlossen");
            }

            // OE-T9 Fix: Delay zwischen CS-Anlagen für verschiedene Timestamps
            Thread.Sleep(500);
        }

        // ContactInitialState überschreiben
        if (pre.ContactInitialState.Count > 0 && ctx.ContactId.HasValue)
        {
            Log("    ContactInitialState überschreiben...");
            var stateFields = ResolveFieldValues(pre.ContactInitialState, ctx);
            var updateEntity = new Entity(ContactEntity, ctx.ContactId.Value);
            ApplyFields(updateEntity, stateFields, allowNull: true);
            _service.Update(updateEntity);
            Thread.Sleep(500);
        }

        // Snapshot des Kontakts für Unchanged-Assertions
        if (ctx.ContactId.HasValue)
        {
            ctx.CurrentContact = _service.Retrieve(
                ContactEntity, ctx.ContactId.Value, new ColumnSet(true));
        }

        ctx.TestStartUtc = DateTime.UtcNow;
        return ctx;
    }

    // ════════════════════════════════════════════════════════════════
    //  Phase 2: Steps ausführen
    // ════════════════════════════════════════════════════════════════

    private void ExecuteSteps(TestCase tc, TestContext ctx)
    {
        foreach (var step in tc.Steps)
        {
            Log($"    Step {step.StepNumber}: {step.Description} [{step.Action}]");

            var resolvedFields = ResolveFieldValues(
                _dataFactory.ResolveTemplateData(step.Fields, ctx), ctx);

            switch (step.Action.ToUpperInvariant())
            {
                case "CREATECONTACTSOURCE":
                    StepCreateContactSource(step, ctx, resolvedFields);
                    break;

                case "UPDATECONTACTSOURCE":
                    StepUpdateContactSource(step, ctx, resolvedFields);
                    break;

                case "UPDATECONTACT":
                    StepUpdateContact(ctx, resolvedFields);
                    break;

                case "CREATEPLATFORMBRIDGERECORD":
                    StepCreatePlatformBridge(step, ctx, resolvedFields);
                    break;

                case "CALLGOVERNANCEAPI":
                case "CALLGOVERNANCEAPICONTACT":
                    StepCallGovernanceForContact(ctx);
                    break;

                case "CALLGOVERNANCEAPICONTACTSOURCE":
                    StepCallGovernanceForContactSource(step, ctx);
                    break;

                // ── Generische Actions ─────────────────────────────

                case "CREATERECORD":
                    StepCreateGenericRecord(step, ctx, resolvedFields);
                    break;

                case "UPDATERECORD":
                    StepUpdateGenericRecord(step, ctx, resolvedFields);
                    break;

                case "DELETERECORD":
                    StepDeleteGenericRecord(step, ctx);
                    break;

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
        }
    }

    private void StepCreateContactSource(
        TestStep step, TestContext ctx,
        Dictionary<string, object?> resolvedFields)
    {
        var sourceCode = step.SourceSystemCode ?? 4;
        var externalId = step.ExternalId ?? Guid.NewGuid().ToString("N").Substring(0, 8);
        var alias = step.TargetAlias ?? $"step_{step.StepNumber}";

        AutoSetDateFields(resolvedFields);

        var entity = new Entity(ContactSourceEntity);
        entity[CsSourceSystem] = new OptionSetValue(sourceCode);
        entity[CsExternalId] = externalId;
        entity[CsExternalIdModifiedOn] = DateTime.UtcNow;
        if (step.LinkToContact && ctx.ContactId.HasValue)
            entity[CsContactLookup] = new EntityReference(ContactEntity, ctx.ContactId.Value);
        ApplyFields(entity, resolvedFields);

        var csId = _service.Create(entity);
        ctx.ContactSourceIds[alias] = csId;
        ctx.CreatedEntities.Add((ContactSourceEntity, csId));
        Log($"      ContactSource [{alias}, SourceSystem={sourceCode}] erstellt: {csId}");

        if (step.WaitForGovernance && ctx.ContactId.HasValue)
        {
            _waiter.WaitForGovernanceCompletionBySource(
                _service, ctx.ContactId.Value, csId, DateTime.UtcNow, step.TimeoutSeconds);
            Log($"      Governance für [{alias}] abgeschlossen");
        }
    }

    private void StepUpdateContactSource(
        TestStep step, TestContext ctx,
        Dictionary<string, object?> resolvedFields)
    {
        // Alias-basierter Lookup (FGTestTool-kompatibel) oder SourceSystem-Fallback
        var csId = ctx.ResolveContactSourceId(step.TargetAlias, step.SourceSystemCode);

        AutoSetDateFields(resolvedFields);

        var entity = new Entity(ContactSourceEntity, csId);
        ApplyFields(entity, resolvedFields, allowNull: true);
        _service.Update(entity);
        Log($"      ContactSource [{step.TargetAlias ?? step.SourceSystemCode?.ToString()}] aktualisiert: {csId}");

        if (step.WaitForGovernance && ctx.ContactId.HasValue)
        {
            _waiter.WaitForGovernanceCompletionBySource(
                _service, ctx.ContactId.Value, csId, DateTime.UtcNow, step.TimeoutSeconds);
            Log($"      Governance abgeschlossen");
        }
    }

    private void StepUpdateContact(
        TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        if (!ctx.ContactId.HasValue)
            throw new InvalidOperationException("Kein Contact im Kontext vorhanden.");

        var entity = new Entity(ContactEntity, ctx.ContactId.Value);
        ApplyFields(entity, resolvedFields, allowNull: true);
        _service.Update(entity);
        Log($"      Contact aktualisiert: {ctx.ContactId.Value}");
    }

    private void StepCreatePlatformBridge(
        TestStep step, TestContext ctx,
        Dictionary<string, object?> resolvedFields)
    {
        var entity = new Entity(PlatformBridgeEntity);
        ApplyFields(entity, resolvedFields);

        var bridgeId = _service.Create(entity);
        ctx.BridgeRecordIds.Add(bridgeId);
        ctx.CreatedEntities.Add((PlatformBridgeEntity, bridgeId));
        Log($"      PlatformBridge erstellt: {bridgeId}");

        if (step.WaitForGovernance)
        {
            _waiter.WaitForBridgeProcessing(_service, bridgeId, step.TimeoutSeconds);
            Log($"      Bridge-Verarbeitung abgeschlossen");
        }
    }

    private void StepCallGovernanceForContact(TestContext ctx)
    {
        if (!ctx.ContactId.HasValue)
            throw new InvalidOperationException(
                "Kein Contact für GovernanceApi vorhanden.");

        var request = new OrganizationRequest("markant_RunFieldGovernanceForContact");
        request["ContactId"] = ctx.ContactId.Value;
        _service.Execute(request);
        Log($"      markant_RunFieldGovernanceForContact aufgerufen: {ctx.ContactId.Value}");
    }

    private void StepCallGovernanceForContactSource(TestStep step, TestContext ctx)
    {
        var csId = ctx.ResolveContactSourceId(step.TargetAlias, step.SourceSystemCode);

        var request = new OrganizationRequest(
            "markant_RunFieldGovernanceForContactSource");
        request["ContactSourceId"] = csId;
        _service.Execute(request);
        Log($"      markant_RunFieldGovernanceForContactSource: {csId}");
    }

    // ════════════════════════════════════════════════════════════════
    //  Generische Actions
    // ════════════════════════════════════════════════════════════════

    private static readonly GenericRecordWaiter _recordWaiter = new GenericRecordWaiter();

    private void StepCreateGenericRecord(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var entityName = step.Entity
            ?? throw new InvalidOperationException("CreateRecord benötigt 'entity'.");
        var alias = step.Alias ?? $"record_{step.StepNumber}";

        var entity = new Entity(entityName);
        ApplyFields(entity, resolvedFields);

        var id = _service.Create(entity);
        ctx.RegisterRecord(alias, entityName, id);
        Log($"      CreateRecord [{alias}] in '{entityName}': {id}");
    }

    private void StepUpdateGenericRecord(
        TestStep step, TestContext ctx, Dictionary<string, object?> resolvedFields)
    {
        var alias = step.RecordRef ?? step.Alias ?? step.TargetAlias
            ?? throw new InvalidOperationException("UpdateRecord benötigt 'recordRef', 'alias' oder 'targetAlias'.");

        // Resolve {RECORD:alias} placeholder in recordRef
        if (alias.StartsWith("{RECORD:", StringComparison.OrdinalIgnoreCase) && alias.EndsWith("}"))
            alias = alias.Substring("{RECORD:".Length, alias.Length - "{RECORD:".Length - 1);

        var recordId = ctx.ResolveRecordId(alias);
        var entityName = step.Entity ?? ctx.ResolveRecordEntityName(alias);

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
        var entityName = step.Entity ?? ctx.ResolveRecordEntityName(alias);

        _service.Delete(entityName, recordId);
        Log($"      DeleteRecord [{alias}] in '{entityName}': {recordId}");
    }

    private void StepWaitForRecord(TestStep step, TestContext ctx)
    {
        var entityName = step.Entity
            ?? throw new InvalidOperationException("WaitForRecord benötigt 'entity'.");
        var filters = step.Filter
            ?? throw new InvalidOperationException("WaitForRecord benötigt 'filter'.");
        var alias = step.Alias ?? $"found_{step.StepNumber}";

        // Resolve placeholders in filter values
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
        var entityName = step.Entity ?? ctx.ResolveRecordEntityName(alias);
        var fieldName = step.Fields.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("WaitForFieldValue: 'field' in fields fehlt.");
        var expectedValue = step.StepExpectedValue
            ?? throw new InvalidOperationException("WaitForFieldValue benötigt 'expectedValue'.");

        // Resolve placeholder in expected value
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
            request[kvp.Key] = kvp.Value;
        }

        _service.Execute(request);
        Log($"      CallCustomApi '{apiName}' aufgerufen");
    }

    // ════════════════════════════════════════════════════════════════
    //  Pre-Flight-Diagnostics
    // ════════════════════════════════════════════════════════════════

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

    // ════════════════════════════════════════════════════════════════
    //  Phase 3: CDH-Logs lesen
    // ════════════════════════════════════════════════════════════════

    private void ReadCdhLogs(TestContext ctx)
    {
        if (!ctx.ContactId.HasValue)
        {
            Log("    Kein Contact vorhanden -- CDH-Logs übersprungen");
            return;
        }

        var qe = new QueryExpression(CdhLoggingEntity)
        {
            ColumnSet = new ColumnSet(
                LogNameField, CdhLogEntry.DiagnosticsFieldName, LogContactLookup,
                LogContactSourceLookup, LogCreatedOn),
            TopCount = 50
        };

        qe.Criteria.AddCondition(
            LogContactLookup, ConditionOperator.Equal, ctx.ContactId.Value);
        qe.Criteria.AddCondition(
            LogCreatedOn, ConditionOperator.GreaterEqual,
            ctx.TestStartUtc.AddSeconds(-2));
        qe.Orders.Add(new OrderExpression(LogCreatedOn, OrderType.Descending));

        var results = _service.RetrieveMultiple(qe);
        ctx.CdhLogs = results.Entities.ToList();

        Log($"    {ctx.CdhLogs.Count} CDH-Log-Einträge geladen");
        foreach (var e in ctx.CdhLogs)
        {
            var entry = CdhLogEntry.FromEntity(e);
            Log($"    - {entry.Name} (erstellt: {entry.CreatedOn:HH:mm:ss}, " +
                $"ContactUpdated={entry.ContactUpdated})");
            if (entry.UpdatedFields.Count > 0)
                Log($"      Aktualisierte Felder: {string.Join(", ", entry.UpdatedFields)}");
            if (entry.Errors.Count > 0)
                Log($"      Fehler: {string.Join("; ", entry.Errors)}");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Phase 4: Assertions auswerten
    // ════════════════════════════════════════════════════════════════

    private bool EvaluateAssertions(
        TestCase tc, TestContext ctx, TestCaseResult result)
    {
        bool allPassed = true;

        foreach (var assertion in tc.Assertions)
        {
            var ar = _assertionEngine.Evaluate(assertion, ctx, _service);
            result.Assertions.Add(ar);

            if (!ar.Passed) allPassed = false;

            Log($"    {(ar.Passed ? "OK" : "FAIL")} {assertion.Description}: {ar.Message}");
        }

        result.CdhLogEntries = ctx.CdhLogs.Select(CdhLogEntry.FromEntity).ToList();

        return allPassed;
    }

    // ════════════════════════════════════════════════════════════════
    //  Phase 5: Cleanup
    // ════════════════════════════════════════════════════════════════

    private void Cleanup(TestContext ctx)
    {
        var toDelete = ctx.CreatedEntities.AsEnumerable().Reverse().ToList();
        int deleted = 0, failed = 0;

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
                Log($"    Löschen fehlgeschlagen: {item.EntityName} {item.Id} -- {ex.Message}");
            }
        }

        Log($"    Cleanup: {deleted} gelöscht, {failed} fehlgeschlagen");
    }

    // ════════════════════════════════════════════════════════════════
    //  Hilfsmethoden
    // ════════════════════════════════════════════════════════════════

    private static void ApplyFields(
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

            var converted = ConvertValue(value);

            if (OptionSetFields.Contains(key))
            {
                converted = converted switch
                {
                    int i => new OptionSetValue(i),
                    long l => new OptionSetValue((int)l),
                    decimal d => new OptionSetValue((int)d),
                    OptionSetValue => converted,
                    _ => converted
                };
            }
            else if (LookupFields.TryGetValue(key, out var targetEntity)
                     && converted is string s && Guid.TryParse(s, out var guid))
            {
                converted = new EntityReference(targetEntity, guid);
            }

            entity[key] = converted;
        }
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

            // Platzhalter auflösen (FGTestTool + Erweiterungen)
            strVal = strVal
                .Replace("{CONTACT_ID}", ctx.ContactId?.ToString() ?? "")
                .Replace("{ACCOUNT_ID}", ctx.AccountId?.ToString() ?? "")
                .Replace("{TESTID}", ctx.TestId)
                .Replace("{TIMESTAMP}", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff"))
                .Replace("{TIMESTAMP_COMPACT}", DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"))
                .Replace("{GUID}", Guid.NewGuid().ToString("N").Substring(0, 8))
                .Replace("{NOW_UTC}", DateTime.UtcNow.ToString("O"));

            foreach (var csKvp in ctx.ContactSourceIds)
                strVal = strVal.Replace($"{{CS:{csKvp.Key}}}", csKvp.Value.ToString());

            for (int i = 0; i < ctx.BridgeRecordIds.Count; i++)
                strVal = strVal.Replace($"{{BRIDGE:{i}}}", ctx.BridgeRecordIds[i].ToString());

            resolved[key] = strVal;
        }

        return resolved;
    }

    private static void AutoSetDateFields(Dictionary<string, object?> fields)
    {
        var now = DateTime.UtcNow;
        foreach (var mapping in DateFieldMapping)
        {
            if (fields.ContainsKey(mapping.Key) && !fields.ContainsKey(mapping.Value))
                fields[mapping.Value] = now;
        }
    }

    private void Log(string message)
    {
        _log.AppendLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }
}
