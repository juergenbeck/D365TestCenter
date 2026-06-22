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
/// Ausführungsreihenfolge. Assert ist ein Step-Action-Typ wie jeder
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

    /// <summary>
    /// Captures unknown top-level JSON properties on deserialization (Newtonsoft
    /// [JsonExtensionData]). Used by the pack validator (R10): an obsolete
    /// pre-ADR-0004 top-level 'preconditions[]'/'assertions[]' array would otherwise
    /// be silently dropped (TestCase only has 'Steps'), and the validator, which runs
    /// on the deserialized object, could no longer see it.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JToken>? AdditionalData { get; set; }
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
/// inklusive Assert. Die Properties sind so gewählt, dass sie je nach
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
    /// Action-Typ. Kanonische Werte: CreateRecord, UpdateRecord, DeleteRecord,
    /// Wait, Delay, ExecuteRequest, RetrieveRecord, WaitForRecord, FindRecord,
    /// WaitForFieldValue, AssertEnvironment, Assert, SetEnvironmentVariable,
    /// RetrieveEnvironmentVariable, BrowserAction (ADR-0006).
    /// Legacy-Aliasse (ADR-0007): CallCustomApi und ExecuteAction werden auf
    /// ExecuteRequest gemappt. Aliasse bleiben für mind. zwei Plugin-Major-
    /// Versionen erhalten.
    /// </summary>
    [JsonProperty("action")]
    public string Action { get; set; } = "";

    // ── BrowserAction-spezifische Properties (ADR-0006) ────────────────────
    // Die folgenden Felder sind nur für Action="BrowserAction" relevant.
    // Im Plugin-Pfad (Sandbox) wird BrowserAction als Skipped markiert.

    /// <summary>
    /// BrowserAction-Sub-Operation: navigate, click, doubleClick, fill,
    /// selectOption, delay, screenshot, waitFor, evaluate.
    /// </summary>
    [JsonProperty("operation")]
    public string? Operation { get; set; }

    /// <summary>BrowserAction navigate: Ziel-URL.</summary>
    [JsonProperty("url")]
    public string? Url { get; set; }

    /// <summary>BrowserAction click/doubleClick/fill/waitFor/evaluate: CSS-Selektor.</summary>
    [JsonProperty("selector")]
    public string? Selector { get; set; }

    /// <summary>BrowserAction click/doubleClick: Fallback-Selektor wenn Primary nicht matcht.</summary>
    [JsonProperty("fallbackSelector")]
    public string? FallbackSelector { get; set; }

    // BrowserAction fill: nutzt das existing 'value'-Property (Assert-Wert),
    // semantisch identisch (erwarteter/einzugebender String-Wert).

    /// <summary>BrowserAction navigate/click/etc.: nach Aktion auf diesen Selektor warten.</summary>
    [JsonProperty("waitForSelector")]
    public string? WaitForSelector { get; set; }

    /// <summary>BrowserAction evaluate: JavaScript-Expression (z.B. "() => localStorage.getItem('roleType')").</summary>
    [JsonProperty("expression")]
    public string? Expression { get; set; }

    /// <summary>BrowserAction screenshot: Datei-Name (ohne Pfad/Endung).</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>BrowserAction navigate: assertNoLoginRedirect (Default true).</summary>
    [JsonProperty("assertNoLoginRedirect")]
    public bool? AssertNoLoginRedirect { get; set; }

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

    /// <summary>
    /// SDK-Message-Name für ExecuteRequest (z.B. "Merge", "SetState", "Assign",
    /// "QualifyLead", "markant_RunFieldGovernanceForContact").
    /// </summary>
    [JsonProperty("requestName")]
    public string? RequestName { get; set; }

    /// <summary>
    /// Legacy-Alias zu RequestName (ADR-0007). Wird in StepExecuteRequest
    /// via Fallback-Kette RequestName -> ActionName -> ApiName -> Entity
    /// gelesen. Bleibt für mind. zwei Plugin-Major-Versionen erhalten.
    /// </summary>
    [JsonProperty("actionName")]
    public string? ActionName { get; set; }

    /// <summary>
    /// Legacy-Alias zu RequestName, Schwester von ActionName (ADR-0007).
    /// SKILL.md d365-test-center 3.2 nutzte vor v5.3.7 'apiName',
    /// Handbuch 'actionName'. Beide werden als Alias akzeptiert.
    /// </summary>
    [JsonProperty("apiName")]
    public string? ApiName { get; set; }

    /// <summary>
    /// Legacy-Alias zu Fields (ADR-0007). Wenn gesetzt, werden die Werte
    /// hier statt aus Fields als SDK-Message-Parameter verwendet.
    /// </summary>
    [JsonProperty("parameters")]
    public Dictionary<string, object?>? Parameters { get; set; }

    /// <summary>
    /// ExecuteRequest-Output als Alias verfügbar machen (A4 / ZastrPay-Feedback).
    /// Wenn gesetzt, werden alle OrganizationResponse-Werte unter diesem Alias
    /// im ctx.OutputAliases-Dict abgelegt und sind via Platzhalter
    /// {alias.outputs.X} oder {alias.outputs.X[type=Y]} (für
    /// EntityReferenceCollection-Filter) referenzierbar.
    /// Beispiel: ExecuteRequest QualifyLead mit outputAlias='qres' macht
    /// {qres.outputs.CreatedEntityReferences[type=account]} verfügbar.
    /// </summary>
    [JsonProperty("outputAlias")]
    public string? OutputAlias { get; set; }

    // ── Assert-spezifische Properties ─────────────────────────

    /// <summary>
    /// Kontext-abhängig (Action bestimmt die Semantik):
    ///  - Assert: Record, Query, Contact, Account, ContactSource, MembershipSource, BridgeRecord, Logging.
    ///  - SetEnvironmentVariable: effective (Default), currentValue, defaultValue.
    /// </summary>
    [JsonProperty("target")]
    public string? Target { get; set; }

    /// <summary>Assert: zu prüfendes Feld auf dem Ziel-Record.</summary>
    [JsonProperty("field")]
    public string? Field { get; set; }

    /// <summary>Assert: Equals, NotEquals, IsNull, IsNotNull, Contains, Exists, NotExists, DateSetRecently, RecordCount.</summary>
    [JsonProperty("operator")]
    public string? Operator { get; set; }

    /// <summary>Assert: erwarteter Wert als String (Platzhalter erlaubt).</summary>
    [JsonProperty("value")]
    public string? Value { get; set; }

    /// <summary>
    /// Fehlerverhalten für diesen Step. "continue" = Exception wird nur
    /// geloggt, Test läuft weiter. "stop" = Test wird abgebrochen (Outcome=Error).
    /// Default ist action-abhängig: "continue" für Assert, "stop" für alle
    /// anderen Actions.
    /// </summary>
    [JsonProperty("onError")]
    public string? OnError { get; set; }

    /// <summary>
    /// Optionale Lauf-Bedingung (ADR-0011). Ist sie zur Laufzeit nicht erfüllt,
    /// wird der Step übersprungen (StepResult.Skipped, kein Failure); sonst läuft
    /// er normal (inkl. onError/expectFailure). Orthogonal zu AssertEnvironment
    /// (das lässt SCHEITERN; condition überspringt). Zwei Steps mit gegensätzlicher
    /// condition bilden ein if/else.
    /// </summary>
    [JsonProperty("condition")]
    public StepCondition? Condition { get; set; }

    // ── EnvironmentVariable-Actions (SetEnvironmentVariable, RetrieveEnvironmentVariable) ──

    /// <summary>schemaname der environmentvariabledefinition (env-unabhängig).</summary>
    [JsonProperty("schemaName")]
    public string? SchemaName { get; set; }

    /// <summary>
    /// RetrieveEnvironmentVariable: "effective" (Default), "currentValue", "defaultValue".
    /// Analog zum Retrieve-Pfad: Plugins lesen effective.
    /// </summary>
    [JsonProperty("source")]
    public string? Source { get; set; }

    // ── Query-Sortierung für FindRecord/WaitForRecord (1c Option A) ──

    /// <summary>
    /// Sortierung für WaitForRecord/FindRecord. Komma-separierter String im
    /// OData-Stil: "feldname asc|desc, feldname2 asc|desc". Default ist asc
    /// wenn nur Feldname ohne Richtung angegeben.
    /// Beispiel: "modifiedon asc, createdon desc"
    /// </summary>
    [JsonProperty("orderBy")]
    public string? OrderBy { get; set; }

    /// <summary>
    /// Maximalzahl Treffer für WaitForRecord/FindRecord. Default ist 1 (nimmt
    /// den ersten Treffer gemäß Sortierung). Stretch-Feature für top > 1
    /// (mehrere Alias-Records) ist aktuell nicht unterstützt.
    /// </summary>
    [JsonProperty("top")]
    public int? Top { get; set; }

    // ── Negative-Path-Tests (expectFailure / expectException) ──

    /// <summary>
    /// Wenn true: dieser Step darf (muss) eine Exception werfen um als
    /// Passed zu gelten. Ohne Exception wird der Step als Failed markiert
    /// mit Message "Expected exception but action succeeded".
    /// Für Assert-Actions ignoriert (Assert-Failure ist bereits durch
    /// NotEquals/IsNull/NotExists etc. abgedeckt).
    /// </summary>
    [JsonProperty("expectFailure")]
    public bool? ExpectFailure { get; set; }

    /// <summary>
    /// Erweiterte Variante: Spec zum Matchen der erwarteten Exception
    /// (messageContains, messageMatches, errorCode, httpStatus).
    /// expectException impliziert expectFailure=true. Alle gesetzten Felder
    /// werden mit AND verknüpft.
    /// </summary>
    [JsonProperty("expectException")]
    public ExpectExceptionSpec? ExpectException { get; set; }

    /// <summary>
    /// Captures unknown step-level JSON properties on deserialization (Newtonsoft
    /// [JsonExtensionData]). Used by the pack validator (STEP_KEY_UNKNOWN, Backlog N):
    /// an unknown key such as 'withinSeconds'/'timeoutMs' is otherwise silently dropped
    /// (MissingMemberHandling default Ignore), so the validator, which runs on the
    /// deserialized object, could not warn about the typo (FB-45). Same mechanism as
    /// TestCase.AdditionalData for R10, one level deeper.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JToken>? AdditionalData { get; set; }
}

