using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer den SandboxSafeOrganizationService-Wrapper. ADR-0005 + FB-31b:
/// Im Sync-Plugin-Pfad muessen alle primitiven Service-Calls via
/// ExecuteMultipleRequest mit ContinueOnError=true laufen, damit der
/// Sandbox-Wachter nicht 0x80040265 wirft.
/// </summary>
public class SandboxSafeOrganizationServiceTests
{
    [Fact]
    public void Create_WrapsRequestInExecuteMultiple()
    {
        var inner = new RecordingOrganizationService();
        inner.NextResponse = new CreateResponse
        {
            Results = new ParameterCollection { ["id"] = System.Guid.NewGuid() }
        };

        var wrapper = new SandboxSafeOrganizationService(inner);
        var entity = new Entity("contact");

        var id = wrapper.Create(entity);

        Assert.NotEqual(System.Guid.Empty, id);
        Assert.Single(inner.ReceivedRequests);
        Assert.IsType<ExecuteMultipleRequest>(inner.ReceivedRequests[0]);
        var em = (ExecuteMultipleRequest)inner.ReceivedRequests[0];
        Assert.True(em.Settings.ContinueOnError);
        Assert.True(em.Settings.ReturnResponses);
        Assert.Single(em.Requests);
        Assert.IsType<CreateRequest>(em.Requests[0]);
    }

    [Fact]
    public void Update_WrapsRequestInExecuteMultiple()
    {
        var inner = new RecordingOrganizationService();
        inner.NextResponse = new UpdateResponse();

        var wrapper = new SandboxSafeOrganizationService(inner);
        wrapper.Update(new Entity("contact", System.Guid.NewGuid()));

        Assert.Single(inner.ReceivedRequests);
        Assert.IsType<ExecuteMultipleRequest>(inner.ReceivedRequests[0]);
    }

    [Fact]
    public void Delete_WrapsRequestInExecuteMultiple()
    {
        var inner = new RecordingOrganizationService();
        inner.NextResponse = new DeleteResponse();

        var wrapper = new SandboxSafeOrganizationService(inner);
        wrapper.Delete("contact", System.Guid.NewGuid());

        Assert.Single(inner.ReceivedRequests);
        Assert.IsType<ExecuteMultipleRequest>(inner.ReceivedRequests[0]);
    }

    [Fact]
    public void Retrieve_WrapsRequestInExecuteMultiple()
    {
        var inner = new RecordingOrganizationService();
        var fetched = new Entity("contact", System.Guid.NewGuid());
        inner.NextResponse = new RetrieveResponse
        {
            Results = new ParameterCollection { ["Entity"] = fetched }
        };

        var wrapper = new SandboxSafeOrganizationService(inner);
        var got = wrapper.Retrieve("contact", fetched.Id, new ColumnSet(true));

        Assert.Same(fetched, got);
        Assert.IsType<ExecuteMultipleRequest>(inner.ReceivedRequests[0]);
    }

    [Fact]
    public void RetrieveMultiple_WrapsRequestInExecuteMultiple()
    {
        var inner = new RecordingOrganizationService();
        var coll = new EntityCollection(new List<Entity> { new("contact") });
        inner.NextResponse = new RetrieveMultipleResponse
        {
            Results = new ParameterCollection { ["EntityCollection"] = coll }
        };

        var wrapper = new SandboxSafeOrganizationService(inner);
        var got = wrapper.RetrieveMultiple(new QueryExpression("contact"));

        Assert.Same(coll, got);
        Assert.IsType<ExecuteMultipleRequest>(inner.ReceivedRequests[0]);
    }

    [Fact]
    public void Fault_BecomesInvalidPluginExecutionException_WithErrorCodeInMessage()
    {
        var inner = new RecordingOrganizationService();
        inner.NextResponse = BuildFaultyEmResponse(unchecked((int)0x80040227), "GDPR-Guard blockiert");

        var wrapper = new SandboxSafeOrganizationService(inner);

        var ex = Assert.Throws<InvalidPluginExecutionException>(
            () => wrapper.Update(new Entity("contact", System.Guid.NewGuid())));

        Assert.Contains("0x80040227", ex.Message);
        Assert.Contains("GDPR-Guard blockiert", ex.Message);
    }

