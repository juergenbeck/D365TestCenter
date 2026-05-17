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
/// B1 Coverage Tests (Session 19).
///
/// Closes documented gaps from the expectException coverage matrix:
///
///   L1 — CallCustomApi / ExecuteAction (legacy aliases of ExecuteRequest)
///        are routed to StepExecuteRequest in the dispatcher but
///        IsSandboxSafeAction filters them out, so they take the normal
///        Step-Loop catch path. Pin both fault variants:
///          • Pattern 1: FaultException at the direct Execute endpoint
///          • Pattern 2: ExecuteMultipleResponse slot fault (not used here
///            since the alias does not go through the Sandbox wrapper)
///        Mismatch case is pinned once to demonstrate matcher engagement.
///
///   L2 — Custom-API Stage 40 PostOp fault placed into the slot is
///        already implicitly covered by FaultInSlotService in
///        ExpectExceptionCustomApiTests but explicitly named here so the
///        coverage matrix shows the variant by name.
///
///   L4 — Single-call non-sandbox actions (RetrieveRecord, Set/Retrieve
///        EnvironmentVariable) route their service call through the normal
///        Step-Loop catch path. Pin RetrieveRecord as representative of the
///        single-call pattern; the EnvironmentVariable actions share the
///        same code path semantics (service.Execute throws → outer catch
///        → EvaluateExpectException(ex)).
///
/// Reference: docs/b1-expectexception-luecken-inventur-2026-05-17.md
/// </summary>
public class ExpectExceptionCoverageTests
{
    // ════════════════════════════════════════════════════════════════
    //  L1 — CallCustomApi alias
    //  (routed to StepExecuteRequest, not via Sandbox-Pfad)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CallCustomApi_FaultAtEndpoint_ExpectExceptionMatches_TestIsPassed()
    {
        var svc = new FaultOnDirectExecuteService(
            faultErrorCode: -2147220969,
            faultMessage: "ContactIds-Array ist leer.");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "L1-CALLCUSTOMAPI-PASS",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "CallCustomApi",
                    ApiName = "markant_RequestFieldGovernanceBatch",
                    Parameters = new Dictionary<string, object?>
                    {
                        ["ContactIds"] = "[]"
                    },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "leer" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(TestOutcome.Passed, result.Results[0].Outcome);
        var step = result.Results[0].StepResults[0];
        Assert.True(step.Success, $"Step.Success was false. Message: {step.Message}");
        Assert.Contains("Expected exception caught", step.Message ?? "");
    }

