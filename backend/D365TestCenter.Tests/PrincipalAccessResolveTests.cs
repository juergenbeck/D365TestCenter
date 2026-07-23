using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Pinnt den $type/PrincipalAccess-Support im ExecuteRequest-Parameterbau
/// (ADR-2026-07-23, Backlog O, Markant-Bridge T11): ResolveTypedValue erzeugt
/// aus { "$type": "PrincipalAccess", "principal": ..., "accessMask": "..." }
/// einen echten SDK-PrincipalAccess — Voraussetzung für GrantAccess-/
/// ModifyAccess-Setups in Packs (Markant DYN10387-TC10).
///
/// Belegt die ECHTE Wirkung am abgefangenen OrganizationRequest (Regel 11),
/// nicht über Log-Präsenz: der Fake-Service fängt den bei _service.Execute
/// übergebenen Request ab; geprüft werden RequestName, Target und der
/// PrincipalAccess-Parameter (Principal-EntityReference auf das in Step 2
/// ERZEUGTE Team, AccessMask als geparste [Flags]-Kombination).
///
/// Gegenprobe: ohne den PRINCIPALACCESS-Case wirft ResolveTypedValue
/// "Unbekannter $type" -> Step-Error, Happy-Path-Tests rot (verifiziert:
/// Tests vor der Implementierung ausgeführt -> rot).
/// </summary>
public class PrincipalAccessResolveTests
{
    private static TestCase LoadTestCase(string json)
    {
        // MetadataPropertyHandling.Ignore wie im Produktionspfad (TestCaseLoader /
        // TestCenterOrchestrator): sonst frisst Newtonsoft "$type" als Type-Metadaten.
        var settings = new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore };
        var tc = JsonConvert.DeserializeObject<TestCase>(json, settings);
        Assert.NotNull(tc);
        return tc!;
    }

    private static string GrantAccessCase(string accessMask) => $$"""
        {
            "id": "PRINCIPALACCESS-01",
            "title": "ExecuteRequest GrantAccess mit $type PrincipalAccess",
            "enabled": true,
            "steps": [
                {
                    "stepNumber": 1, "action": "CreateRecord", "entity": "contacts", "alias": "con",
                    "fields": { "firstname": "JBE", "lastname": "Share" }
                },
                {
                    "stepNumber": 2, "action": "CreateRecord", "entity": "teams", "alias": "teamAT",
                    "fields": { "name": "JBE Team AT" }
                },
                {
                    "stepNumber": 3, "action": "ExecuteRequest", "requestName": "GrantAccess",
                    "fields": {
                        "Target": { "$type": "EntityReference", "entity": "contact", "ref": "con" },
                        "PrincipalAccess": {
                            "$type": "PrincipalAccess",
                            "principal": { "$type": "EntityReference", "entity": "team", "ref": "teamAT" },
                            "accessMask": "{{accessMask}}"
                        }
                    }
                }
            ]
        }
        """;

    [Fact]
    public void ExecuteRequest_GrantAccess_WithPrincipalAccess_BuildsTypedParameter()
    {
        var tc = LoadTestCase(GrantAccessCase("ReadAccess,WriteAccess,AppendAccess"));

        var svc = new RequestCaptureService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase> { tc });

        Assert.Equal(0, result.ErrorCount);
        Assert.NotNull(svc.CapturedRequest);
        Assert.Equal("GrantAccess", svc.CapturedRequest!.RequestName);

        // Target: EntityReference auf den in Step 1 erzeugten Contact.
        var target = Assert.IsType<EntityReference>(svc.CapturedRequest["Target"]);
        Assert.Equal("contact", target.LogicalName);
        Assert.Equal(svc.ContactId, target.Id);

        // PrincipalAccess: echter SDK-Typ, Principal = erzeugtes Team, Maske geparst.
        var pa = Assert.IsType<PrincipalAccess>(svc.CapturedRequest["PrincipalAccess"]);
        Assert.Equal("team", pa.Principal.LogicalName);
        Assert.Equal(svc.TeamId, pa.Principal.Id);
        Assert.Equal(
            AccessRights.ReadAccess | AccessRights.WriteAccess | AccessRights.AppendAccess,
            pa.AccessMask);
    }

    [Fact]
    public void ExecuteRequest_ModifyAccess_AccessMaskIsCaseInsensitive()
    {
        // netstandard2.0-Detail: nicht-generisches Enum.Parse mit ignoreCase: true.
        const string json = """
        {
            "id": "PRINCIPALACCESS-02",
            "title": "ExecuteRequest ModifyAccess, accessMask case-insensitiv",
            "enabled": true,
            "steps": [
                {
                    "stepNumber": 1, "action": "CreateRecord", "entity": "contacts", "alias": "con",
                    "fields": { "firstname": "JBE", "lastname": "Share" }
                },
                {
                    "stepNumber": 2, "action": "CreateRecord", "entity": "teams", "alias": "teamAT",
                    "fields": { "name": "JBE Team AT" }
                },
                {
                    "stepNumber": 3, "action": "ExecuteRequest", "requestName": "ModifyAccess",
                    "fields": {
                        "Target": { "$type": "EntityReference", "entity": "contact", "ref": "con" },
                        "PrincipalAccess": {
                            "$type": "PrincipalAccess",
                            "principal": { "$type": "EntityReference", "entity": "team", "ref": "teamAT" },
                            "accessMask": "readaccess,shareaccess"
                        }
                    }
                }
            ]
        }
        """;
        var tc = LoadTestCase(json);

        var svc = new RequestCaptureService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase> { tc });

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal("ModifyAccess", svc.CapturedRequest!.RequestName);
        var pa = Assert.IsType<PrincipalAccess>(svc.CapturedRequest["PrincipalAccess"]);
        Assert.Equal(AccessRights.ReadAccess | AccessRights.ShareAccess, pa.AccessMask);
    }

    [Fact]
    public void ExecuteRequest_PrincipalAccess_InvalidAccessMask_IsStepError()
    {
        var tc = LoadTestCase(GrantAccessCase("ReadAccess,Schreibrecht"));

        var svc = new RequestCaptureService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase> { tc });

        // Der Parameterbau wirft VOR dem Execute: Step-Error, kein Request abgesetzt.
        Assert.Equal(1, result.ErrorCount);
        Assert.Null(svc.CapturedRequest);
    }

    [Fact]
    public void ExecuteRequest_PrincipalAccess_MissingPrincipal_IsStepError()
    {
        const string json = """
        {
            "id": "PRINCIPALACCESS-04",
            "title": "PrincipalAccess ohne principal ist ein Step-Error",
            "enabled": true,
            "steps": [
                {
                    "stepNumber": 1, "action": "CreateRecord", "entity": "contacts", "alias": "con",
                    "fields": { "firstname": "JBE", "lastname": "Share" }
                },
                {
                    "stepNumber": 2, "action": "ExecuteRequest", "requestName": "GrantAccess",
                    "fields": {
                        "Target": { "$type": "EntityReference", "entity": "contact", "ref": "con" },
                        "PrincipalAccess": {
                            "$type": "PrincipalAccess",
                            "accessMask": "ReadAccess"
                        }
                    }
                }
            ]
        }
        """;
        var tc = LoadTestCase(json);

        var svc = new RequestCaptureService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase> { tc });

        Assert.Equal(1, result.ErrorCount);
        Assert.Null(svc.CapturedRequest);
    }

    /// <summary>
    /// Minimaler IOrganizationService-Fake: Create vergibt Ids (Contact/Team werden
    /// gemerkt), Execute fängt den gebauten OrganizationRequest ab. Metadata-Abfragen
    /// schlagen fehl und werden vom EntityMetadataCache zu null gefangen — für den
    /// $type-Pfad irrelevant, weil ResolveTypedValue keine Metadata braucht.
    /// </summary>
    private sealed class RequestCaptureService : IOrganizationService
    {
        public Guid ContactId { get; private set; }
        public Guid TeamId { get; private set; }
        public OrganizationRequest? CapturedRequest { get; private set; }

        public Guid Create(Entity entity)
        {
            var id = Guid.NewGuid();
            if (entity.LogicalName == "contact") ContactId = id;
            if (entity.LogicalName == "team") TeamId = id;
            return id;
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            // Nur die Test-Messages abfangen; interne Requests (z.B. Metadata) nicht.
            if (request.RequestName is "GrantAccess" or "ModifyAccess")
                CapturedRequest = request;
            return new OrganizationResponse();
        }

        public void Delete(string entityName, Guid id) { }
        public void Update(Entity entity) { }
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
