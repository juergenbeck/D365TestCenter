using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Microsoft.Xrm.Sdk;

namespace D365TestCenter.Core;

// ═══════════════════════════════════════════════════════════════════════
//  JSON-Testfall-Definitionen (deserialisiert aus Testsuite-JSON)
//  Voll kompatibel mit dem FGTestTool-Format (vs_FG_TestTool)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Root-Objekt einer JSON-Testsuite-Datei.</summary>
public sealed class TestSuiteDefinition
{
    [JsonProperty("suiteId")]
    public string SuiteId { get; set; } = "";

    [JsonProperty("suiteName")]
    public string SuiteName { get; set; } = "";

    // FGTestTool-Kompatibilität
    [JsonProperty("suiteDescription")]
    public string? SuiteDescription { get; set; }

    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("jiraReference")]
    public string? JiraReference { get; set; }

    [JsonProperty("testCases")]
    public List<TestCase> TestCases { get; set; } = new();
}

/// <summary>Einzelner Testfall innerhalb einer Suite.</summary>
public sealed class TestCase
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("title")]
    public string Title { get; set; } = "";

    // FGTestTool-Kompatibilität: optionale Beschreibung
    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    // FGTestTool-Kompatibilität: notImplemented-Flag
    [JsonProperty("notImplemented")]
    public bool NotImplemented { get; set; }

    /// <summary>"bogus" für zufällige Daten oder "template" für Platzhalter-basierte Daten.</summary>
    [JsonProperty("dataMode")]
    public string DataMode { get; set; } = "template";

    [JsonProperty("cleanupAfterTest")]
    public bool CleanupAfterTest { get; set; } = true;

    // FGTestTool-Kompatibilität: optionaler Async-Wait-Override
    [JsonProperty("asyncWaitOverrideSeconds")]
    public int? AsyncWaitOverrideSeconds { get; set; }

    [JsonProperty("preconditions")]
    [JsonConverter(typeof(PreconditionsConverter))]
    public TestPreconditions Preconditions { get; set; } = new();

    [JsonProperty("steps")]
    public List<TestStep> Steps { get; set; } = new();

    [JsonProperty("assertions")]
    public List<TestAssertion> Assertions { get; set; } = new();

    // ── Datengetriebene Tests ─────────────────────────────────

    /// <summary>
    /// Optionale Datenzeilen für parameterisierte Tests.
    /// Pro Zeile wird der Testfall einmal ausgeführt mit {ROW:fieldname}-Platzhaltern.
    /// </summary>
    [JsonProperty("dataRows")]
    public List<Dictionary<string, object?>>? DataRows { get; set; }

    // ── Testfall-Abhängigkeiten ───────────────────────────────

    /// <summary>IDs von Testfällen, die vorher bestanden haben müssen.</summary>
    [JsonProperty("dependsOn")]
    public List<string>? DependsOn { get; set; }

    /// <summary>Geteilter Kontext-Name für Testfälle, die zusammengehören.</summary>
    [JsonProperty("sharedContext")]
    public string? SharedContext { get; set; }
}

/// <summary>Generische Precondition im Array-Format: [{entity, alias, fields}].</summary>
public sealed class GenericPrecondition
{
    [JsonProperty("entity")]
    public string Entity { get; set; } = "";

    [JsonProperty("alias")]
    public string? Alias { get; set; }

    [JsonProperty("fields")]
    public Dictionary<string, object?> Fields { get; set; } = new();

    [JsonProperty("waitForAsync")]
    public bool WaitForAsync { get; set; }
}

/// <summary>
/// JsonConverter der sowohl Objekt-Format (FGTestTool) als auch Array-Format (generisch) akzeptiert.
/// </summary>
public sealed class PreconditionsConverter : JsonConverter<TestPreconditions>
{
    public override TestPreconditions? ReadJson(JsonReader reader, Type objectType,
        TestPreconditions? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);

        if (token.Type == JTokenType.Object)
        {
            // FGTestTool-Format: { createContact: true, contactSources: [...] }
            var result = new TestPreconditions();
            serializer.Populate(token.CreateReader(), result);
            return result;
        }

        if (token.Type == JTokenType.Array)
        {
            // Generisches Format: [ { entity, alias, fields }, ... ]
            var result = new TestPreconditions
            {
                CreateAccount = false,
                CreateContact = false
            };
            result.GenericRecords = token.ToObject<List<GenericPrecondition>>(serializer)
                ?? new List<GenericPrecondition>();
            return result;
        }