/// <summary>
/// Match-Spezifikation für erwartete Exceptions (expectException).
/// Mehrere gesetzte Felder werden mit AND verknüpft.
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
/// Eine einzelne Vergleichsklausel einer Step-Condition (ADR-0011):
/// left &lt;operator&gt; right. left/right laufen durch die PlaceholderEngine;
/// operator ist einer aus <see cref="ValueComparator.SupportedOperators"/>.
/// Basis für die Einfachklausel (<see cref="StepCondition"/>) und die
/// Elemente von all/any.
/// </summary>
public class StepConditionClause
{
    /// <summary>Linker Vergleichswert (Platzhalter erlaubt, z.B. "{wbcfg.fields.markant_writebacktocontact}").</summary>
    [JsonProperty("left")]
    public string? Left { get; set; }

    /// <summary>Vergleichsoperator aus <see cref="ValueComparator.SupportedOperators"/> (case-insensitiv).</summary>
    [JsonProperty("operator")]
    public string? Operator { get; set; }

    /// <summary>Rechter Vergleichswert (Platzhalter erlaubt; bei IsNull/IsNotNull ungenutzt).</summary>
    [JsonProperty("right")]
    public string? Right { get; set; }
}

/// <summary>
/// Lauf-Bedingung eines Steps (ADR-0011). Genau EINE Form:
///  - Einfachklausel: geerbtes Left/Operator/Right.
///  - <see cref="All"/>: AND über mehrere Klauseln (alle müssen erfüllt sein).
///  - <see cref="Any"/>: OR über mehrere Klauseln (mindestens eine erfüllt).
/// Der PackValidator (CONDITION_MALFORMED) erzwingt die Wohlgeformtheit;
/// zur Laufzeit gewinnt All vor Any vor Einfachklausel.
/// </summary>
public sealed class StepCondition : StepConditionClause
{
    /// <summary>AND-Verknüpfung: alle Klauseln müssen erfüllt sein.</summary>
    [JsonProperty("all")]
    public List<StepConditionClause>? All { get; set; }

