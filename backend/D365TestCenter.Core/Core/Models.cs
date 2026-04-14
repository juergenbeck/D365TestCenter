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

/// <summary>Einzelner Testfall innerhalb einer Suite.</summary>
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

    [JsonProperty("preconditions")]
    [JsonConverter(typeof(PreconditionsConverter))]
    public List<GenericPrecondition> Preconditions { get; set; } = new();

    [JsonProperty("steps")]
    public List<TestStep> Steps { get; set; } = new();

    [JsonProperty("assertions")]
    public List<TestAssertion> Assertions { get; set; } = new();

    // ── Datengetriebene Tests ─────────────────────────────────

    [JsonProperty("dataRows")]
    public List<Dictionary<string, object?>>? DataRows { get; set; }

    // ── Testfall-Abhängigkeiten ───────────────────────────────

    [JsonProperty("dependsOn")]
    public List<string>? DependsOn { get; set; }

    [JsonProperty("sharedContext")]
    public string? SharedContext { get; set; }
}

/// <summary>Generische Precondition: {entity, alias, fields}.</summary>
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

    /// <summary>Nach dem Create diese Spalten per Retrieve laden (für {alias.fields.x}).</summary>
    [JsonProperty("columns")]
    public List<string>? Columns { get; set; }
}

/// <summary>
/// JsonConverter: akzeptiert preconditions als Array [{entity,alias,fields}] (generisch)
/// oder als leeres Objekt {} (Abwärtskompatibilität).
/// </summary>
public sealed class PreconditionsConverter : JsonConverter<List<GenericPrecondition>>
{
    public override List<GenericPrecondition>? ReadJson(JsonReader reader, Type objectType,
        List<GenericPrecondition>? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var token = JToken.Load(reader);

        if (token.Type == JTokenType.Array)
            return token.ToObject<List<GenericPrecondition>>(serializer)
                ?? new List<GenericPrecondition>();

        // Leeres Objekt {} oder unbekanntes Format: keine Preconditions
        return new List<GenericPrecondition>();
    }

    public override void WriteJson(JsonWriter writer, List<GenericPrecondition>? value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
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

/// <summary>Einzelner Testschritt.</summary>
public sealed class TestStep
{
    [JsonProperty("stepNumber")]
    public int StepNumber { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    /// <summary>
    /// CreateRecord, UpdateRecord, DeleteRecord, WaitForRecord,
    /// WaitForFieldValue, CallCustomApi, AssertEnvironment, Wait.
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
}

/// <summary>Einzelne Assertion zur Prüfung nach der Testausführung.</summary>
public sealed class TestAssertion
{
    /// <summary>
    /// "Record" (per recordRef), "Query" (per entity+filter).
    /// </summary>
    [JsonProperty("target")]
    public string Target { get; set; } = "Query";

    [JsonProperty("field")]
    public string Field { get; set; } = "";

    [JsonProperty("entity")]
    public string? Entity { get; set; }

    [JsonProperty("recordRef")]
    public string? RecordRef { get; set; }

    [JsonProperty("filter")]
    [JsonConverter(typeof(FilterListConverter))]
    public List<FilterCondition>? Filter { get; set; }

    /// <summary>Equals, NotEquals, IsNull, IsNotNull, Contains, DateSetRecently, Changed, Exists, RecordCount.</summary>
    [JsonProperty("operator")]
    public string Operator { get; set; } = "Equals";

    [JsonProperty("value")]
    public string? Value { get; set; }

    [JsonProperty("description")]
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
    Skipped
}