        return new TestPreconditions();
    }

    public override void WriteJson(JsonWriter writer, TestPreconditions? value, JsonSerializer serializer)
    {
        if (value != null && value.GenericRecords.Count > 0)
        {
            // Array-Format: nur GenericRecords schreiben
            serializer.Serialize(writer, value.GenericRecords);
        }
        else
        {
            // Objekt-Format: manuell serialisieren um Endlosschleife zu vermeiden
            var obj = new JObject();
            if (value != null)
            {
                obj["createContact"] = value.CreateContact;
                obj["createAccount"] = value.CreateAccount;
                if (value.AccountData.Count > 0)
                    obj["accountData"] = JToken.FromObject(value.AccountData, serializer);
                if (value.ContactData.Count > 0)
                    obj["contactData"] = JToken.FromObject(value.ContactData, serializer);
                if (value.ContactInitialState.Count > 0)
                    obj["contactInitialState"] = JToken.FromObject(value.ContactInitialState, serializer);
                if (value.ExistingContactSources.Count > 0)
                    obj["existingContactSources"] = JToken.FromObject(value.ExistingContactSources, serializer);
            }
            obj.WriteTo(writer);
        }
    }
}

/// <summary>Vorbedingungen für einen Testfall (Account, Contact, Sources).</summary>
public sealed class TestPreconditions
{
    // FGTestTool-Kompatibilität: explizite Flags für Contact-/Account-Erstellung
    [JsonProperty("createContact")]
    public bool CreateContact { get; set; } = true;

    [JsonProperty("createAccount")]
    public bool CreateAccount { get; set; } = true;

    [JsonProperty("accountData")]
    public Dictionary<string, object?> AccountData { get; set; } = new();

    [JsonProperty("contactData")]
    public Dictionary<string, object?> ContactData { get; set; } = new();

    /// <summary>Kontakt-Feldwerte, die NACH dem Anlegen aller Sources gesetzt werden.</summary>
    [JsonProperty("contactInitialState")]
    public Dictionary<string, object?> ContactInitialState { get; set; } = new();

    /// <summary>
    /// ContactSource-Records die als Vorbedingung angelegt werden.
    /// Akzeptiert sowohl "existingContactSources" (FGTestTool) als auch "contactSources" (neu).
    /// </summary>
    private List<ContactSourceSetup> _contactSources = new();

    [JsonProperty("existingContactSources")]
    public List<ContactSourceSetup> ExistingContactSources
    {
        get => _contactSources;
        set => _contactSources = value ?? new();
    }

    [JsonProperty("contactSources")]
    public List<ContactSourceSetup> ContactSources
    {
        get => _contactSources;
        set { if (value != null && value.Count > 0) _contactSources = value; }
    }

    /// <summary>Generische Precondition-Records im Array-Format (LM/Standard-Tests).</summary>
    [JsonIgnore]
    public List<GenericPrecondition> GenericRecords { get; set; } = new();
}

/// <summary>
/// Setup-Definition für einen ContactSource-Record in den Vorbedingungen.
/// Unterstützt sowohl String-basierte ("Pisa") als auch int-basierte (4) Quellsystem-Angaben.
/// </summary>
public sealed class ContactSourceSetup
{
    // FGTestTool-Kompatibilität: Alias-basierte Referenzierung
    [JsonProperty("alias")]
    public string Alias { get; set; } = "";

    /// <summary>
    /// Quellsystem: akzeptiert String ("Pisa", "Dynamics", "Platform") oder int (4, 1, 5).
    /// </summary>
    [JsonProperty("sourceSystem")]
    public object? RawSourceSystem { get; set; }

    /// <summary>Aufgelöster OptionSet-Wert des Quellsystems.</summary>
    [JsonIgnore]
    public int SourceSystemCode => SourceSystemHelper.Resolve(RawSourceSystem);

    [JsonProperty("externalId")]
    public string? ExternalId { get; set; }

    [JsonProperty("fields")]
    public Dictionary<string, object?> Fields { get; set; } = new();

    [JsonProperty("linkToContact")]
    public bool LinkToContact { get; set; } = true;

    [JsonProperty("waitForGovernance")]
    public bool WaitForGovernance { get; set; } = true;