    /// <summary>OR-Verknüpfung: mindestens eine Klausel muss erfüllt sein.</summary>
    [JsonProperty("any")]
    public List<StepConditionClause>? Any { get; set; }
}

/// <summary>
/// Snapshot eines EnvironmentVariable-Zustands für Auto-Restore im Cleanup.
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
/// Interne Datenstruktur für AssertionEngine. Ein Assert-Step wird zur
/// Ausführung in ein TestAssertion-Objekt übersetzt, dann evaluiert, und
/// das Ergebnis wieder in den StepResult zurückgemappt.
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

    /// <summary>Snapshots für EnvironmentVariable-Auto-Restore im Cleanup.</summary>
    public List<EnvVarSnapshot> EnvVarSnapshots { get; set; } = new List<EnvVarSnapshot>();

    /// <summary>
    /// ExecuteRequest-Output-Werte unter Alias (A4). Außen: Alias-Name.
    /// Innen: Output-Name -> nativer Wert (EntityReference, EntityReferenceCollection,
    /// OptionSetValue, Money, Guid, primitive Typen). PlaceholderEngine
    /// löst {alias.outputs.X} und {alias.outputs.X[type=Y]} hierüber auf.
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

    /// <summary>
    /// Registriert einen Record im Registry und (bei trackForCleanup=true) in CreatedEntities
    /// für den Cleanup. Angelegte Records (CreateRecord) tracken; per FindRecord/WaitForRecord
    /// nur GEFUNDENE Bestands-Records dürfen NICHT getrackt werden (trackForCleanup=false),
    /// sonst löscht der Cleanup geteilte Stammdaten (z.B. den per FindRecord gelesenen
    /// markant_fg_fieldconfig) auf der Ziel-Umgebung. Ein gefundener Record ist kein erzeugter.
    /// </summary>
    public void RegisterRecord(string alias, string entityName, Guid id, bool trackForCleanup = true)
    {
        Records[alias] = (entityName, id);
        if (trackForCleanup)
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

    /// <summary>
    /// Anzahl übersprungener Tests (deaktiviert, dependsOn nicht erfüllt, oder
    /// ADR-0011: alle Asserts condition-geskippt). Macht die Summary ehrlich:
    /// Passed + Failed + Error + Skipped = Total.
    /// </summary>
    [JsonProperty("skippedCount")]
    public int SkippedCount { get; set; }

    [JsonProperty("results")]
    public List<TestCaseResult> Results { get; set; } = new();

    [JsonProperty("fullLog")]
    public string FullLog { get; set; } = "";
}

