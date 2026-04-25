using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace D365TestCenter.Core;

/// <summary>
/// IOrganizationService-Wrapper that routes every primitive service operation
/// through ExecuteMultipleRequest with ContinueOnError=true, providing a
/// sub-transaction boundary that satisfies the Dataverse sandbox watcher
/// rule "ISV code should not catch exceptions from OrganizationService calls
/// and continue processing" (fault code 0x80040265).
///
/// When the inner request faults, the wrapper extracts the fault and throws
/// an InvalidPluginExecutionException whose message embeds the error code in
/// "0x........" form. This is a managed exception (not an OrganizationService
/// exception), so plugin code may catch and continue without violating the
/// sandbox rule.
///
/// Use this wrapper in any sync plugin that needs to recover from individual
/// service-call failures (e.g. expectFailure-style integration tests). Async
/// plugins do NOT need this wrapper — async plugins run after the pipeline
/// transaction completes and are not subject to the rule.
///
/// Performance: each call adds one ExecuteMultipleRequest envelope and one
/// extra round-trip layer in the pipeline. Measured overhead per operation
/// is approximately 1-2 ms in practice.
///
/// Architecture: ADR-0005 + FB-31b (D365TestCenter-Workspace).
/// </summary>
public sealed class SandboxSafeOrganizationService : IOrganizationService
{
    private readonly IOrganizationService _inner;

    public SandboxSafeOrganizationService(IOrganizationService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Guid Create(Entity entity)
    {
        var resp = (CreateResponse)Execute(new CreateRequest { Target = entity });
        return resp.id;
    }

    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
    {
        var resp = (RetrieveResponse)Execute(new RetrieveRequest
        {
            Target = new EntityReference(entityName, id),
            ColumnSet = columnSet
        });
        return resp.Entity;
    }

    public void Update(Entity entity)
    {
        Execute(new UpdateRequest { Target = entity });
    }

    public void Delete(string entityName, Guid id)
    {
        Execute(new DeleteRequest { Target = new EntityReference(entityName, id) });
    }

    public OrganizationResponse Execute(OrganizationRequest request)
    {
        // ExecuteMultipleRequest cannot wrap another ExecuteMultipleRequest in
        // a meaningful way (returns nested responses). If the caller already
        // builds an ExecuteMultipleRequest, pass it through directly — they
        // know what they're doing, and the inner Faults are explicit anyway.
        if (request is ExecuteMultipleRequest)
        {
            return _inner.Execute(request);
        }

        // Metadata operations and a few other special requests cannot be sent
        // through ExecuteMultipleRequest. Pass them through directly. The
        // sandbox-watcher rule only triggers if the caller catches the
        // resulting service exception and continues — that is the caller's
        // responsibility for these special paths (e.g. DetectConfig in the
        // Custom-API plugin).
        if (IsBypassRequest(request))
        {
            return _inner.Execute(request);
        }

        var emReq = new ExecuteMultipleRequest
        {
            Settings = new ExecuteMultipleSettings
            {
                ContinueOnError = true,
                ReturnResponses = true
            },
            Requests = new OrganizationRequestCollection { request }
        };

        var emResp = (ExecuteMultipleResponse)_inner.Execute(emReq);

        if (emResp.Responses == null || emResp.Responses.Count == 0)
        {
            throw new InvalidPluginExecutionException(
                $"SandboxSafe: ExecuteMultipleResponse hatte keine Responses fuer Request '{request.RequestName}'.");
        }

        var first = emResp.Responses[0];
        if (first.Fault != null)
        {
            throw FaultToException(first.Fault, request.RequestName);
        }

        return first.Response
            ?? throw new InvalidPluginExecutionException(
                $"SandboxSafe: ExecuteMultipleResponse hatte weder Response noch Fault fuer Request '{request.RequestName}'.");
    }

    public void Associate(string entityName, Guid entityId, Relationship relationship,
        EntityReferenceCollection relatedEntities)
    {
        Execute(new AssociateRequest
        {
            Target = new EntityReference(entityName, entityId),
            Relationship = relationship,
            RelatedEntities = relatedEntities
        });
    }

    public void Disassociate(string entityName, Guid entityId, Relationship relationship,
        EntityReferenceCollection relatedEntities)
    {
        Execute(new DisassociateRequest
        {
            Target = new EntityReference(entityName, entityId),
            Relationship = relationship,
            RelatedEntities = relatedEntities
        });
    }

    public EntityCollection RetrieveMultiple(QueryBase query)
    {
        var resp = (RetrieveMultipleResponse)Execute(new RetrieveMultipleRequest { Query = query });
        return resp.EntityCollection;
    }

    /// <summary>
    /// Build a managed exception from an OrganizationServiceFault. The error
    /// code is embedded in the message in "0x........" form so that the
    /// existing TestRunner.ExtractErrorCode regex picks it up. We deliberately
    /// do NOT throw FaultException&lt;OrganizationServiceFault&gt; — that lives in
    /// System.ServiceModel which is not part of netstandard2.0 by default.
    /// </summary>
    public static Exception FaultToException(OrganizationServiceFault fault, string? requestName = null)
    {
        var errorCodeHex = $"0x{fault.ErrorCode:X8}";
        var prefix = string.IsNullOrEmpty(requestName) ? "" : $"[{requestName}] ";
        var msg = $"{prefix}{errorCodeHex}: {fault.Message}";
        return new InvalidPluginExecutionException(msg);
    }

    /// <summary>
    /// Returns true for requests that are NOT supported inside an
    /// ExecuteMultipleRequest envelope. These are routed straight to the
    /// inner service. The list is conservative — adding more categories
    /// over time is fine, but each has to be verified.
    /// </summary>
    private static bool IsBypassRequest(OrganizationRequest request)
    {
        // RequestName is preferred (string-based, stable across SDK versions);
        // GetType().Name is a fallback for OrganizationRequest instances built
        // without a known message (custom APIs use RequestName).
        var name = request.RequestName ?? request.GetType().Name;

        // Metadata operations: cannot batch.
        if (name.StartsWith("RetrieveEntity", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("RetrieveAllEntities", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("RetrieveAttribute", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("RetrieveRelationship", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("RetrieveOptionSet", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("RetrieveManyToMany", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("RetrieveProvider", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("CreateEntity", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("CreateAttribute", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("CreateRelationship", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("CreateOptionSet", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("UpdateEntity", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("UpdateAttribute", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("UpdateRelationship", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("UpdateOptionSet", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("DeleteEntity", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("DeleteAttribute", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("DeleteRelationship", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("DeleteOptionSet", StringComparison.OrdinalIgnoreCase)) return true;
        // Publishing / customization: cannot batch.
        if (name.Equals("PublishXml", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("PublishAllXml", StringComparison.OrdinalIgnoreCase)) return true;
        // Solution / import / export: cannot batch.
        if (name.StartsWith("Import", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("Export", StringComparison.OrdinalIgnoreCase)) return true;
        // ExecuteTransaction is a sibling of ExecuteMultiple — cannot nest.
        if (name.Equals("ExecuteTransaction", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
