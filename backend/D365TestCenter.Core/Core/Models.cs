using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Microsoft.Xrm.Sdk;

namespace D365TestCenter.Core;

// ═══════════════════════════════════════════════════════════════════════
//  JSON-Testfall-Definitionen (generisches Format)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Root-Objekt einer JSON-Testsuite-Datei.</summary>
public sealed class TestSuiteDefinition
{
    [JsonProperty("suiteId")]
    public string SuiteId { get; set; } = "";

    [JsonProperty("suiteName")]
    public string SuiteName { get; set; } = "";

    [JsonProperty("suiteDescription")]
    public string? SuiteDescription { get; set; }

    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("jiraReference")]
    public string? JiraReference { get; set; }

    [JsonProperty("testCases")]
    public List<TestCase> TestCases { get; set; } = new();
}

/// <summary>
/// Einzelner Testfall innerhalb einer Suite.
/// Seit ADR-0004 (2026-04-23): Ein Test ist eine einzige geordnete Liste
/// von Actions. Es gibt kein getrenntes Preconditions- oder Assertions-
/// Array mehr — alles ist ein Step. Die JSON-Reihenfolge ist die
/// Ausfuehrungsreihenfolge. Assert ist ein Step-Action-Typ wie jeder
/// andere.
/// </summary>
public sealed class TestCase
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("steps")]
    public List<TestStep> Steps { get; set; } = new();

    // ── Datengetriebene Tests ─────────────────────────────────

    [JsonProperty("dataRows")]
    public List<Dictionary<string, object?>>? DataRows { get; set; }

    // ── Testfall-Abhängigkeiten ───────────────────────────────

    [JsonProperty("dependsOn")]
    public List<string>? DependsOn { get; set; }

    [JsonProperty("sharedContext")]
    public string? SharedContext { get; set; }
}

/// <summary>
/// JsonConverter: akzeptiert filter als Objekt {"field":"value"} oder Array [{field,operator,value}].
/// </summary>
public sealed class FilterListConverter : JsonConverter<List<FilterCondition>>
{
    public override List<FilterCondition>? ReadJson(JsonReader reader, Type objectType,
        List<FilterCondition>? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);

        if (token.Type == JTokenType.Array)
            return token.ToObject<List<FilterCondition>>(serializer)
                ?? new List<FilterCondition>();

        if (token.Type == JTokenType.Object)
        {
            var result = new List<FilterCondition>();
            foreach (var prop in ((JObject)token).Properties())
            {
                result.Add(new FilterCondition
                {
                    Field = prop.Name,
                    Operator = "eq",
                    Value = prop.Value.Type == JTokenType.Null ? null : prop.Value.ToString()
                });
            }
            return result;
        }

        return new List<FilterCondition>();
    }

    public override void WriteJson(JsonWriter writer, List<FilterCondition>? value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}

/// <summary>Filterkriterium für WaitForRecord und Query-Assertions.</summary>
public sealed class FilterCondition
{
    [JsonProperty("field")]
    public string Field { get; set; } = "";

    [JsonProperty("operator")]
    public string Operator { get; set; } = "eq";

    [JsonProperty("value")]
    public object? Value { get; set; }
}

/// <summary>
/// Einzelner Testschritt. Seit ADR-0004 umfasst TestStep alle Action-Typen
/// inklusive Assert. Die Properties sind so gewaehlt, dass sie je nach
/// Action-Typ andere Teilmengen benutzen:
///
///  - CreateRecord:      entity + alias + fields (+ columns)
///  - UpdateRecord:      alias|recordRef + fields
///  - DeleteRecord:      alias|recordRef
///  - Wait:              waitSeconds
///  - ExecuteRequest:    requestName + fields + waitSeconds
///  - CallCustomApi:     entity (=ApiName) + fields
///  - WaitForRecord:     entity + filter + alias (+ columns, timeoutSeconds)
///  - WaitForFieldValue: alias + fields (Feldname) + expectedValue
///  - RetrieveRecord:    alias (+ columns)
///  - AssertEnvironment: filter
///  - Assert:            target + field + operator + value + recordRef|entity+filter
/// </summary>
public sealed class TestStep
{
    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// Action-Typ. Gueltige Werte: CreateRecord, UpdateRecord, DeleteRecord,
    /// Wait, ExecuteRequest, CallCustomApi, RetrieveRecord, WaitForRecord,
    /// WaitForFieldValue, AssertEnvironment, Assert.
    /// </summary>
    [JsonProperty("action")]
    public string Action { get; set; } = "";