    [JsonProperty("asyncWaitOverrideSeconds")]
    public int? AsyncWaitOverrideSeconds { get; set; }
}

/// <summary>Einzelner Testschritt (Aktion + optionales Warten auf Governance).</summary>
public sealed class TestStep
{
    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// CreateContactSource, UpdateContactSource, UpdateContact,
    /// CreatePlatformBridgeRecord, CallGovernanceApiContactSource,
    /// CallGovernanceApiContact, CallGovernanceApi, Wait, Delay.
    /// </summary>
    [JsonProperty("action")]
    public string Action { get; set; } = "";

    // FGTestTool-Kompatibilität: Alias-basierte Referenzierung
    [JsonProperty("targetAlias")]
    public string? TargetAlias { get; set; }

    // ── NEU: Generische Action-Properties ─────────────────────

    /// <summary>Ziel-Entity für CreateRecord/UpdateRecord/WaitForRecord.</summary>
    [JsonProperty("entity")]
    public string? Entity { get; set; }

    /// <summary>Alias unter dem der erstellte/gefundene Record im Context registriert wird.</summary>
    [JsonProperty("alias")]
    public string? Alias { get; set; }

    /// <summary>Referenz auf einen bestehenden Record per Alias: "{RECORD:alias}" oder direkt ein Alias-Name.</summary>
    [JsonProperty("recordRef")]
    public string? RecordRef { get; set; }

    /// <summary>Filterkriterien für WaitForRecord.</summary>
    [JsonProperty("filter")]
    [JsonConverter(typeof(FilterListConverter))]
    public List<FilterCondition>? Filter { get; set; }

    /// <summary>Erwarteter Feldwert für WaitForFieldValue.</summary>
    [JsonProperty("expectedValue")]
    public object? StepExpectedValue { get; set; }

    /// <summary>Welche Spalten beim WaitForRecord geladen werden sollen.</summary>
    [JsonProperty("columns")]
    public List<string>? Columns { get; set; }

    /// <summary>Polling-Intervall in Millisekunden (Standard: 2000).</summary>
    [JsonProperty("pollingIntervalMs")]
    public int PollingIntervalMs { get; set; } = 2000;

    /// <summary>Maximale Dauer in ms, ab der eine Performance-Assertion fehlschlägt.</summary>
    [JsonProperty("maxDurationMs")]
    public int? MaxDurationMs { get; set; }

    // ── Bestehend ─────────────────────────────────────────────

    /// <summary>Quellsystem: akzeptiert String oder int.</summary>
    [JsonProperty("sourceSystem")]
    public object? RawSourceSystem { get; set; }

    [JsonIgnore]
    public int? SourceSystemCode => RawSourceSystem != null ? SourceSystemHelper.Resolve(RawSourceSystem) : null;

    [JsonProperty("externalId")]
    public string? ExternalId { get; set; }

    // FGTestTool-Kompatibilität: "fields" ist der primäre Name (wie im FGTestTool)
    // "data" wird als Alias akzeptiert
    private Dictionary<string, object?> _fields = new();

    [JsonProperty("fields")]
    public Dictionary<string, object?> Fields
    {
        get => _fields;
        set => _fields = value ?? new();
    }

    [JsonProperty("data")]
    public Dictionary<string, object?> Data
    {
        get => _fields;
        set { if (value != null && value.Count > 0) _fields = value; }
    }

    // FGTestTool-Kompatibilität: linkToContact für CreateContactSource
    [JsonProperty("linkToContact")]
    public bool LinkToContact { get; set; } = true;

    [JsonProperty("waitForGovernance")]
    public bool WaitForGovernance { get; set; }