    [Fact]
    public void CallCustomApi_FaultAtEndpoint_MessageMismatch_TestIsFailed()
    {
        var svc = new FaultOnDirectExecuteService(
            faultErrorCode: -2147220969,
            faultMessage: "Some other plugin error");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "L1-CALLCUSTOMAPI-MISMATCH",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "CallCustomApi",
                    ApiName = "markant_RequestFieldGovernanceBatch",
                    Parameters = new Dictionary<string, object?> { ["ContactIds"] = "[]" },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "xxxNICHTxxx" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains("expectException-Match fehlgeschlagen",
            result.Results[0].StepResults[0].Message ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  L1 — ExecuteAction alias
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void ExecuteAction_FaultAtEndpoint_ExpectExceptionMatches_TestIsPassed()
    {
        var svc = new FaultOnDirectExecuteService(
            faultErrorCode: -2147220970,
            faultMessage: "Price list calculation failed: invalid input");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "L1-EXECUTEACTION-PASS",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "ExecuteAction",
                    ActionName = "new_CalculatePriceList",
                    Parameters = new Dictionary<string, object?>
                    {
                        ["PriceListId"] = "invalid"
                    },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "invalid input" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(TestOutcome.Passed, result.Results[0].Outcome);
        var step = result.Results[0].StepResults[0];
        Assert.True(step.Success);
        Assert.Contains("Expected exception caught", step.Message ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  L2 — Custom-API Stage 40 PostOp (Fault placed into slot via
    //  ExecuteRequest sandbox path)
    //  Already implicitly covered by FaultInSlot tests; named here so
    //  the coverage matrix shows the variant explicitly.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CustomApi_Stage40PostOp_FaultInSlot_ExpectExceptionMatches_TestIsPassed()
    {
        var svc = new FaultInSlotService(
            faultErrorCode: -2147220970,
            faultMessage: "PostOp validation failed: account is locked");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "L2-STAGE40-POSTOP",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "ExecuteRequest",
                    RequestName = "new_LockAccount",
                    Fields = new Dictionary<string, object?>
                    {
                        ["AccountId"] = "00000000-0000-0000-0000-000000000001"
                    },
                    ExpectException = new ExpectExceptionSpec { MessageContains = "locked" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.ErrorCount);
        var step = result.Results[0].StepResults[0];
        Assert.True(step.Success);
        Assert.Contains("Expected exception caught", step.Message ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  L4 — RetrieveRecord (single-call action, normal Step-Loop catch)
    //  Represents the pattern for Set/RetrieveEnvironmentVariable too —
    //  same code path: service.Execute throws → outer catch → matcher.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void RetrieveRecord_PluginFault_ExpectExceptionMatches_TestIsPassed()
    {
        // Two-step test: Step 1 creates the record (alias 'a1'),
        // Step 2 retrieves it with expectException. The mock throws a
        // FaultException from Retrieve so the outer catch engages.
        var svc = new FaultOnRetrieveService(
            faultErrorCode: -2147220891,
            faultMessage: "Retrieve blocked by row-level security plugin");
        var runner = new TestRunner(svc);

        var test = new TestCase
        {
            Id = "L4-RETRIEVERECORD-PASS",
            Steps = new List<TestStep>
            {
                new TestStep
                {
                    StepNumber = 1,
                    Action = "CreateRecord",
                    Entity = "contacts",
                    Alias = "a1",
                    Fields = new Dictionary<string, object?> { ["firstname"] = "Test" }
                },
                new TestStep
                {
                    StepNumber = 2,
                    Action = "RetrieveRecord",
                    Alias = "a1",
                    ExpectException = new ExpectExceptionSpec { MessageContains = "row-level security" }
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(TestOutcome.Passed, result.Results[0].Outcome);
        // The retrieve step is index 1, create-step at index 0 succeeds
        var retrieveStep = result.Results[0].StepResults[1];
        Assert.True(retrieveStep.Success,
            $"Retrieve step.Success was false. Message: {retrieveStep.Message}");
        Assert.Contains("Expected exception caught", retrieveStep.Message ?? "");
    }

    // ════════════════════════════════════════════════════════════════
    //  Mocks
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Mock service that throws FaultException&lt;OrganizationServiceFault&gt;
    /// from Execute() for any non-ExecuteMultiple request. Models the
    /// CLI-path behaviour for CallCustomApi/ExecuteAction aliases: those
    /// actions go through StepExecuteRequest but bypass the sandbox
    /// wrapper (IsSandboxSafeAction is false), so the platform fault
    /// hits the outer Step-Loop catch directly as a managed exception.
    /// </summary>
    private sealed class FaultOnDirectExecuteService : MockOrgServiceBase
    {
        private readonly int _errorCode;
        private readonly string _message;

        public FaultOnDirectExecuteService(int faultErrorCode, string faultMessage)
        {
            _errorCode = faultErrorCode;
            _message = faultMessage;
        }

        public override OrganizationResponse Execute(OrganizationRequest request)
        {
            // ExecuteMultiple is not used by CallCustomApi/ExecuteAction aliases,
            // but defensively succeed if it ever shows up so we never accidentally
            // mask a wrong test setup.
            if (request is ExecuteMultipleRequest)
                return new ExecuteMultipleResponse();

            var fault = new OrganizationServiceFault
            {
                ErrorCode = _errorCode,
                Message = _message
            };
            throw new FaultException<OrganizationServiceFault>(fault, new FaultReason(_message));
        }
    }

    /// <summary>
    /// Mock service that places the inner-request fault into
    /// ExecuteMultipleResponse.Responses[0].Fault. Used here to pin the
    /// Custom-API Stage 40 PostOp variant via the ExecuteRequest sandbox
    /// path (Action = "ExecuteRequest" → IsSandboxSafeAction → wrapped in
    /// ExecuteMultipleRequest). Same shape as FaultInSlotService in
    /// ExpectExceptionCustomApiTests but kept local to this file for
    /// readability of the L2 test.
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

    /// <summary>
    /// Mock service for RetrieveRecord-with-expectException coverage:
    /// Create succeeds (returns a deterministic Id so the alias 'a1' is
    /// populated in TestContext), but the subsequent Retrieve throws a
    /// FaultException as if a row-level security plugin blocked the read.
    /// </summary>
    private sealed class FaultOnRetrieveService : MockOrgServiceBase
    {
        private readonly int _errorCode;
        private readonly string _message;
        private readonly Guid _createdId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        public FaultOnRetrieveService(int faultErrorCode, string faultMessage)
        {
            _errorCode = faultErrorCode;
            _message = faultMessage;
        }

        public override Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            var fault = new OrganizationServiceFault
            {
                ErrorCode = _errorCode,
                Message = _message
            };
            throw new FaultException<OrganizationServiceFault>(fault, new FaultReason(_message));
        }

        public override Guid Create(Entity entity) => _createdId;
    }

    private abstract class MockOrgServiceBase : IOrganizationService
    {
        public virtual OrganizationResponse Execute(OrganizationRequest request) =>
            new OrganizationResponse();
        public virtual Guid Create(Entity entity) => Guid.NewGuid();
        public virtual Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            new Entity(entityName, id);
        public void Update(Entity entity) { }
        public void Delete(string entityName, Guid id) { }
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