/// <summary>
/// Ergebnis eines zeitbudgetierten Gruppen-Laufs (ADR-0009 Phase 1, Befund 3,
/// Gruppen-Grenzen-Continuation). <see cref="TestRunner.RunGroupsBudgeted"/> verarbeitet
/// Abhängigkeits-Gruppen ab <c>startGroupIndex</c> bis das Zeitbudget reißt, immer
/// mindestens eine Gruppe pro Aufruf. <see cref="NextGroupIndex"/> ist der Cursor für
/// die nächste Continuation-Welle; <see cref="Done"/> ist true, wenn alle Gruppen
/// abgearbeitet sind. <see cref="Run"/> trägt die Ergebnisse dieser Welle.
/// </summary>
public sealed class BudgetedRunResult
{
    /// <summary>Index der nächsten un-gelaufenen Gruppe (Continuation-Cursor). == Gruppen-Anzahl bei Done.</summary>
    public int NextGroupIndex { get; set; }

    /// <summary>True, wenn alle Gruppen abgearbeitet sind (kein weiterer Self-Trigger nötig).</summary>
    public bool Done { get; set; }

    /// <summary>Ergebnisse + Zähler + Log der in dieser Welle gelaufenen Gruppen.</summary>
    public TestRunResult Run { get; set; } = new TestRunResult();
}

