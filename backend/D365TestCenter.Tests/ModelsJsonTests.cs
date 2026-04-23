using D365TestCenter.Core;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer die JSON-Deserialisierung der Testfall-Modelle.
/// ADR-0004: Preconditions und Assertions als separate JSON-Arrays entfallen —
/// alles ist ein Step.
/// </summary>
public class ModelsJsonTests
{
    [Fact]
    public void TestSuiteDefinition_Deserializes_FromJson()
    {
        const string json = """
        {
            "suiteId": "FG-Test",
            "suiteName": "Field Governance Tests",
            "testCases": [
                {
                    "id": "TC01",
                    "title": "LUW Single Source",
                    "enabled": true
                }
            ]
        }
        """;

        var suite = JsonConvert.DeserializeObject<TestSuiteDefinition>(json);

        Assert.NotNull(suite);
        Assert.Equal("FG-Test", suite!.SuiteId);
        Assert.Single(suite.TestCases);
        Assert.Equal("TC01", suite.TestCases[0].Id);
        Assert.True(suite.TestCases[0].Enabled);
    }

    [Fact]
    public void TestCase_Defaults_AreCorrect()
    {
        var tc = new TestCase();

        Assert.True(tc.Enabled);
        Assert.NotNull(tc.Steps);
        Assert.Empty(tc.Steps);
        Assert.NotNull(tc.Tags);
        Assert.Empty(tc.Tags);
    }

    [Fact]
    public void TestCase_Deserializes_WithStepsOnly()
    {
        // ADR-0004: JSON hat nur ein "steps"-Array, kein preconditions/assertions.
        const string json = """
        {
            "id": "TC01",
            "title": "CRUD-Test",
            "steps": [
                { "stepNumber": 1, "action": "CreateRecord", "entity": "contact", "alias": "c1", "fields": { "firstname": "Max" } },
                { "stepNumber": 2, "action": "Assert", "target": "Record", "recordRef": "{RECORD:c1}", "field": "firstname", "operator": "Equals", "value": "Max" }
            ]
        }
        """;

        var tc = JsonConvert.DeserializeObject<TestCase>(json);

        Assert.NotNull(tc);
        Assert.Equal("TC01", tc!.Id);
        Assert.Equal(2, tc.Steps.Count);
        Assert.Equal("CreateRecord", tc.Steps[0].Action);
        Assert.Equal("Assert", tc.Steps[1].Action);
    }

