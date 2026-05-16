using System;
using System.Collections.Generic;
using System.ServiceModel;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Markant-Bug-Report Session 13 (2026-05-16): expectException on
/// ExecuteRequest against a Custom-API Pattern 1 (PluginType directly bound
/// to customapi.plugintypeid, Stage 30 MainOperation) was reported as
/// Outcome=Errored in the CLI path, even though the messageContains pattern
/// matched. Markant repros: RTC05 (`ContactIds: "[]"`) and RTC03 (201 GUIDs).
///
/// Root cause: the platform wraps Custom-API MainOperation faults as
/// FaultException&lt;OrganizationServiceFault&gt; at the ExecuteMultipleRequest
/// endpoint instead of placing them into ExecuteMultipleResponse.Responses[0].Fault.
/// TestRunner.ExecuteSandboxSafe had no try/catch around _service.Execute(emReq),
/// so the exception propagated to the outer Sandbox-Boundary catch which
/// reported Success=false without ever calling EvaluateExpectException.
///
/// These tests pin both platform variants:
///   Hypothesis A (CLI variant): Service throws FaultException at the endpoint.
///                               Before the fix: test is Errored (BUG).
///                               After the fix: test is Passed.
///   Hypothesis C (plugin variant): Service places fault into the response slot.
///                                  Already green pre-fix, must stay green.
///   Regression (Pattern 2): Service places fault into slot for plain
///                           Create/Update with Pre/PostOp plugin fault.
///                           Must stay green.
/// </summary>
public class ExpectExceptionCustomApiTests
{
    // ════════════════════════════════════════════════════════════════
    //  Hypothesis A: FaultException at the ExecuteMultipleRequest endpoint
    //  (Custom-API Pattern 1 / Stage 30 MainOperation platform variant)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomApi_Stage30_FaultAtEndpoint_ExpectExceptionMatches_TestIsPassed()
    {
        var svc = new FaultAtEndpointService(
            faultErrorCode: -2147220969, // 0x80040217 OperationFailed
            faultMessage: "ContactIds-Array ist leer.");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "RTC05",
            Title = "Markant repro: empty ContactIds",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "ExecuteRequest",
                    RequestName = "markant_RequestFieldGovernanceBatch",
                    Fields = new Dictionary<string, object?>
                    {
                        ["ContactIds"] = "[]",
                        ["ResetFailed"] = false
                    },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "leer" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(TestOutcome.Passed, result.Results[0].Outcome);
        // StepResult message must indicate the matcher ran and succeeded
        var step = result.Results[0].StepResults[0];
        Assert.True(step.Success, $"Step.Success was false. Message: {step.Message}");
        Assert.Contains("Expected exception caught", step.Message ?? "");
    }

    [Fact]
    public void CustomApi_Stage30_FaultAtEndpoint_MessageMismatch_TestIsFailed()
    {
        // expectException is configured but the actual message does NOT match.
        // After the fix, the runner must call EvaluateExpectException, detect
        // the mismatch, and mark the step as Failed (not Errored).
        var svc = new FaultAtEndpointService(
            faultErrorCode: -2147220969,
            faultMessage: "Some other error message");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "RTC05-MISMATCH",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "ExecuteRequest",
                    RequestName = "markant_RequestFieldGovernanceBatch",
                    Fields = new Dictionary<string, object?> { ["ContactIds"] = "[]" },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "xxxNICHTxxx" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains("expectException-Match fehlgeschlagen", result.Results[0].StepResults[0].Message ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  Hypothesis C: Fault placed into ExecuteMultipleResponse slot
    //  (plugin path / Pre/PostOp platform variant)
    //  Already works before the fix - kept as regression guard.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomApi_FaultInSlot_ExpectExceptionMatches_TestIsPassed()
    {
        var svc = new FaultInSlotService(
            faultErrorCode: -2147220969,
            faultMessage: "ContactIds-Array ist leer.");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "RTC05-PLUGIN-VARIANT",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "ExecuteRequest",
                    RequestName = "markant_RequestFieldGovernanceBatch",
                    Fields = new Dictionary<string, object?> { ["ContactIds"] = "[]" },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "leer" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        var step = result.Results[0].StepResults[0];
        Assert.True(step.Success);
        Assert.Contains("Expected exception caught", step.Message ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  Regression: Pre/PostOp plugin fault on plain CreateRecord
    //  Must keep working after the fix.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CreateRecord_PreOpPluginFault_InSlot_ExpectExceptionMatches_TestIsPassed()
    {
        var svc = new FaultInSlotService(
            faultErrorCode: -2147220970,
            faultMessage: "Cannot create contact: validation failed.");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "PREOP-FAULT-01",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "CreateRecord",
                    Entity = "contacts",
                    Alias = "c1",
                    Fields = new Dictionary<string, object?> { ["firstname"] = "Test" },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "validation" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        Assert.True(result.Results[0].StepResults[0].Success);
    }

    // ════════════════════════════════════════════════════════════════
    //  Mocks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mock service that throws FaultException&lt;OrganizationServiceFault&gt;
    /// from the ExecuteMultipleRequest endpoint. Simulates the platform's
    /// observed behaviour for Custom-API Pattern 1 (Stage 30 MainOperation)
    /// faults when wrapped in an ExecuteMultipleRequest envelope.
    /// </summary>
    private sealed class FaultAtEndpointService : MockOrgServiceBase
    {
        private readonly int _errorCode;
        private readonly string _message;

        public FaultAtEndpointService(int faultErrorCode, string faultMessage)
        {
            _errorCode = faultErrorCode;
            _message = faultMessage;
        }

        public override OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request is ExecuteMultipleRequest)
            {
                var fault = new OrganizationServiceFault
                {
                    ErrorCode = _errorCode,
                    Message = _message
                };
                throw new FaultException<OrganizationServiceFault>(fault, new FaultReason(_message));
            }
            return new OrganizationResponse();
        }
    }

    /// <summary>
    /// Mock service that places the inner-request fault into
    /// ExecuteMultipleResponse.Responses[0].Fault, matching the platform's
    /// observed behaviour for Pre/PostOp plugin faults and for the plugin
    /// (async) execution path. ExecuteMultipleRequest returns normally.
    /// </summary>
    private sealed class FaultInSlotService : MockOrgServiceBase
    {
        private readonly int _errorCode;
        private readonly string _message;

        public FaultInSlotService(int faultErrorCode, string faultMessage)
        {
            _errorCode = faultErrorCode;
            _message = faultMessage;
        }

        public override OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request is ExecuteMultipleRequest)
            {
                var emResp = new ExecuteMultipleResponse();
                emResp.Results["Responses"] = new ExecuteMultipleResponseItemCollection
                {
                    new ExecuteMultipleResponseItem
                    {
                        RequestIndex = 0,
                        Fault = new OrganizationServiceFault
                        {
                            ErrorCode = _errorCode,
                            Message = _message
                        }
                    }
                };
                return emResp;
            }
            return new OrganizationResponse();
        }
    }

    private abstract class MockOrgServiceBase : IOrganizationService
    {
        public abstract OrganizationResponse Execute(OrganizationRequest request);
        public Guid Create(Entity entity) => Guid.NewGuid();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public void Update(Entity entity) { }
        public void Delete(string entityName, Guid id) { }
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