/// <summary>
/// Run-Aggregat, einmal am Plateau aus den Result-Records gerechnet (ADR-0009 B.5,
/// Plan Phase 3). Outcome-Split + Dauer-Verteilung + angelegte Records. Die Verteilung
/// (avg/median/min/max/slowest) wird über die AUSGEFÜHRTEN Tests gerechnet
/// (Outcome != Skipped); Skipped-Tests (Dauer 0) verzerren sie sonst. Median-Konvention
/// wie die Cold-Start-Heuristik (<c>sorted[count/2]</c>, obere Mitte bei gerader Anzahl).
/// Wall-Clock (<c>jbe_durationms</c>) und Chunk-Zähler kommen NICHT von hier
/// (Timestamps bzw. Chunk-Records) -- das rechnet der Worker inline.
/// </summary>
public sealed class RunAggregate
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Errored { get; set; }
    public int Skipped { get; set; }

    /// <summary>Summe der Per-Test-Dauern der ausgeführten Tests (Rechenzeit).</summary>
    public long TotalTestMs { get; set; }
    public int AvgTestMs { get; set; }
    public int MedianTestMs { get; set; }
    public int MinTestMs { get; set; }
    public int MaxTestMs { get; set; }

    /// <summary>Test-ID mit der höchsten Dauer (Triage/Hotspot). Null wenn nichts ausgeführt.</summary>
    public string? SlowestTestId { get; set; }

    /// <summary>Summe der getrackten angelegten Records über alle Ergebnisse.</summary>
    public int RecordsCreated { get; set; }
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
    /// Entity plus Id). Wird vor Cleanup aus ctx.CreatedEntities gefüllt
    /// und als JSON in jbe_testrunresult.jbe_trackedrecords persistiert.
    /// Gibt Test-Autoren bei keepRecords=true die Liste zum manuellen Cleanup
    /// und dokumentiert bei normalem Cleanup welche Records es gab.
    /// </summary>
    [JsonProperty("trackedRecords")]
    public List<TrackedRecord> TrackedRecords { get; set; } = new();
}

/// <summary>Pro TestCase getrackter Record für jbe_trackedrecords (B5).</summary>
public sealed class TrackedRecord
{
    [JsonProperty("entity")]
    public string Entity { get; set; } = "";

    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("alias")]
    public string? Alias { get; set; }

    /// <summary>Primary-Name des Records (z.B. account.name, contact.fullname,
    /// lead.subject). Nur im CLI-run-Pfad erfasst (OE-10, CaptureRecordNames);
    /// null in den Plugin-Pfaden und wenn die Metadaten nicht ladbar waren.</summary>
    [JsonProperty("name")]
    public string? Name { get; set; }
}

/// <summary>
/// Ergebnis eines einzelnen Testschritts. Universelle Struktur für alle
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

    /// <summary>
    /// ADR-0011: Step wurde wegen einer nicht erfüllten <see cref="TestStep.Condition"/>
    /// übersprungen (nicht ausgeführt). Ein Skipped-Step ist weder Passed noch Failed;
    /// die Outcome-Logik zählt ihn nicht als Failure, und die Persistenz schreibt
    /// jbe_stepstatus=Skipped (statt Passed/Failed). Default false.
    /// </summary>
    [JsonProperty("skipped")]
    public bool Skipped { get; set; }

    /// <summary>ADR-0011: Grund des Skips (welche Condition nicht erfüllt war). Nur bei Skipped gesetzt.</summary>
    [JsonProperty("skipReason")]
    public string? SkipReason { get; set; }

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

    /// <summary>Name des geprüften Feldes (nur bei Assert).</summary>
    [JsonProperty("assertField")]
    public string? AssertField { get; set; }

    /// <summary>Erwarteter Wert in Anzeigeform (nur bei Assert).</summary>
    [JsonProperty("expectedDisplay")]
    public string? ExpectedDisplay { get; set; }

    /// <summary>Tatsächlicher Wert in Anzeigeform (nur bei Assert).</summary>
    [JsonProperty("actualDisplay")]
    public string? ActualDisplay { get; set; }

    // ── Debug / Transparenz ─────────────────────────────────

    /// <summary>Eingabe-Payload als JSON (optional, für Replay/Debug).</summary>
    [JsonProperty("inputData")]
    public string? InputData { get; set; }

    /// <summary>Ausgabe-Payload als JSON (optional, z.B. ExecuteRequest-Response).</summary>
    [JsonProperty("outputData")]
    public string? OutputData { get; set; }

    /// <summary>
    /// ADR-0006 Phase 1d: Diagnostic artefacts from a failed BrowserAction step
    /// (PNG screenshot, Playwright trace.zip, context). Set by TestRunner from
    /// IBrowserActionExecutor.LastDiagnostics. Null for non-UI steps and for
    /// successful UI steps. Picked up by TestCenterOrchestrator for upload to
    /// jbe_testrunresult.jbe_screenshot / jbe_uitrace File-fields.
    /// Excluded from JSON serialisation (binary blobs do not belong in test logs).
    /// </summary>
    [JsonIgnore]
    public StepDiagnostics? Diagnostics { get; set; }
}

/// <summary>
/// Internes Rückgabe-Objekt des AssertionEngine. Wird vom TestRunner
/// nach einem Assert-Step in einen StepResult übertragen.
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
