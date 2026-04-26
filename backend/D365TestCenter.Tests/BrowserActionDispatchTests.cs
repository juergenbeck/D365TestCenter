using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Unit tests for the BrowserAction step dispatch in TestRunner (ADR-0006).
///
/// Covers:
///   - BrowserAction without an injected executor is skipped (Plugin path)
///   - BrowserAction with an injected executor invokes ExecuteAsync
///   - BrowserAction does not block other action types in the same test
///
/// Does NOT cover:
///   - Actual Playwright behaviour (that lives in
///     D365TestCenter.Cli.UiAutomation.PlaywrightBrowserActionExecutor and is
///     verified end-to-end via the PoC under workspace 08_projects/markant/poc).
/// </summary>
public class BrowserActionDispatchTests
{
    [Fact]
    public void BrowserAction_WithoutExecutor_IsSkipped_NotFailed()
    {
        var service = new MinimalOrgService();
        var runner = new TestRunner(service); // no browser executor injected

        var test = new TestCase
        {
            Id = "UI-DISPATCH-1",
            Title = "BrowserAction without executor must be skipped",
            Enabled = true,
            Steps = new List<TestStep>
            {
                new()
                {
                    StepNumber = 1,
                    Action = "BrowserAction",
                    Operation = "navigate",
                    Url = "https://example.invalid"
                }
            }
        };

        var result = runner.RunAll(new List<TestCase> { test });

        // Test-case should not be marked as Failed/Error — Skipped step is OK.
        Assert.Equal(1, result.TotalCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public void BrowserAction_WithExecutor_InvokesExecutor()
    {
        var service = new MinimalOrgService();
        var executor = new RecordingBrowserActionExecutor();
        var runner = new TestRunner(service, executor);

        var test = new TestCase
        {
            Id = "UI-DISPATCH-2",
            Title = "BrowserAction with executor must invoke it",
            Enabled = true,
            Steps = new List<TestStep>
            {
                new()
                {
                    StepNumber = 1,
                    Action = "BrowserAction",
                    Operation = "navigate",
                    Url = "https://example.invalid"
                },
                new()
                {
                    StepNumber = 2,
                    Action = "BrowserAction",
                    Operation = "click",
                    Selector = "[data-id='ok']"
                }
            }
        };

        runner.RunAll(new List<TestCase> { test });

        Assert.Equal(2, executor.ExecuteCallCount);
        Assert.Equal(2, executor.ExecutedSteps.Count);
        Assert.Equal("navigate", executor.ExecutedSteps[0].Operation);
        Assert.Equal("click", executor.ExecutedSteps[1].Operation);
    }

    // ---------------------------------------------------------------------
    //  Test doubles
    // ---------------------------------------------------------------------

    private sealed class MinimalOrgService : IOrganizationService
    {
        public Guid Create(Entity entity) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public OrganizationResponse Execute(OrganizationRequest request)
        {
            // Metadata-Cache lookups — return an empty response, the runner only uses
            // metadata when it dispatches CRUD steps. BrowserAction never touches OrgService.
            return new OrganizationResponse();
        }
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
            => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, Microsoft.Xrm.Sdk.Query.ColumnSet columnSet)
            => throw new NotImplementedException();
        public EntityCollection RetrieveMultiple(Microsoft.Xrm.Sdk.Query.QueryBase query)
            => new EntityCollection();
    }

    private sealed class RecordingBrowserActionExecutor : IBrowserActionExecutor
    {
        public int ExecuteCallCount { get; private set; }
        public List<TestStep> ExecutedSteps { get; } = new();
        public StepDiagnostics? LastDiagnostics => null;

        public Task ExecuteAsync(TestStep step, TestContext ctx)
        {
            ExecuteCallCount++;
            ExecutedSteps.Add(step);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
