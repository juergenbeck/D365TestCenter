using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für die Engine v5.2 Features:
/// - ResolveTypedValue ($type-System)
/// - RequestName auf TestStep
/// - Columns auf GenericPrecondition
/// - ResolveFieldValues preserviert JObjects
/// - PlaceholderEngine: ungelöste {alias.fields.x} Platzhalter
/// </summary>
public class ExecuteRequestTests
{
    // ================================================================
    //  JSON-Deserialisierung
    // ================================================================

    [Fact]
    public void TestStep_RequestName_DeserializesFromJson()
    {
        const string json = """
        {
            "action": "ExecuteRequest",
            "requestName": "Merge",
            "fields": {
                "Target": { "$type": "EntityReference", "entity": "contact", "ref": "con1" },
                "PerformParentingChecks": false
            },
            "waitSeconds": 5
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("ExecuteRequest", step!.Action);
        Assert.Equal("Merge", step.RequestName);
        Assert.Equal(5, step.WaitSeconds);
        Assert.True(step.Fields.ContainsKey("Target"));
        Assert.True(step.Fields.ContainsKey("PerformParentingChecks"));
    }

    [Fact]
    public void TestStep_RequestName_NullWhenNotSet()
    {
        const string json = """
        {
            "action": "CreateRecord",
            "entity": "accounts",
            "fields": { "name": "Test" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Null(step!.RequestName);
    }

    [Fact]
    public void TestStep_Columns_DeserializesFromJson()
    {
        // ADR-0004: "columns" wird auf einem CreateRecord-Step ausgewertet
        // (vormals Precondition). Auto-Retrieve nach dem Create.
        const string json = """
        {
            "stepNumber": 1,
            "action": "CreateRecord",
            "entity": "contacts",
            "alias": "con1",
            "fields": { "firstname": "Test" },
            "columns": ["markant_goldenrecordidnumber", "contactid"]
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.NotNull(step!.Columns);
        Assert.Equal(2, step.Columns!.Count);
        Assert.Equal("markant_goldenrecordidnumber", step.Columns[0]);
        Assert.Equal("contactid", step.Columns[1]);
    }

    [Fact]
    public void TestStep_Columns_NullWhenNotSet()
    {
        const string json = """
        {
            "stepNumber": 1,
            "action": "CreateRecord",
            "entity": "accounts",
            "alias": "acc1",
            "fields": { "name": "Test" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Null(step!.Columns);
    }

    // ================================================================
    //  ResolveFieldValues preserviert JObjects
    // ================================================================

    [Fact]
    public void ResolveFieldValues_PreservesJObjects()
    {
        // Simuliere was passiert wenn Fields ein $type-Objekt enthalten:
        // Die JSON-Deserialisierung erzeugt JObjects für verschachtelte Objekte.
        // ResolveAll (PlaceholderEngine) darf diese NICHT zu Strings konvertieren.
        var engine = new PlaceholderEngine();
        var ctx = new TestContext { TestId = "TEST01" };

        var fields = new Dictionary<string, object?>
        {
            ["Target"] = JObject.Parse("""{"$type":"EntityReference","entity":"contact","ref":"con1"}"""),
            ["SubordinateId"] = JObject.Parse("""{"$type":"Guid","ref":"con2"}"""),
            ["PerformParentingChecks"] = new JValue(false),
            ["SimpleString"] = "Hello {TESTID}"
        };

        var resolved = engine.ResolveAll(fields, ctx);

        // JObjects müssen als JObjects erhalten bleiben
        Assert.IsType<JObject>(resolved["Target"]);
        Assert.IsType<JObject>(resolved["SubordinateId"]);

        // Primitive JValues bleiben erhalten (Bool ist kein String)
        var boolVal = resolved["PerformParentingChecks"];
        Assert.NotNull(boolVal);

        // Strings werden aufgeloest
        Assert.Equal("Hello TEST01", resolved["SimpleString"]);
    }

    [Fact]
    public void ResolveFieldValues_JObjectWithDollarType_HasCorrectStructure()
    {
        var engine = new PlaceholderEngine();
        var ctx = new TestContext { TestId = "TEST01" };

        var fields = new Dictionary<string, object?>
        {
            ["Target"] = JObject.Parse("""{"$type":"EntityReference","entity":"contact","ref":"myAlias"}""")
        };

        var resolved = engine.ResolveAll(fields, ctx);
        var target = resolved["Target"] as JObject;

        Assert.NotNull(target);
        Assert.True(target!.ContainsKey("$type"));
        Assert.Equal("EntityReference", target["$type"]!.Value<string>());
        Assert.Equal("contact", target["entity"]!.Value<string>());
        Assert.Equal("myAlias", target["ref"]!.Value<string>());
    }

    // ================================================================
    //  $type Objekt-Struktur (werden von ResolveTypedValue verarbeitet)
    // ================================================================

    [Fact]
    public void DollarType_EntityReference_HasRequiredFields()
    {
        var json = JObject.Parse("""{"$type":"EntityReference","entity":"contact","ref":"myAlias"}""");

        Assert.True(json.ContainsKey("$type"));
        Assert.Equal("EntityReference", json["$type"]!.Value<string>());
        Assert.Equal("contact", json["entity"]!.Value<string>());
        Assert.Equal("myAlias", json["ref"]!.Value<string>());
    }

    [Fact]
    public void DollarType_EntityReference_WithId_HasRequiredFields()
    {
        var guid = Guid.NewGuid().ToString();
        var json = JObject.Parse($$$"""{"$type":"EntityReference","entity":"contact","id":"{{{guid}}}"}""");

        Assert.Equal("EntityReference", json["$type"]!.Value<string>());
        Assert.Equal(guid, json["id"]!.Value<string>());
        Assert.False(json.ContainsKey("ref"));
    }

    [Fact]
    public void DollarType_Guid_WithRef()
    {
        var json = JObject.Parse("""{"$type":"Guid","ref":"myAlias"}""");

        Assert.Equal("Guid", json["$type"]!.Value<string>());
        Assert.Equal("myAlias", json["ref"]!.Value<string>());
    }

    [Fact]
    public void DollarType_OptionSetValue()
    {
        var json = JObject.Parse("""{"$type":"OptionSetValue","value":595300002}""");

        Assert.Equal("OptionSetValue", json["$type"]!.Value<string>());
        Assert.Equal(595300002, json["value"]!.Value<int>());
    }

    [Fact]
    public void DollarType_Money()
    {
        var json = JObject.Parse("""{"$type":"Money","value":1500.50}""");

        Assert.Equal("Money", json["$type"]!.Value<string>());
        Assert.Equal(1500.50m, json["value"]!.Value<decimal>());
    }

    [Fact]
    public void DollarType_Entity_EmptyFields()
    {
        var json = JObject.Parse("""{"$type":"Entity","entity":"contact","fields":{}}""");

        Assert.Equal("Entity", json["$type"]!.Value<string>());
        Assert.Equal("contact", json["entity"]!.Value<string>());
        var fields = json["fields"] as JObject;
        Assert.NotNull(fields);
        Assert.Empty(fields!.Properties());
    }

    // ================================================================
    //  Vollstaendiger ExecuteRequest Testcase deserialisiert korrekt
    // ================================================================

    [Fact]
    public void FullMergeTestCase_Deserializes_Correctly()
    {
        // ADR-0004-Format: alles in einer Steps-Liste, inklusive Setup und Assert.
        const string json = """
        {
            "id": "MGR06",
            "title": "Merge ohne Golden Record",
            "steps": [
                { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "acc1", "fields": { "name": "Test" } },
                { "stepNumber": 2, "action": "CreateRecord", "entity": "contacts", "alias": "con1", "fields": { "firstname": "A" }, "columns": ["contactid"] },
                { "stepNumber": 3, "action": "CreateRecord", "entity": "contacts", "alias": "con2", "fields": { "firstname": "B" } },
                {
                    "stepNumber": 4,
                    "action": "ExecuteRequest",
                    "requestName": "Merge",
                    "fields": {
                        "Target": { "$type": "EntityReference", "entity": "contact", "ref": "con1" },
                        "SubordinateId": { "$type": "Guid", "ref": "con2" },
                        "UpdateContent": { "$type": "Entity", "entity": "contact", "fields": {} },
                        "PerformParentingChecks": false
                    },
                    "waitSeconds": 5
                },
                { "stepNumber": 5, "action": "Assert", "target": "Query", "entity": "contacts", "filter": [{"field":"contactid","operator":"eq","value":"{RECORD:con1}"}], "field": "statecode", "operator": "Equals", "value": "0" }
            ]
        }
        """;

        var tc = JsonConvert.DeserializeObject<TestCase>(json);

        Assert.NotNull(tc);
        Assert.Equal(5, tc!.Steps.Count);

        // Setup-Steps (ehemals Preconditions)
        Assert.Equal("CreateRecord", tc.Steps[0].Action);
        Assert.Equal("acc1", tc.Steps[0].Alias);
        Assert.NotNull(tc.Steps[1].Columns);
        Assert.Single(tc.Steps[1].Columns!);
        Assert.Null(tc.Steps[2].Columns);

        // ExecuteRequest-Step
        var mergeStep = tc.Steps[3];
        Assert.Equal("ExecuteRequest", mergeStep.Action);
        Assert.Equal("Merge", mergeStep.RequestName);
        Assert.Equal(5, mergeStep.WaitSeconds);

        // Fields enthalten JObjects (nicht Strings!)
        Assert.IsType<JObject>(mergeStep.Fields["Target"]);
        Assert.IsType<JObject>(mergeStep.Fields["SubordinateId"]);
        Assert.IsType<JObject>(mergeStep.Fields["UpdateContent"]);

        // $type im JObject
        var target = (JObject)mergeStep.Fields["Target"]!;
        Assert.Equal("contact", target["entity"]!.Value<string>());
        Assert.Equal("con1", target["ref"]!.Value<string>());
        var hasType = target.ContainsKey("$type");
        if (hasType)
            Assert.Equal("EntityReference", target["$type"]!.Value<string>());

        // Assert-Step
        var assertStep = tc.Steps[4];
        Assert.Equal("Assert", assertStep.Action);
        Assert.Equal("Query", assertStep.Target);
        Assert.Equal("Equals", assertStep.Operator);
        Assert.Equal("statecode", assertStep.Field);
    }
}
