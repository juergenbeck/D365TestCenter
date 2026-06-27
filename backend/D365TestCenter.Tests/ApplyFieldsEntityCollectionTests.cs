using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Pinnt den $type/EntityCollection-Support im CreateRecord/UpdateRecord
/// (ADR-2026-06-27): ApplyFields löst ein Feld mit "$type" über den rekursiven
/// ResolveTypedValue auf, statt es als flachen Attributwert zu behandeln. Damit
/// kann ein CreateRecord eine activityparty-Partylist (z.B. "requiredattendees")
/// als echte EntityCollection ins Target schreiben — Voraussetzung für den
/// LM-Integrationstest PLG-APPT-KONT-01.
///
/// Belegt die ECHTE Wirkung am Create-Target (Regel 11 / Test prüft echte
/// Änderung), nicht über Log- oder Aufruf-Präsenz: der Fake-Service fängt das
/// beim _service.Create übergebene appointment-Entity ab; geprüft wird, dass
/// "requiredattendees" eine EntityCollection mit einer activityparty ist, deren
/// "partyid" eine EntityReference auf den in Step 1 ERZEUGTEN Contact trägt.
///
/// Gegenprobe: ohne den $type-Zweig in ApplyFields landet der JObject-Wert über
/// ConvertValue als String (JObject.ToString()) im Target -> IsType&lt;EntityCollection&gt;
/// schlägt fehl (manuell verifiziert: $type-Block auskommentiert -> rot).
/// </summary>
public class ApplyFieldsEntityCollectionTests
{
    [Fact]
    public void CreateRecord_WithEntityCollectionField_SetsPartyListOnTarget()
    {
        const string json = """
        {
            "id": "APPLY-PARTYLIST-01",
            "title": "CreateRecord mit EntityCollection-Partylist (requiredattendees)",
            "enabled": true,
            "steps": [
                {
                    "stepNumber": 1, "action": "CreateRecord", "entity": "contacts", "alias": "con",
                    "fields": { "firstname": "JBE", "lastname": "Partylist" }
                },
                {
                    "stepNumber": 2, "action": "CreateRecord", "entity": "appointments", "alias": "appt",
                    "fields": {
                        "subject": "JBE AppKont",
                        "requiredattendees": {
                            "$type": "EntityCollection",
                            "entities": [
                                {
                                    "$type": "Entity", "entity": "activityparty",
                                    "fields": {
                                        "partyid": { "$type": "EntityReference", "entity": "contact", "ref": "con" }
                                    }
                                }
                            ]
                        }
                    }
                }
            ]
        }
        """;

        // MetadataPropertyHandling.Ignore wie im Produktionspfad (TestCaseLoader /
        // TestCenterOrchestrator): sonst frisst Newtonsoft das "$type"-Property als
        // Type-Metadaten, und das $type-System (hier wie im ExecuteRequest-Pfad) bekäme
        // ein Objekt ohne $type. Genau diese Settings nutzt die Engine beim Pack-Laden.
        var settings = new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore };
        var tc = JsonConvert.DeserializeObject<TestCase>(json, settings);
        Assert.NotNull(tc);

        var svc = new PartyListCaptureService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase> { tc! });

        // Der Setup-Lauf (zwei CreateRecord-Steps) ist fehlerfrei durchgelaufen.
        Assert.Equal(0, result.ErrorCount);
        Assert.NotEqual(Guid.Empty, svc.ContactId);
        Assert.NotNull(svc.AppointmentTarget);

        // requiredattendees ist eine echte EntityCollection (nicht ein String/JObject).
        Assert.True(svc.AppointmentTarget!.Contains("requiredattendees"));
        var partylist = Assert.IsType<EntityCollection>(svc.AppointmentTarget["requiredattendees"]);

        // Genau eine activityparty mit partyid -> EntityReference auf den erzeugten Contact.
        var party = Assert.Single(partylist.Entities);
        Assert.Equal("activityparty", party.LogicalName);
        var partyId = Assert.IsType<EntityReference>(party["partyid"]);
        Assert.Equal("contact", partyId.LogicalName);
        Assert.Equal(svc.ContactId, partyId.Id);
    }

    /// <summary>
    /// Minimaler IOrganizationService-Fake: Create vergibt je eine Id und fängt
    /// das contact- bzw. appointment-Target ab. Metadata-Abfragen schlagen (mangels
    /// echter Verbindung) fehl und werden vom EntityMetadataCache zu null gefangen —
    /// für den $type-Pfad irrelevant, weil ResolveTypedValue keine Metadata braucht.
    /// </summary>
    private sealed class PartyListCaptureService : IOrganizationService
    {
        public Guid ContactId { get; private set; }
        public Entity? AppointmentTarget { get; private set; }

        public Guid Create(Entity entity)
        {
            var id = Guid.NewGuid();
            if (entity.LogicalName == "contact") ContactId = id;
            if (entity.LogicalName == "appointment") AppointmentTarget = entity;
            return id;
        }

        public void Delete(string entityName, Guid id) { }
        public void Update(Entity entity) { }
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