    [Fact]
    public void Fault_ErrorCodeIsExtractableViaTestRunner()
    {
        // End-to-End: Fault aus Wrapper -> EvaluateExpectException matcht.
        var inner = new RecordingOrganizationService();
        inner.NextResponse = BuildFaultyEmResponse(unchecked((int)0x80040227), "blocked");

        var wrapper = new SandboxSafeOrganizationService(inner);

        Exception? caught = null;
        try { wrapper.Update(new Entity("contact", System.Guid.NewGuid())); }
        catch (InvalidPluginExecutionException ex) { caught = ex; }

        Assert.NotNull(caught);
        var spec = new ExpectExceptionSpec { ErrorCode = "0x80040227" };
        var (ok, _) = TestRunner.EvaluateExpectException(spec, caught!);
        Assert.True(ok);
    }

    [Fact]
    public void RetrieveEntityRequest_PassesThroughDirectly()
    {
        // Metadata-Requests koennen nicht in ExecuteMultipleRequest gewrappt
        // werden — Wrapper muss bypass machen.
        var inner = new RecordingOrganizationService();
        inner.NextResponse = new RetrieveEntityResponse();

        var wrapper = new SandboxSafeOrganizationService(inner);
        var req = new RetrieveEntityRequest
        {
            LogicalName = "account",
            EntityFilters = EntityFilters.Entity
        };
        wrapper.Execute(req);

        Assert.Single(inner.ReceivedRequests);
        Assert.IsType<RetrieveEntityRequest>(inner.ReceivedRequests[0]);
    }

    [Fact]
    public void NestedExecuteMultiple_PassesThroughDirectly()
    {
        // ExecuteMultipleRequest soll nicht erneut gewrappt werden.
        var inner = new RecordingOrganizationService();
        inner.NextResponse = new ExecuteMultipleResponse
        {
            Results = new ParameterCollection { ["Responses"] = new ExecuteMultipleResponseItemCollection() }
        };

        var wrapper = new SandboxSafeOrganizationService(inner);
        var req = new ExecuteMultipleRequest
        {
            Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
            Requests = new OrganizationRequestCollection()
        };
        wrapper.Execute(req);

        Assert.Single(inner.ReceivedRequests);
        Assert.Same(req, inner.ReceivedRequests[0]);
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => new SandboxSafeOrganizationService(null!));
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static ExecuteMultipleResponse BuildFaultyEmResponse(int errorCode, string message)
    {
        var responses = new ExecuteMultipleResponseItemCollection
        {
            new ExecuteMultipleResponseItem
            {
                RequestIndex = 0,
                Fault = new OrganizationServiceFault
                {
                    ErrorCode = errorCode,
                    Message = message
                }
            }
        };
        return new ExecuteMultipleResponse
        {
            Results = new ParameterCollection { ["Responses"] = responses }
        };
    }

    /// <summary>
    /// Minimaler Test-Helper: zeichnet alle Execute-Requests auf und
    /// liefert einen vorgegebenen Response. Beim Wrap-Pfad ist der
    /// Response immer ein ExecuteMultipleResponse.
    /// </summary>
    private sealed class RecordingOrganizationService : IOrganizationService
    {
        public List<OrganizationRequest> ReceivedRequests { get; } = new();
        public OrganizationResponse? NextResponse { get; set; }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            ReceivedRequests.Add(request);

            // Wrap-Pfad gibt ExecuteMultipleResponse zurueck mit der
            // vorgegebenen Inner-Response (oder Fault).
            if (request is ExecuteMultipleRequest em && NextResponse != null
                && NextResponse is not ExecuteMultipleResponse)
            {
                var responses = new ExecuteMultipleResponseItemCollection
                {
                    new ExecuteMultipleResponseItem
                    {
                        RequestIndex = 0,
                        Response = NextResponse
                    }
                };
                return new ExecuteMultipleResponse
                {
                    Results = new ParameterCollection { ["Responses"] = responses }
                };
            }

            return NextResponse
                ?? throw new System.InvalidOperationException("RecordingOrganizationService: NextResponse not set");
        }

        public System.Guid Create(Entity entity) => throw new System.NotImplementedException();
        public Entity Retrieve(string entityName, System.Guid id, ColumnSet columnSet) => throw new System.NotImplementedException();
        public void Update(Entity entity) => throw new System.NotImplementedException();
        public void Delete(string entityName, System.Guid id) => throw new System.NotImplementedException();
        public void Associate(string en, System.Guid id, Relationship rel, EntityReferenceCollection rc) => throw new System.NotImplementedException();
        public void Disassociate(string en, System.Guid id, Relationship rel, EntityReferenceCollection rc) => throw new System.NotImplementedException();
        public EntityCollection RetrieveMultiple(QueryBase query) => throw new System.NotImplementedException();
    }
}