    [JsonProperty("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 120;

    // FGTestTool-Kompatibilität: waitSeconds für Wait-Actions
    [JsonProperty("waitSeconds")]
    public int? WaitSeconds { get; set; }

    [JsonProperty("delayMs")]
    public int? DelayMs { get; set; }
}

/// <summary>
/// JsonConverter der sowohl Objekt-Format {"field":"value"} als auch Array-Format [{field,operator,value}] akzeptiert.
/// </summary>
public sealed class FilterListConverter : JsonConverter<List<FilterCondition>>
{
    public override List<FilterCondition>? ReadJson(JsonReader reader, Type objectType,
        List<FilterCondition>? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);

        if (token.Type == JTokenType.Array)
        {
            // Strukturiertes Format: [{field, operator, value}]
            return token.ToObject<List<FilterCondition>>(serializer)
                ?? new List<FilterCondition>();
        }

        if (token.Type == JTokenType.Object)
        {
            // Dictionary-Format: {"field1": "value1", "field2": "value2"}
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

    /// <summary>eq, ne, gt, lt, ge, le, contains, like, null, notnull, beginswith, endswith, in, notin.</summary>
    [JsonProperty("operator")]
    public string Operator { get; set; } = "eq";

    [JsonProperty("value")]
    public object? Value { get; set; }
}

/// <summary>Einzelne Assertion zur Prüfung nach der Testausführung.</summary>
public sealed class TestAssertion
{
    /// <summary>
    /// Ziel-Entity: "Contact", "ContactSource", "ContactSource:{alias}", "CdhLogging", "PlatformBridge",
    /// "Record" (generisch per recordRef), "Query" (generisch per entity+filter).
    /// </summary>
    [JsonProperty("target")]
    public string Target { get; set; } = "Contact";

    [JsonProperty("field")]
    public string Field { get; set; } = "";

    // ── NEU: Generische Assertion-Properties ──────────────────

    /// <summary>Ziel-Entity für target="Record" oder target="Query".</summary>
    [JsonProperty("entity")]
    public string? Entity { get; set; }

    /// <summary>Record-Referenz per Alias für target="Record".</summary>
    [JsonProperty("recordRef")]
    public string? RecordRef { get; set; }

    /// <summary>Filterkriterien für target="Query".</summary>
    [JsonProperty("filter")]
    [JsonConverter(typeof(FilterListConverter))]
    public List<FilterCondition>? AssertionFilter { get; set; }

    // ── Bestehend ─────────────────────────────────────────────

    /// <summary>Equals, NotEquals, IsNull, IsNotNull, Contains, DateSetRecently, Unchanged, Changed, Exists, ContactUpdated.</summary>
    [JsonProperty("operator")]
    public string Operator { get; set; } = "Equals";

    // FGTestTool-Kompatibilität: akzeptiert sowohl "value" als auch "expectedValue"
    private string? _value;

    [JsonProperty("value")]
    public string? Value
    {
        get => _value;
        set => _value = value;
    }

    [JsonProperty("expectedValue")]
    public object? ExpectedValue
    {
        get => _value;
        set { if (value != null) _value = value.ToString(); }
    }

    [JsonProperty("description")]
    public string? Description { get; set; }

    /// <summary>Für ContactSource-Assertions: welches Quellsystem (OptionSet-Wert).</summary>
    [JsonProperty("sourceSystem")]
    public int? SourceSystem { get; set; }

    /// <summary>Für DateSetRecently: maximale Sekunden seit dem Setzen.</summary>
    [JsonProperty("withinSeconds")]
    public int? WithinSeconds { get; set; }

    /// <summary>Für CdhLogging-Assertions: "Name" oder "Diagnostics".</summary>
    [JsonProperty("logField")]
    public string? LogField { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
//  Laufzeit-Kontext (pro Testfall, nicht JSON-serialisiert)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Hält den Zustand während der Ausführung eines einzelnen Testfalls.</summary>
public sealed class TestContext
{
    public DateTime TestStartUtc { get; set; }
    public string TestId { get; set; } = "";
    public Guid? AccountId { get; set; }
    public Guid? ContactId { get; set; }

    // ── Bestehend (Rückwärtskompatibilität) ──────────────────────

    /// <summary>Alias -> ContactSource-Id.</summary>
    public Dictionary<string, Guid> ContactSourceIds { get; set; } = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alias -> MembershipSource-Id.</summary>
    public Dictionary<string, Guid> MembershipSourceIds { get; set; } = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

    public List<Guid> BridgeRecordIds { get; set; } = new List<Guid>();

    /// <summary>CDH-Log-Entities, geladen nach den Teststeps.</summary>
    public List<Entity> CdhLogs { get; set; } = new List<Entity>();

    /// <summary>Alle erstellten Entity-IDs für Cleanup nach dem Test.</summary>
    public List<(string EntityName, Guid Id)> CreatedEntities { get; set; } = new List<(string, Guid)>();

    /// <summary>Snapshot des Kontakts vor den Teststeps (für Unchanged-Assertions).</summary>
    public Entity? CurrentContact { get; set; }

    // ── NEU: Generisches Record-Registry ─────────────────────────

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

    // ── Lookup-Methoden ──────────────────────────────────────────

    /// <summary>Sucht eine ContactSource-ID: erst nach Alias, dann nach SourceSystem-Code.</summary>
    public Guid ResolveContactSourceId(string? alias, int? sourceSystem)
    {
        if (!string.IsNullOrEmpty(alias) && ContactSourceIds.TryGetValue(alias, out var byAlias))
            return byAlias;

        if (sourceSystem.HasValue)
        {
            var key = sourceSystem.Value.ToString();
            if (ContactSourceIds.TryGetValue(key, out var byCode))
                return byCode;
        }

        throw new InvalidOperationException(
            $"ContactSource nicht gefunden: alias='{alias}', sourceSystem={sourceSystem}. " +
            $"Verfügbar: {string.Join(", ", ContactSourceIds.Keys)}");
    }

    /// <summary>Sucht eine Record-ID über alle Registries: Records, ContactSourceIds, MembershipSourceIds.</summary>
    public Guid ResolveRecordId(string alias)
    {
        if (Records.TryGetValue(alias, out var record)) return record.Id;
        if (ContactSourceIds.TryGetValue(alias, out var csId)) return csId;
        if (MembershipSourceIds.TryGetValue(alias, out var msId)) return msId;
        throw new InvalidOperationException(
            $"Record '{alias}' nicht im Kontext gefunden. " +
            $"Verfügbar: Records=[{string.Join(", ", Records.Keys)}], " +
            $"CS=[{string.Join(", ", ContactSourceIds.Keys)}]");
    }

    /// <summary>Gibt die Entity-Bezeichnung für einen Alias zurück.</summary>
    public string ResolveRecordEntityName(string alias)
    {
        if (Records.TryGetValue(alias, out var record)) return record.EntityName;
        throw new InvalidOperationException($"Entity-Name für Record '{alias}' nicht gefunden.");
    }

    /// <summary>Registriert einen Record im generischen Registry UND in CreatedEntities für Cleanup.</summary>
    public void RegisterRecord(string alias, string entityName, Guid id)
    {
        Records[alias] = (entityName, id);
        CreatedEntities.Add((entityName, id));
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  Testergebnisse
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Gesamtergebnis eines Testlaufs (alle Testfälle einer Suite).</summary>
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

/// <summary>Ergebnis eines einzelnen Testfalls.</summary>
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

    [JsonProperty("assertions")]
    public List<AssertionResult> Assertions { get; set; } = new();

    [JsonProperty("stepResults")]
    public List<StepResult> StepResults { get; set; } = new();

    [JsonProperty("cdhLogEntries")]
    public List<CdhLogEntry> CdhLogEntries { get; set; } = new();
}

/// <summary>Ergebnis eines einzelnen Testschritts.</summary>
public sealed class StepResult
{
    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }
}

/// <summary>Ergebnis einer einzelnen Assertion.</summary>
public sealed class AssertionResult
{
    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("passed")]
    public bool Passed { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = "";

    [JsonProperty("expectedDisplay")]
    public string? ExpectedDisplay { get; set; }

    [JsonProperty("actualDisplay")]
    public string? ActualDisplay { get; set; }
}

[JsonConverter(typeof(StringEnumConverter))]
public enum TestOutcome
{
    Passed,
    Failed,
    Error,
    Skipped,
    NotImplemented
}

// ═══════════════════════════════════════════════════════════════════════
//  FG-Logging-Eintrag (geparst aus markant_fg_logging)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>Strukturierte Darstellung eines CDH-Log-Eintrags.</summary>
public sealed class CdhLogEntry
{
    // Tatsächlicher Dataverse-Spaltenname (Tippfehler im Schema, aber so heißt das Feld)
    internal const string DiagnosticsFieldName = "markant_diagonstics_text";

    [JsonProperty("logId")]
    public Guid LogId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("diagnosticsText")]
    public string? DiagnosticsText { get; set; }

    [JsonProperty("contactId")]
    public Guid? ContactId { get; set; }

    [JsonProperty("contactSourceId")]
    public Guid? ContactSourceId { get; set; }

    [JsonProperty("createdOn")]
    public DateTime CreatedOn { get; set; }

    [JsonProperty("correlationId")]
    public string? CorrelationId { get; set; }

    [JsonProperty("contactUpdated")]
    public bool? ContactUpdated { get; set; }

    [JsonProperty("pisaTriggered")]
    public bool? PisaTriggered { get; set; }

    [JsonProperty("updatedFields")]
    public List<string> UpdatedFields { get; set; } = new();

    [JsonProperty("errors")]
    public List<string> Errors { get; set; } = new();

    [JsonProperty("decisions")]
    public List<string> Decisions { get; set; } = new();

    /// <summary>Parst DiagnosticsText in die strukturierten Felder.</summary>
    public void ParseDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(DiagnosticsText)) return;

        bool inDecisions = false, inErrors = false;

        foreach (var rawLine in DiagnosticsText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("CorrelationId="))
                CorrelationId = line.Substring("CorrelationId=".Length).Trim();
            else if (line.StartsWith("ContactUpdated="))
                ContactUpdated = line.Contains("True", StringComparison.OrdinalIgnoreCase);
            else if (line.StartsWith("PisaTriggered="))
                PisaTriggered = line.Contains("True", StringComparison.OrdinalIgnoreCase);
            else if (line == "Errors:")
            { inErrors = true; inDecisions = false; }
            else if (line == "Decisions:")
            { inDecisions = true; inErrors = false; }
            else if (inErrors && line.StartsWith("- "))
                Errors.Add(line.Substring(2));
            else if (inDecisions && line.StartsWith("Field="))
            {
                Decisions.Add(line);
                if (line.Contains("Updated=True"))
                {
                    var fieldPart = line.Split('|').FirstOrDefault()?.Trim();
                    if (fieldPart != null && fieldPart.StartsWith("Field="))
                        UpdatedFields.Add(fieldPart.Substring("Field=".Length).Trim());
                }
            }
        }
    }

    /// <summary>Erstellt einen CdhLogEntry aus einer Dataverse-Entity.</summary>
    public static CdhLogEntry FromEntity(Entity entity)
    {
        var entry = new CdhLogEntry
        {
            LogId = entity.Id,
            Name = entity.GetAttributeValue<string>("markant_name") ?? "",
            // Tatsächlicher Dataverse-Feldname (mit Tippfehler im Schema)
            DiagnosticsText = entity.GetAttributeValue<string>(DiagnosticsFieldName),
            CreatedOn = entity.GetAttributeValue<DateTime>("createdon")
        };

        var contactRef = entity.GetAttributeValue<EntityReference>("markant_contactid");
        if (contactRef != null) entry.ContactId = contactRef.Id;

        var csRef = entity.GetAttributeValue<EntityReference>("markant_fg_contactsourceid");
        if (csRef != null) entry.ContactSourceId = csRef.Id;

        entry.ParseDiagnostics();
        return entry;
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  SourceSystem-Konvertierung (String ↔ int)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Konvertiert Quellsystem-Angaben zwischen String-Format (FGTestTool) und int-Format.
/// </summary>
public static class SourceSystemHelper
{
    private static readonly Dictionary<string, int> NameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dynamics"] = 1,
        ["LinkedIn"] = 2,
        ["Newsletter"] = 3,
        ["Pisa"] = 4,
        ["Platform"] = 5
    };

    /// <summary>
    /// Löst einen Quellsystem-Wert auf: akzeptiert "Pisa" (String), 4 (int), "4" (String-Zahl).
    /// </summary>
    public static int Resolve(object? value)
    {
        return value switch
        {
            int i => i,
            long l => (int)l,
            string s when int.TryParse(s, out var parsed) => parsed,
            string s when NameToCode.TryGetValue(s, out var code) => code,
            JToken jt when jt.Type == JTokenType.Integer => jt.Value<int>(),
            JToken jt when jt.Type == JTokenType.String => Resolve(jt.Value<string>()),
            _ => throw new InvalidOperationException(
                $"Unbekanntes Quellsystem: '{value}'. Erwartet: int (1-5) oder Name (Dynamics, Pisa, Platform, ...)")
        };
    }

    /// <summary>Gibt den lesbaren Namen für einen SourceSystem-Code zurück.</summary>
    public static string ToName(int code) =>
        NameToCode.FirstOrDefault(kvp => kvp.Value == code).Key ?? $"Unknown({code})";
}
