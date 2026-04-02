using D365TestCenter.Core;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

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
                    "enabled": true,
                    "dataMode": "template"
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
        Assert.Equal("template", suite.TestCases[0].DataMode);
    }

    [Fact]
    public void TestCase_Defaults_AreCorrect()
    {
        var tc = new TestCase();

        Assert.True(tc.Enabled);
        Assert.Equal("template", tc.DataMode);
        Assert.True(tc.CleanupAfterTest);
        Assert.NotNull(tc.Steps);
        Assert.Empty(tc.Steps);
        Assert.NotNull(tc.Assertions);
        Assert.Empty(tc.Assertions);
    }

    [Fact]
    public void TestStep_Deserializes_WithDefaults()
    {
        const string json = """
        {
            "stepNumber": 1,
            "description": "Create CS",
            "action": "CreateContactSource",
            "sourceSystem": 5,
            "data": { "markant_firstname": "Max" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal(1, step!.StepNumber);
        Assert.Equal("CreateContactSource", step.Action);
        Assert.Equal(5, step.SourceSystemCode);
        Assert.Equal(120, step.TimeoutSeconds);
        Assert.False(step.WaitForGovernance);
    }

    [Fact]
    public void TestAssertion_Deserializes_AllFields()
    {
        const string json = """
        {
            "target": "ContactSource",
            "field": "markant_firstname",
            "operator": "Equals",
            "value": "Max",
            "sourceSystem": 5,
            "withinSeconds": 60,
            "description": "CS hat Vornamen"
        }
        """;

        var assertion = JsonConvert.DeserializeObject<TestAssertion>(json);

        Assert.NotNull(assertion);
        Assert.Equal("ContactSource", assertion!.Target);
        Assert.Equal("markant_firstname", assertion.Field);
        Assert.Equal("Equals", assertion.Operator);
        Assert.Equal("Max", assertion.Value);
        Assert.Equal(5, assertion.SourceSystem);
        Assert.Equal(60, assertion.WithinSeconds);
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
    public void CdhLogEntry_ParseDiagnostics_ExtractsFields()
    {
        var entry = new CdhLogEntry
        {
            DiagnosticsText = """
            CorrelationId=abc-123
            ContactUpdated=True
            PisaTriggered=False
            Decisions:
            Field=firstname | Winner=Max | Strategy=LUW | Updated=True
            Field=lastname | Winner=Muster | Strategy=LUW | Updated=False
            Errors:
            - Config fehlt für telephone2
            """
        };

        entry.ParseDiagnostics();

        Assert.Equal("abc-123", entry.CorrelationId);
        Assert.True(entry.ContactUpdated);
        Assert.False(entry.PisaTriggered);
        Assert.Equal(2, entry.Decisions.Count);
        Assert.Single(entry.UpdatedFields);
        Assert.Equal("firstname", entry.UpdatedFields[0]);
        Assert.Single(entry.Errors);
        Assert.Contains("telephone2", entry.Errors[0]);
    }

    [Fact]
    public void ContactSourceSetup_Defaults()
    {
        var cs = new ContactSourceSetup();

        Assert.True(cs.LinkToContact);
        Assert.True(cs.WaitForGovernance);
        Assert.Null(cs.AsyncWaitOverrideSeconds);
        Assert.NotNull(cs.Fields);
    }

    [Fact]
    public void TestOutcome_Serializes_AsString()
    {
        var result = new TestCaseResult { Outcome = TestOutcome.Failed };
        var json = JsonConvert.SerializeObject(result);

        Assert.Contains("\"Failed\"", json);
    }
}
