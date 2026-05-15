using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests for ADR-0007 (ExecuteRequest consolidation).
///
/// Covers:
///   - Deserialization of legacy aliases: actionName, apiName, parameters
///   - Verb aliasing in TestRunner switch: CallCustomApi, ExecuteAction → StepExecuteRequest
///   - Fallback chain for request name: RequestName ?? ActionName ?? ApiName ?? Entity
///   - Parameter map fallback: Parameters ?? Fields
///   - Regression: canonical ExecuteRequest + requestName + fields still works
///   - Regression: legacy CallCustomApi + entity + fields still works (no migration required)
///
/// Tests run against the real TestRunner with a recording IOrganizationService double.
/// They verify the OrganizationRequest reaches the service with the expected name and parameters.
/// </summary>
public class ExecuteRequestAliasTests
{
    // ================================================================
    //  Deserialization: new alias properties land on the model
    // ================================================================

    [Fact]
    public void TestStep_ActionName_DeserializesFromJson()
    {
        const string json = """
        {
            "action": "ExecuteAction",
            "actionName": "new_CalculatePriceList",
            "parameters": {
                "Target": "{opp.id}",
                "PriceListId": "stdprice"
            }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("ExecuteAction", step!.Action);
        Assert.Equal("new_CalculatePriceList", step.ActionName);
        Assert.NotNull(step.Parameters);
        Assert.Equal(2, step.Parameters!.Count);
        Assert.True(step.Parameters.ContainsKey("Target"));
        Assert.True(step.Parameters.ContainsKey("PriceListId"));
    }

    [Fact]
    public void TestStep_ApiName_DeserializesFromJson()
    {
        // SKILL.md d365-test-center 3.2 used 'apiName' (instead of 'actionName')
        // for the Custom-API name. Both must deserialize to the alias slot.
        const string json = """
        {
            "action": "ExecuteAction",
            "apiName": "lm_CancelInvoice",
            "parameters": { "InvoiceId": "{inv.id}" }
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("lm_CancelInvoice", step!.ApiName);
        Assert.NotNull(step.Parameters);
        Assert.Single(step.Parameters!);
    }

    [Fact]
    public void TestStep_Parameters_IsNullWhenNotSet()
    {
        const string json = """
        { "action": "CreateRecord", "entity": "accounts", "fields": { "name": "Demo" } }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Null(step!.ActionName);
        Assert.Null(step.ApiName);
        Assert.Null(step.Parameters);
    }

    // ================================================================
    //  Dispatch: legacy verbs route to ExecuteRequest pipeline
    // ================================================================

    [Fact]
    public void Verb_CallCustomApi_With_Entity_Fields_Routes_To_ExecuteRequest()
    {
        // Legacy CallCustomApi packs (e.g., LM lm-coverage-boost) must keep working.
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "CallCustomApi",
            Entity = "lm_DoSomething",
            Fields = new Dictionary<string, object?> { ["Foo"] = "bar" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.NotNull(svc.LastRequest);
        Assert.Equal("lm_DoSomething", svc.LastRequest!.RequestName);
        Assert.Equal("bar", svc.LastRequest["Foo"]);
    }

    [Fact]
    public void Verb_ExecuteAction_With_ApiName_Parameters_Routes_To_ExecuteRequest()
    {
        // LM lm-plugin-verification.json and Markant fg-testtool-v2.json pattern.
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ApiName = "markant_RunFieldGovernanceForContact",
            Parameters = new Dictionary<string, object?> { ["ContactId"] = "{c.id}" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.NotNull(svc.LastRequest);
        Assert.Equal("markant_RunFieldGovernanceForContact", svc.LastRequest!.RequestName);
        Assert.Equal("{c.id}", svc.LastRequest["ContactId"]);
    }

    [Fact]
    public void Verb_ExecuteAction_With_ActionName_Parameters_Routes_To_ExecuteRequest()
    {
        // Handbuch ExecuteAction-Sektion pattern: actionName + parameters.
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ActionName = "new_CalculatePriceList",
            Parameters = new Dictionary<string, object?> { ["PriceListId"] = "stdprice" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.NotNull(svc.LastRequest);
        Assert.Equal("new_CalculatePriceList", svc.LastRequest!.RequestName);
        Assert.Equal("stdprice", svc.LastRequest["PriceListId"]);
    }

    [Fact]
    public void Verb_ExecuteAction_With_Entity_Fields_Routes_To_ExecuteRequest()
    {
        // Markant SW03 mixed form: ExecuteAction verb, entity+fields schema.
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            Entity = "markant_RunFieldGovernanceForContact",
            Fields = new Dictionary<string, object?> { ["ContactId"] = "{c.id}" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.NotNull(svc.LastRequest);
        Assert.Equal("markant_RunFieldGovernanceForContact", svc.LastRequest!.RequestName);
        Assert.Equal("{c.id}", svc.LastRequest["ContactId"]);
    }

    [Fact]
    public void Verb_ExecuteRequest_With_RequestName_Fields_Still_Works()
    {
        // Canonical regression: pre-ADR-0007 ExecuteRequest tests unchanged.
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteRequest",
            RequestName = "SetState",
            Fields = new Dictionary<string, object?> { ["Status"] = 2 }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.NotNull(svc.LastRequest);
        Assert.Equal("SetState", svc.LastRequest!.RequestName);
        Assert.Equal(2, svc.LastRequest["Status"]);
    }

    // ================================================================
    //  Fallback chain: RequestName wins; Parameters wins over Fields
    // ================================================================

    [Fact]
    public void RequestName_Wins_Over_ActionName_And_Entity()
    {
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteRequest",
            RequestName = "wins_RequestName",
            ActionName = "loses_ActionName",
            ApiName = "loses_ApiName",
            Entity = "loses_Entity",
            Fields = new Dictionary<string, object?> { ["k"] = "v" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.Equal("wins_RequestName", svc.LastRequest!.RequestName);
    }

    [Fact]
    public void ActionName_Wins_When_RequestName_Null()
    {
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ActionName = "wins_ActionName",
            ApiName = "loses_ApiName",
            Entity = "loses_Entity",
            Fields = new Dictionary<string, object?> { ["k"] = "v" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.Equal("wins_ActionName", svc.LastRequest!.RequestName);
    }

    [Fact]
    public void ApiName_Wins_When_RequestName_And_ActionName_Null()
    {
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ApiName = "wins_ApiName",
            Entity = "loses_Entity",
            Fields = new Dictionary<string, object?> { ["k"] = "v" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.Equal("wins_ApiName", svc.LastRequest!.RequestName);
    }

    [Fact]
    public void Entity_Used_As_Last_Resort()
    {
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "CallCustomApi",
            Entity = "fallback_Entity",
            Fields = new Dictionary<string, object?> { ["k"] = "v" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.Equal("fallback_Entity", svc.LastRequest!.RequestName);
    }

    [Fact]
    public void Parameters_Win_Over_Fields_When_Both_Set()
    {
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ApiName = "test_Api",
            Parameters = new Dictionary<string, object?> { ["fromParameters"] = "P" },
            Fields = new Dictionary<string, object?> { ["fromFields"] = "F" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.True(svc.LastRequest!.Parameters.ContainsKey("fromParameters"));
        Assert.False(svc.LastRequest.Parameters.ContainsKey("fromFields"));
        Assert.Equal("P", svc.LastRequest["fromParameters"]);
    }

    [Fact]
    public void Fields_Used_When_Parameters_Empty()
    {
        var (svc, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            ApiName = "test_Api",
            Parameters = new Dictionary<string, object?>(), // empty -> ignored
            Fields = new Dictionary<string, object?> { ["fromFields"] = "F" }
        });

        runner.RunAll(new List<TestCase> { test });

        Assert.Equal("F", svc.LastRequest!["fromFields"]);
    }

    // ================================================================
    //  Error case: no name at all
    // ================================================================

    [Fact]
    public void Throws_When_All_Name_Sources_Null()
    {
        var (_, runner) = NewRunner();

        var test = MakeTest(new TestStep
        {
            StepNumber = 1,
            Action = "ExecuteAction",
            Fields = new Dictionary<string, object?> { ["k"] = "v" }
        });

        var result = runner.RunAll(new List<TestCase> { test });

        // Step throws → TestCase is marked as Error (onError default "stop" for non-Assert).
        Assert.Equal(1, result.ErrorCount);
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static (RecordingOrgService svc, TestRunner runner) NewRunner()
    {
        var svc = new RecordingOrgService();
        var runner = new TestRunner(svc);
        return (svc, runner);
    }

    private static TestCase MakeTest(params TestStep[] steps)
    {
        return new TestCase
        {
            Id = "EXEC-ALIAS-TEST",
            Title = "ExecuteRequest alias test",
            Enabled = true,
            Steps = new List<TestStep>(steps)
        };
    }

    /// <summary>
    /// IOrganizationService double that records the last OrganizationRequest.
    /// Returns an empty OrganizationResponse so the runner can continue.
    /// </summary>
    private sealed class RecordingOrgService : IOrganizationService
    {
        public OrganizationRequest? LastRequest { get; private set; }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            LastRequest = request;
            return new OrganizationResponse();
        }

        public Guid Create(Entity entity) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet columnSet)
            => throw new NotImplementedException();
        public EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query) => new();
    }
}
