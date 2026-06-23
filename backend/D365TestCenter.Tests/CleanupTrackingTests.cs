using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Pinnt das Cleanup-Tracking-Verhalten des TestRunners durch die ECHTE Wirkung
/// (Regel 11 / Test prüft echte Änderung): ein per CreateRecord ERZEUGTER Record
/// landet in der Cleanup-Löschliste und wird bei KeepRecords=false gelöscht; ein
/// per FindRecord/WaitForRecord nur GEFUNDENER Bestands-Record NICHT.
///
/// Regression zum Fix (StepWaitForRecord ruft RegisterRecord mit
/// trackForCleanup:false): der ModelsJsonTests-Unit-Test deckt nur die
/// RegisterRecord-Mechanik ab, NICHT die Verdrahtung im Ausführungspfad. Würde
/// jemand das trackForCleanup:false in StepWaitForRecord wieder entfernen, bliebe
/// jener Unit-Test grün - dieser Integrationstest würde rot. Belegt wird die Wirkung
/// über echte Delete-Aufrufe am Service, nicht über Log- oder Aufruf-Präsenz.
/// </summary>
public class CleanupTrackingTests
{
    [Fact]
    public void Cleanup_DeletesCreatedRecord_ButLeavesFoundRecord()
    {
        var svc = new CleanupTrackingService();
        var runner = new TestRunner(svc); // KeepRecords default false -> Cleanup aktiv

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "CLEAN01",
                Title = "CreateRecord + FindRecord, danach Cleanup",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new() { StepNumber = 1, Action = "CreateRecord", Entity = "accounts", Alias = "created" },
                    new()
                    {
                        StepNumber = 2, Action = "FindRecord", Entity = "accounts", Alias = "wbcfg",
                        PollingIntervalMs = 1, TimeoutSeconds = 10,
                        Filter = new List<FilterCondition>
                        {
                            new() { Field = "name", Operator = "Equals", Value = "Shared Master Data" }
                        }
                    }
                }
            }
        });

        // Beide Steps liefen fehlerfrei (FindRecord hat den Bestands-Record gefunden).
        Assert.Equal(0, result.ErrorCount);
        Assert.Single(svc.CreatedIds);
        var createdId = svc.CreatedIds[0];

        // Cleanup ist aktiv (KeepRecords=false): der ERZEUGTE Record wird gelöscht ...
        Assert.Contains(createdId, svc.DeletedIds);
        // ... der per FindRecord GEFUNDENE Bestands-Record NICHT (der eigentliche Fix).
        Assert.DoesNotContain(svc.FoundRecordId, svc.DeletedIds);
        // Genau einer gelöscht: nur der erzeugte, nicht der gefundene.
        Assert.Single(svc.DeletedIds);
    }

    [Fact]
    public void FindRecord_FoundRecord_IsNotRegisteredForCleanup()
    {
        var svc = new CleanupTrackingService();
        var runner = new TestRunner(svc); // KeepRecords default false -> Cleanup aktiv

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "CLEAN02",
                Title = "Nur FindRecord (reiner Lesezugriff)",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new()
                    {
                        StepNumber = 1, Action = "FindRecord", Entity = "accounts", Alias = "wbcfg",
                        PollingIntervalMs = 1, TimeoutSeconds = 10,
                        Filter = new List<FilterCondition>
                        {
                            new() { Field = "name", Operator = "Equals", Value = "Shared Master Data" }
                        }
                    }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        Assert.True(svc.RetrieveMultipleCalls > 0); // FindRecord hat wirklich abgefragt
        // Nur GEFUNDEN, nichts erzeugt -> der Cleanup darf NICHTS löschen.
        Assert.Empty(svc.DeletedIds);
    }

    /// <summary>
    /// Minimaler IOrganizationService-Fake mit Wirkungs-Tracking: Create vergibt eine
    /// Id und merkt sie, Delete merkt die gelöschte Id, RetrieveMultiple liefert für
    /// die Stammdaten-Entity genau einen festen Bestands-Record (FindRecord-Treffer).
    /// Diagnostik-Queries auf andere Entities (z.B. plugintracelog) bleiben leer.
    /// </summary>
    private sealed class CleanupTrackingService : IOrganizationService
    {
        public readonly Guid FoundRecordId = Guid.NewGuid();
        public readonly List<Guid> CreatedIds = new();
        public readonly List<Guid> DeletedIds = new();
        public int RetrieveMultipleCalls;
        private const string FoundEntityName = "account";

        public Guid Create(Entity entity)
        {
            var id = Guid.NewGuid();
            CreatedIds.Add(id);
            return id;
        }

        public void Delete(string entityName, Guid id) => DeletedIds.Add(id);

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            RetrieveMultipleCalls++;
            var ec = new EntityCollection();
            if ((query as QueryExpression)?.EntityName == FoundEntityName)
                ec.Entities.Add(new Entity(FoundEntityName, FoundRecordId));
            return ec;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public void Update(Entity entity) { }
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