    [Fact]
    public void TestStep_Deserializes_WithDefaults()
    {
        const string json = """
        {
            "stepNumber": 1,
            "description": "Record erstellen",
            "action": "CreateRecord",
            "entity": "contact",
            "fields": { "firstname": "Max" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal(1, step!.StepNumber);
        Assert.Equal("CreateRecord", step.Action);
        Assert.Equal("contact", step.Entity);
        Assert.Equal(120, step.TimeoutSeconds);
        Assert.True(step.Fields.ContainsKey("firstname"));
    }

    [Fact]
    public void TestStep_Assert_DeserializesAllFields()
    {
        // ADR-0004: Assert ist eine Step-Action. Target/Field/Operator/Value/OnError
        // stehen direkt auf TestStep.
        const string json = """
        {
            "stepNumber": 5,
            "action": "Assert",
            "target": "Record",
            "recordRef": "{RECORD:c1}",
            "field": "firstname",
            "operator": "Equals",
            "value": "Max",
            "description": "Vorname gesetzt",
            "onError": "continue"
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("Assert", step!.Action);
        Assert.Equal("Record", step.Target);
        Assert.Equal("{RECORD:c1}", step.RecordRef);
        Assert.Equal("firstname", step.Field);
        Assert.Equal("Equals", step.Operator);
        Assert.Equal("Max", step.Value);
        Assert.Equal("continue", step.OnError);
        Assert.Equal("Vorname gesetzt", step.Description);
    }

    [Fact]
    public void TestRunResult_Serializes_RoundTrip()
    {
        var result = new TestRunResult
        {
            StartedAt = new DateTime(2026, 3, 26, 14, 0, 0, DateTimeKind.Utc),
            TotalCount = 10,
            PassedCount = 8,
            FailedCount = 2
        };
        result.Results.Add(new TestCaseResult
        {
            TestId = "TC01",
            Title = "Test 1",
            Outcome = TestOutcome.Passed,
            DurationMs = 1234
        });

        var json = JsonConvert.SerializeObject(result);
        var deserialized = JsonConvert.DeserializeObject<TestRunResult>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(10, deserialized!.TotalCount);
        Assert.Equal(8, deserialized.PassedCount);
        Assert.Single(deserialized.Results);
        Assert.Equal("TC01", deserialized.Results[0].TestId);
        Assert.Equal(TestOutcome.Passed, deserialized.Results[0].Outcome);
    }

    [Fact]
    public void TestOutcome_Serializes_AsString()
    {
        var result = new TestCaseResult { Outcome = TestOutcome.Failed };
        var json = JsonConvert.SerializeObject(result);

        Assert.Contains("\"Failed\"", json);
    }

    [Fact]
    public void StepResult_HasAllRelevantFields()
    {
        // Neue StepResult-Struktur ab ADR-0004: universelle Form fuer alle
        // Action-Typen, ohne Phase-Property.
        var sr = new StepResult
        {
            StepNumber = 1,
            Action = "CreateRecord",
            Description = "Create contact",
            Success = true,
            DurationMs = 50,
            Alias = "c1",
            Entity = "contact",
            RecordId = Guid.NewGuid()
        };

        var json = JsonConvert.SerializeObject(sr);
        var back = JsonConvert.DeserializeObject<StepResult>(json);

        Assert.NotNull(back);
        Assert.Equal("CreateRecord", back!.Action);
        Assert.Equal("c1", back.Alias);
        Assert.Equal(sr.RecordId, back.RecordId);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FilterListConverter-Tests (wird auf TestStep.Filter angewendet)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FilterListConverter_Object_ConvertsToFilterConditions()
    {
        // Kurzform: {"field": "value"} wird zu FilterCondition mit operator "eq"
        const string json = """
        {
            "stepNumber": 1,
            "action": "WaitForRecord",
            "entity": "contact",
            "filter": { "lastname": "Muster", "city": "Berlin" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.NotNull(step!.Filter);
        Assert.Equal(2, step.Filter!.Count);
        Assert.Equal("lastname", step.Filter[0].Field);
        Assert.Equal("eq", step.Filter[0].Operator);
        Assert.Equal("Muster", step.Filter[0].Value?.ToString());
    }

    [Fact]
    public void FilterListConverter_Array_DeserializesDirectly()
    {
        // Langform: Array mit expliziten FilterConditions
        const string json = """
        {
            "stepNumber": 1,
            "action": "WaitForRecord",
            "entity": "contact",
            "filter": [
                { "field": "lastname", "operator": "eq", "value": "Muster" },
                { "field": "statecode", "operator": "eq", "value": "0" }
            ]
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.NotNull(step!.Filter);
        Assert.Equal(2, step.Filter!.Count);
        Assert.Equal("lastname", step.Filter[0].Field);
        Assert.Equal("statecode", step.Filter[1].Field);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TestContext-Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void TestContext_RegisterRecord_AddsToRegistryAndCleanup()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var id = Guid.NewGuid();

        ctx.RegisterRecord("myAlias", "contact", id);

        Assert.True(ctx.Records.ContainsKey("myAlias"));
        Assert.Equal(id, ctx.Records["myAlias"].Id);
        Assert.Equal("contact", ctx.Records["myAlias"].EntityName);
        Assert.Single(ctx.CreatedEntities);
    }

    [Fact]
    public void TestContext_ResolveRecordId_ThrowsForUnknownAlias()
    {
        var ctx = new TestContext { TestId = "TC01" };

        Assert.Throws<InvalidOperationException>(() => ctx.ResolveRecordId("unbekannt"));
    }
}