    [JsonProperty("entity")]
    public string? Entity { get; set; }

    [JsonProperty("alias")]
    public string? Alias { get; set; }

    [JsonProperty("recordRef")]
    public string? RecordRef { get; set; }

    [JsonProperty("filter")]
    [JsonConverter(typeof(FilterListConverter))]
    public List<FilterCondition>? Filter { get; set; }

    [JsonProperty("expectedValue")]
    public object? ExpectedValue { get; set; }

    [JsonProperty("columns")]
    public List<string>? Columns { get; set; }

    [JsonProperty("pollingIntervalMs")]
    public int PollingIntervalMs { get; set; } = 2000;

    [JsonProperty("maxDurationMs")]
    public int? MaxDurationMs { get; set; }

    [JsonProperty("fields")]
    public Dictionary<string, object?> Fields { get; set; } = new();

    [JsonProperty("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;

    [JsonProperty("waitSeconds")]
    public int? WaitSeconds { get; set; }

    [JsonProperty("delayMs")]
    public int? DelayMs { get; set; }

    /// <summary>SDK-Message-Name für ExecuteRequest (z.B. "Merge", "SetState", "Assign").</summary>
    [JsonProperty("requestName")]
    public string? RequestName { get; set; }

    /// <summary>
    /// ExecuteRequest-Output als Alias verfuegbar machen (A4 / ZastrPay-Feedback).
    /// Wenn gesetzt, werden alle OrganizationResponse-Werte unter diesem Alias
    /// im ctx.OutputAliases-Dict abgelegt und sind via Platzhalter
    /// {alias.outputs.X} oder {alias.outputs.X[type=Y]} (fuer
    /// EntityReferenceCollection-Filter) referenzierbar.
    /// Beispiel: ExecuteRequest QualifyLead mit outputAlias='qres' macht
    /// {qres.outputs.CreatedEntityReferences[type=account]} verfuegbar.
    /// </summary>
    [JsonProperty("outputAlias")]
    public string? OutputAlias { get; set; }

    // ── Assert-spezifische Properties ─────────────────────────

    /// <summary>
    /// Kontext-abhaengig (Action bestimmt die Semantik):
    ///  - Assert: Record, Query, Contact, Account, ContactSource, MembershipSource, BridgeRecord, Logging.
    ///  - SetEnvironmentVariable: effective (Default), currentValue, defaultValue.
    /// </summary>
    [JsonProperty("target")]
    public string? Target { get; set; }

    /// <summary>Assert: zu pruefendes Feld auf dem Ziel-Record.</summary>
    [JsonProperty("field")]
    public string? Field { get; set; }

    /// <summary>Assert: Equals, NotEquals, IsNull, IsNotNull, Contains, Exists, NotExists, DateSetRecently, RecordCount.</summary>
    [JsonProperty("operator")]
    public string? Operator { get; set; }

    /// <summary>Assert: erwarteter Wert als String (Platzhalter erlaubt).</summary>
    [JsonProperty("value")]
    public string? Value { get; set; }

    /// <summary>
    /// Fehlerverhalten fuer diesen Step. "continue" = Exception wird nur
    /// geloggt, Test laeuft weiter. "stop" = Test wird abgebrochen (Outcome=Error).
    /// Default ist action-abhaengig: "continue" fuer Assert, "stop" fuer alle
    /// anderen Actions.
    /// </summary>
    [JsonProperty("onError")]
    public string? OnError { get; set; }

    // ── EnvironmentVariable-Actions (SetEnvironmentVariable, RetrieveEnvironmentVariable) ──

    /// <summary>schemaname der environmentvariabledefinition (env-unabhaengig).</summary>
    [JsonProperty("schemaName")]
    public string? SchemaName { get; set; }

    /// <summary>
    /// RetrieveEnvironmentVariable: "effective" (Default), "currentValue", "defaultValue".
    /// Analog zum Retrieve-Pfad: Plugins lesen effective.
    /// </summary>
    [JsonProperty("source")]
    public string? Source { get; set; }

    // ── Query-Sortierung fuer FindRecord/WaitForRecord (1c Option A) ──

    /// <summary>
    /// Sortierung fuer WaitForRecord/FindRecord. Komma-separierter String im
    /// OData-Stil: "feldname asc|desc, feldname2 asc|desc". Default ist asc
    /// wenn nur Feldname ohne Richtung angegeben.
    /// Beispiel: "modifiedon asc, createdon desc"
    /// </summary>
    [JsonProperty("orderBy")]
    public string? OrderBy { get; set; }

    /// <summary>
    /// Maximalzahl Treffer fuer WaitForRecord/FindRecord. Default ist 1 (nimmt
    /// den ersten Treffer gemaess Sortierung). Stretch-Feature fuer top > 1
    /// (mehrere Alias-Records) ist aktuell nicht unterstuetzt.
    /// </summary>
    [JsonProperty("top")]
    public int? Top { get; set; }

    // ── Negative-Path-Tests (expectFailure / expectException) ──

    /// <summary>
    /// Wenn true: dieser Step darf (muss) eine Exception werfen um als
    /// Passed zu gelten. Ohne Exception wird der Step als Failed markiert
    /// mit Message "Expected exception but action succeeded".
    /// Fuer Assert-Actions ignoriert (Assert-Failure ist bereits durch
    /// NotEquals/IsNull/NotExists etc. abgedeckt).
    /// </summary>
    [JsonProperty("expectFailure")]
    public bool? ExpectFailure { get; set; }

    /// <summary>
    /// Erweiterte Variante: Spec zum Matchen der erwarteten Exception
    /// (messageContains, messageMatches, errorCode, httpStatus).
    /// expectException impliziert expectFailure=true. Alle gesetzten Felder
    /// werden mit AND verknuepft.
    /// </summary>
    [JsonProperty("expectException")]
    public ExpectExceptionSpec? ExpectException { get; set; }
}

/// <summary>
/// Match-Spezifikation fuer erwartete Exceptions (expectException).
/// Mehrere gesetzte Felder werden mit AND verknuepft.
/// messageContains und messageMatches sind exklusiv — beide zusammen ist
/// ein Validierungsfehler.
/// </summary>
public sealed class ExpectExceptionSpec
{
    /// <summary>
    /// Exception.Message muss diesen Substring enthalten. Case-insensitive.
    /// Exklusiv zu messageMatches.
    /// </summary>
    [JsonProperty("messageContains")]
    public string? MessageContains { get; set; }

    /// <summary>
    /// Exception.Message muss diesem Regex entsprechen.
    /// Case-insensitive Default. Exklusiv zu messageContains.
    /// </summary>
    [JsonProperty("messageMatches")]
    public string? MessageMatches { get; set; }

    /// <summary>
    /// Dataverse-Error-Code (z.B. "0x80040227") muss exakt matchen.
    /// Bei SDK-Pfad aus FaultException&lt;OrganizationServiceFault&gt;.Detail.ErrorCode.
    /// </summary>
    [JsonProperty("errorCode")]
    public string? ErrorCode { get; set; }

    /// <summary>
    /// HTTP-Status der API-Antwort muss matchen. Meist 400/403/404 bei
    /// Plugin-Faults. Bei pure SDK-Calls ignoriert (kein HTTP-Kontext).
    /// </summary>
    [JsonProperty("httpStatus")]
    public int? HttpStatus { get; set; }
}

/// <summary>
/// Snapshot eines EnvironmentVariable-Zustands fuer Auto-Restore im Cleanup.
/// Wird von SetEnvironmentVariable erzeugt wenn alias gesetzt ist.
/// </summary>
public sealed class EnvVarSnapshot
{
    public string SchemaName { get; set; } = "";
    public Guid DefinitionId { get; set; }

    /// <summary>"currentValue" oder "defaultValue" (zur Laufzeit resolved).</summary>
    public string ResolvedTarget { get; set; } = "";

    /// <summary>Existierte der Value-Record vor dem Set? Nur relevant bei ResolvedTarget=currentValue.</summary>
    public bool ValueRecordExistedBefore { get; set; }

    /// <summary>ID des Value-Records (bei ResolvedTarget=currentValue, nach dem Set gesetzt).</summary>
    public Guid? ValueRecordId { get; set; }

    /// <summary>Der Wert vor dem Set (bei ResolvedTarget=currentValue und ValueRecordExistedBefore=true).</summary>
    public string? OriginalValue { get; set; }

    /// <summary>Der DefaultValue vor dem Set (bei ResolvedTarget=defaultValue).</summary>
    public string? OriginalDefaultValue { get; set; }
}

/// <summary>
/// Interne Datenstruktur fuer AssertionEngine. Ein Assert-Step wird zur
/// Ausfuehrung in ein TestAssertion-Objekt uebersetzt, dann evaluiert, und
/// das Ergebnis wieder in den StepResult zurueckgemappt.
/// </summary>
public sealed class TestAssertion
{
    public string Target { get; set; } = "Query";
    public string Field { get; set; } = "";
    public string? Entity { get; set; }
    public string? RecordRef { get; set; }
    public List<FilterCondition>? Filter { get; set; }
    public string Operator { get; set; } = "Equals";
    public string? Value { get; set; }
    public string? Description { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
//  Laufzeit-Kontext (pro Testfall, nicht JSON-serialisiert)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Hält den Zustand während der Ausführung eines einzelnen Testfalls.</summary>
public sealed class TestContext
{
    public DateTime TestStartUtc { get; set; }
    public string TestId { get; set; } = "";

    /// <summary>Alias -> (EntityName, Guid). Für alle per CreateRecord/WaitForRecord registrierten Records.</summary>
    public Dictionary<string, (string EntityName, Guid Id)> Records { get; set; }
        = new Dictionary<string, (string, Guid)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alias -> Entity. Für WaitForRecord-Ergebnisse mit geladenen Feldern.</summary>
    public Dictionary<string, Entity> FoundRecords { get; set; }
        = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name -> Wert. Für {GENERATED:name}-Platzhalter.</summary>
    public Dictionary<string, string> GeneratedValues { get; set; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aktuelle Datenzeile für datengetriebene Tests ({ROW:field}).</summary>
    public Dictionary<string, object?>? CurrentDataRow { get; set; }

    /// <summary>Alle erstellten Entity-IDs für Cleanup nach dem Test.</summary>
    public List<(string EntityName, Guid Id)> CreatedEntities { get; set; } = new List<(string, Guid)>();

    /// <summary>Snapshots fuer EnvironmentVariable-Auto-Restore im Cleanup.</summary>
    public List<EnvVarSnapshot> EnvVarSnapshots { get; set; } = new List<EnvVarSnapshot>();

    /// <summary>
    /// ExecuteRequest-Output-Werte unter Alias (A4). Aussen: Alias-Name.
    /// Innen: Output-Name -> nativer Wert (EntityReference, EntityReferenceCollection,
    /// OptionSetValue, Money, Guid, primitive Typen). PlaceholderEngine
    /// loest {alias.outputs.X} und {alias.outputs.X[type=Y]} hierueber auf.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> OutputAliases { get; set; }
        = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sucht eine Record-ID über das Records-Registry.</summary>
    public Guid ResolveRecordId(string alias)
    {
        if (Records.TryGetValue(alias, out var record)) return record.Id;
        throw new InvalidOperationException(
            $"Record '{alias}' nicht im Kontext gefunden. " +
            $"Verfügbar: [{string.Join(", ", Records.Keys)}]");
    }

    /// <summary>Gibt die Entity-Bezeichnung für einen Alias zurück.</summary>
    public string ResolveRecordEntityName(string alias)
    {
        if (Records.TryGetValue(alias, out var record)) return record.EntityName;
        throw new InvalidOperationException($"Entity-Name für Record '{alias}' nicht gefunden.");
    }

    /// <summary>Registriert einen Record im Registry und in CreatedEntities für Cleanup.</summary>
    public void RegisterRecord(string alias, string entityName, Guid id)
    {
        Records[alias] = (entityName, id);
        CreatedEntities.Add((entityName, id));
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  Testergebnisse
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Gesamtergebnis eines Testlaufs.</summary>
public sealed class TestRunResult
{
    [JsonProperty("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonProperty("completedAt")]
    public DateTime CompletedAt { get; set; }

    [JsonProperty("totalCount")]
    public int TotalCount { get; set; }

    [JsonProperty("passedCount")]
    public int PassedCount { get; set; }

    [JsonProperty("failedCount")]
    public int FailedCount { get; set; }

    [JsonProperty("errorCount")]
    public int ErrorCount { get; set; }

    [JsonProperty("results")]
    public List<TestCaseResult> Results { get; set; } = new();

    [JsonProperty("fullLog")]
    public string FullLog { get; set; } = "";
}

/// <summary>
/// Ergebnis eines einzelnen Testfalls. Seit ADR-0004 gibt es keine separate
/// Assertions-Liste mehr; Assert-Steps sind in StepResults enthalten.
/// </summary>
public sealed class TestCaseResult
{
    [JsonProperty("testId")]
    public string TestId { get; set; } = "";

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    [JsonProperty("outcome")]
    public TestOutcome Outcome { get; set; }

    [JsonProperty("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    [JsonProperty("stepResults")]
    public List<StepResult> StepResults { get; set; } = new();

    /// <summary>
    /// B5 / ZastrPay-Feedback: alle vom Test angelegten Records (Alias plus
    /// Entity plus Id). Wird vor Cleanup aus ctx.CreatedEntities gefuellt
    /// und als JSON in jbe_testrunresult.jbe_trackedrecords persistiert.
    /// Gibt Test-Autoren bei keepRecords=true die Liste zum manuellen Cleanup
    /// und dokumentiert bei normalem Cleanup welche Records es gab.
    /// </summary>
    [JsonProperty("trackedRecords")]
    public List<TrackedRecord> TrackedRecords { get; set; } = new();
}

/// <summary>Pro TestCase getrackter Record fuer jbe_trackedrecords (B5).</summary>
public sealed class TrackedRecord
{
    [JsonProperty("entity")]
    public string Entity { get; set; } = "";

    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("alias")]
    public string? Alias { get; set; }
}

/// <summary>
/// Ergebnis eines einzelnen Testschritts. Universelle Struktur fuer alle
/// Action-Typen (CreateRecord/UpdateRecord/Assert/...); Persistenz-Ebene
/// schreibt pro StepResult einen jbe_teststep-Record.
/// </summary>
public sealed class StepResult
{
    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    /// <summary>Action-Typ aus dem TestStep (CreateRecord, Assert, ...).</summary>
    [JsonProperty("action")]
    public string Action { get; set; } = "";

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    // ── Kontext zur angefassten Entity (bei Create/Update/Delete/Retrieve/Assert-Record) ──

    [JsonProperty("entity")]
    public string? Entity { get; set; }

    [JsonProperty("alias")]
    public string? Alias { get; set; }

    [JsonProperty("recordId")]
    public Guid? RecordId { get; set; }

    // ── Assert-spezifisch ────────────────────────────────────

    /// <summary>Name des geprueften Feldes (nur bei Assert).</summary>
    [JsonProperty("assertField")]
    public string? AssertField { get; set; }

    /// <summary>Erwarteter Wert in Anzeigeform (nur bei Assert).</summary>
    [JsonProperty("expectedDisplay")]
    public string? ExpectedDisplay { get; set; }

    /// <summary>Tatsaechlicher Wert in Anzeigeform (nur bei Assert).</summary>
    [JsonProperty("actualDisplay")]
    public string? ActualDisplay { get; set; }

    // ── Debug / Transparenz ─────────────────────────────────

    /// <summary>Eingabe-Payload als JSON (optional, fuer Replay/Debug).</summary>
    [JsonProperty("inputData")]
    public string? InputData { get; set; }

    /// <summary>Ausgabe-Payload als JSON (optional, z.B. ExecuteRequest-Response).</summary>
    [JsonProperty("outputData")]
    public string? OutputData { get; set; }
}

/// <summary>
/// Internes Rueckgabe-Objekt des AssertionEngine. Wird vom TestRunner
/// nach einem Assert-Step in einen StepResult uebertragen.
/// </summary>
public sealed class AssertionResult
{
    public string Description { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public string? ExpectedDisplay { get; set; }
    public string? ActualDisplay { get; set; }
}

[JsonConverter(typeof(StringEnumConverter))]
public enum TestOutcome
{
    Passed,
    Failed,
    Error,
    Skipped
}
