using D365TestCenter.Core;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für die JSON-Deserialisierung der Testfall-Modelle.
/// Prüft alle aktuellen Klassen: TestSuiteDefinition, TestCase, TestStep,
/// TestAssertion, GenericPrecondition, PreconditionsConverter, FilterListConverter.
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
        Assert.NotNull(tc.Assertions);
        Assert.Empty(tc.Assertions);
        Assert.NotNull(tc.Preconditions);
        Assert.Empty(tc.Preconditions);
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
    public void TestAssertion_Deserializes_AllFields()
    {
        // Prüft die aktuellen Felder: target, field, operator, value, entity, recordRef, filter
        const string json = """
        {
            "target": "Record",
            "field": "firstname",
            "operator": "Equals",
            "value": "Max",
            "recordRef": "cs1",
            "description": "Vorname geprüft"
        }
        """;

        var assertion = JsonConvert.DeserializeObject<TestAssertion>(json);

        Assert.NotNull(assertion);
        Assert.Equal("Record", assertion!.Target);
        Assert.Equal("firstname", assertion.Field);
        Assert.Equal("Equals", assertion.Operator);
        Assert.Equal("Max", assertion.Value);
        Assert.Equal("cs1", assertion.RecordRef);
        Assert.Equal("Vorname geprüft", assertion.Description);
    }

    [Fact]
    public void TestAssertion_Defaults_AreCorrect()
    {
        var a = new TestAssertion();

        Assert.Equal("Query", a.Target);
        Assert.Equal("Equals", a.Operator);
        Assert.Equal("", a.Field);
        Assert.Null(a.Value);
        Assert.Null(a.Filter);
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

    // ═══════════════════════════════════════════════════════════════════
    //  GenericPrecondition-Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void GenericPrecondition_Deserializes_FromJson()
    {
        const string json = """
        {
            "entity": "contact",
            "alias": "c1",
            "fields": { "firstname": "Max", "lastname": "Muster" },
            "waitForAsync": true
        }
        """;

        var pre = JsonConvert.DeserializeObject<GenericPrecondition>(json);

        Assert.NotNull(pre);
        Assert.Equal("contact", pre!.Entity);
        Assert.Equal("c1", pre.Alias);
        Assert.Equal(2, pre.Fields.Count);
        Assert.True(pre.WaitForAsync);
    }

    [Fact]
    public void GenericPrecondition_Defaults_AreCorrect()
    {
        var pre = new GenericPrecondition();

        Assert.Equal("", pre.Entity);
        Assert.Null(pre.Alias);
        Assert.NotNull(pre.Fields);
        Assert.Empty(pre.Fields);
        Assert.False(pre.WaitForAsync);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PreconditionsConverter-Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void PreconditionsConverter_Array_DeserializesCorrectly()
    {
        const string json = """
        {
            "id": "TC01",
            "title": "Test",
            "preconditions": [
                { "entity": "contact", "alias": "c1", "fields": { "firstname": "Max" } },
                { "entity": "account", "alias": "a1", "fields": { "name": "Firma" } }
            ]
        }
        """;

        var tc = JsonConvert.DeserializeObject<TestCase>(json);

        Assert.NotNull(tc);
        Assert.Equal(2, tc!.Preconditions.Count);
        Assert.Equal("contact", tc.Preconditions[0].Entity);
        Assert.Equal("account", tc.Preconditions[1].Entity);
    }

    [Fact]
    public void PreconditionsConverter_EmptyObject_ReturnsEmptyList()
    {
        // Abwärtskompatibilität: leeres Objekt {} ergibt leere Liste
        const string json = """
        {
            "id": "TC01",
            "title": "Test",
            "preconditions": {}
        }
        """;

        var tc = JsonConvert.DeserializeObject<TestCase>(json);

        Assert.NotNull(tc);
        Assert.Empty(tc!.Preconditions);
    }

    [Fact]
    public void PreconditionsConverter_EmptyArray_ReturnsEmptyList()
    {
        const string json = """
        {
            "id": "TC01",
            "title": "Test",
            "preconditions": []
        }
        """;

        var tc = JsonConvert.DeserializeObject<TestCase>(json);

        Assert.NotNull(tc);
        Assert.Empty(tc!.Preconditions);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  FilterListConverter-Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void FilterListConverter_Object_ConvertsToFilterConditions()
    {
        // Kurzform: {"field": "value"} wird zu FilterCondition mit operator "eq"
        const string json = """
        {
            "target": "Query",
            "field": "firstname",
            "operator": "Equals",
            "entity": "contact",
            "filter": { "lastname": "Muster", "city": "Berlin" }
        }
        """;

        var assertion = JsonConvert.DeserializeObject<TestAssertion>(json);

        Assert.NotNull(assertion);
        Assert.NotNull(assertion!.Filter);
        Assert.Equal(2, assertion.Filter!.Count);
        Assert.Equal("lastname", assertion.Filter[0].Field);
        Assert.Equal("eq", assertion.Filter[0].Operator);
        Assert.Equal("Muster", assertion.Filter[0].Value?.ToString());
    }

    [Fact]
    public void FilterListConverter_Array_DeserializesDirectly()
    {
        // Langform: Array mit expliziten FilterConditions
        const string json = """
        {
            "target": "Query",
            "field": "firstname",
            "operator": "Equals",
            "entity": "contact",
            "filter": [
                { "field": "lastname", "operator": "eq", "value": "Muster" },
                { "field": "statecode", "operator": "eq", "value": "0" }
            ]
        }
        """;

        var assertion = JsonConvert.DeserializeObject<TestAssertion>(json);

        Assert.NotNull(assertion);
        Assert.NotNull(assertion!.Filter);
        Assert.Equal(2, assertion.Filter!.Count);
        Assert.Equal("lastname", assertion.Filter[0].Field);
        Assert.Equal("statecode", assertion.Filter[1].Field);
    }

    [Fact]
    public void FilterListConverter_OnStep_DeserializesCorrectly()
    {
        // FilterListConverter wird auch auf TestStep.Filter verwendet
        const string json = """
        {
            "stepNumber": 1,
            "action": "WaitForRecord",
            "entity": "contact",
            "filter": { "lastname": "Test" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.NotNull(step!.Filter);
        Assert.Single(step.Filter!);
        Assert.Equal("lastname", step.Filter[0].Field);
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
