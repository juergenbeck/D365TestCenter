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

    // ════════════════════════════════════════════════════════════════
    //  FB-54: serverseitig erzeugte Records tracken (trackForCleanup /
    //  TrackRecord) + Cleanup-Fehler-Sichtbarkeit
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void WaitForRecord_WithTrackForCleanup_DeletesFoundServerSideRecord()
    {
        var svc = new CleanupTrackingService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "FB54-A1",
                Title = "trackForCleanup:true nimmt den gefundenen Record in die Löschliste",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    // Simuliert den von der getesteten API SERVERSEITIG erzeugten Record
                    // (z.B. invoice), den der Test anschließend findet und markiert.
                    new()
                    {
                        StepNumber = 1, Action = "WaitForRecord", Entity = "accounts", Alias = "beleg",
                        TrackForCleanup = true,
                        PollingIntervalMs = 1, TimeoutSeconds = 10,
                        Filter = new List<FilterCondition>
                        {
                            new() { Field = "name", Operator = "Equals", Value = "Server-Side Created" }
                        }
                    }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        // Der GEFUNDENE Record wird jetzt gelöscht — das explizite Flag hebt den
        // Stammdaten-Schutz-Default gezielt auf (Gegenprobe: CLEAN01/CLEAN02 oben
        // pinnen weiter das Default-false-Verhalten).
        Assert.Contains(svc.FoundRecordId, svc.DeletedIds);
        Assert.Single(svc.DeletedIds);
    }

    [Fact]
    public void TrackRecord_KnownId_IsDeletedInCleanup()
    {
        var svc = new CleanupTrackingService();
        var runner = new TestRunner(svc);
        var serverSideId = Guid.NewGuid(); // simuliert eine Custom-API-Output-Id

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "FB54-A2",
                Title = "TrackRecord registriert eine bekannte Id für den Cleanup",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new()
                    {
                        StepNumber = 1, Action = "TrackRecord", Entity = "invoices",
                        Alias = "inv", RecordId = serverSideId.ToString()
                    }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        Assert.Contains(serverSideId, svc.DeletedIds);
        Assert.Single(svc.DeletedIds);
    }

    [Fact]
    public void TrackRecord_PlaceholderResolved_AndAlreadyTrackedRecordIsNotDeletedTwice()
    {
        var svc = new CleanupTrackingService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "FB54-A2b",
                Title = "TrackRecord löst Platzhalter auf; Dedup verhindert Doppel-Delete",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new() { StepNumber = 1, Action = "CreateRecord", Entity = "accounts", Alias = "created" },
                    // Referenziert den schon getrackten Record per Platzhalter — der
                    // Dedup in RegisterRecord verhindert den zweiten (404-)Delete.
                    new()
                    {
                        StepNumber = 2, Action = "TrackRecord", Entity = "accounts",
                        Alias = "same", RecordId = "{created.id}"
                    }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        var createdId = Assert.Single(svc.CreatedIds);
        Assert.Equal(createdId, Assert.Single(svc.DeletedIds));
        Assert.Equal(0, result.CleanupFailedCount);
    }

    [Fact]
    public void TrackRecord_UnresolvedPlaceholder_IsError_NotSilentlyIgnored()
    {
        var svc = new CleanupTrackingService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "FB54-A2c",
                Title = "TrackRecord mit unauflösbarem Platzhalter bricht als Error ab",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new()
                    {
                        StepNumber = 1, Action = "TrackRecord", Entity = "invoices",
                        RecordId = "{missing.outputs.InvoiceId}"
                    }
                }
            }
        });

        Assert.Equal(1, result.ErrorCount);
        Assert.Empty(svc.DeletedIds);
    }

    [Fact]
    public void CleanupFailure_IsCountedOnRun_ButOutcomeStaysGreen()
    {
        var svc = new CleanupTrackingService();
        svc.FailDeleteFor.Add("account"); // simuliert restrict-delete-Blocker (FB-54)
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "FB54-C",
                Title = "Delete-Blocker: Test bleibt grün, der Lauf weist den Cleanup-Fehler aus",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new() { StepNumber = 1, Action = "CreateRecord", Entity = "accounts", Alias = "acc" },
                    new() { StepNumber = 2, Action = "CreateRecord", Entity = "contacts", Alias = "con" }
                }
            }
        });

        // Outcome bewusst unberührt: fachlich ist der Test grün.
        Assert.Equal(1, result.PassedCount);
        Assert.Equal(0, result.ErrorCount);
        // Aber der Lauf macht das Datenleck sichtbar (FB-54 Sichtbarkeits-Lücke).
        Assert.Equal(1, result.CleanupFailedCount);
        Assert.Equal(1, result.Results[0].CleanupFailedCount);
        // Der Contact wurde geräumt, der blockierte Account nicht.
        Assert.Single(svc.DeletedIds);
        var cleanupStep = Assert.Single(
            result.Results[0].StepResults,
            s => string.Equals(s.Action, "Cleanup", StringComparison.OrdinalIgnoreCase));
        Assert.False(cleanupStep.Success);
        Assert.Contains("account", cleanupStep.Message);
    }

    /// <summary>
    /// Minimaler IOrganizationService-Fake mit Wirkungs-Tracking: Create vergibt eine
    /// Id und merkt sie, Delete merkt die gelöschte Id, RetrieveMultiple liefert für
    /// die Stammdaten-Entity genau einen festen Bestands-Record (FindRecord-Treffer).
    /// Diagnostik-Queries auf andere Entities (z.B. plugintracelog) bleiben leer.
    /// FB-54: Delete wirft für Entities in FailDeleteFor (Restrict-Delete-Simulation).
    /// </summary>
    private sealed class CleanupTrackingService : IOrganizationService
    {
        public readonly Guid FoundRecordId = Guid.NewGuid();
        public readonly List<Guid> CreatedIds = new();
        public readonly List<Guid> DeletedIds = new();
        public readonly HashSet<string> FailDeleteFor = new(StringComparer.OrdinalIgnoreCase);
        public int RetrieveMultipleCalls;
        private const string FoundEntityName = "account";

        public Guid Create(Entity entity)
        {
            var id = Guid.NewGuid();
            CreatedIds.Add(id);
            return id;
        }

        public void Delete(string entityName, Guid id)
        {
            if (FailDeleteFor.Contains(entityName))
                throw new InvalidOperationException(
                    $"The {entityName} record cannot be deleted because it is associated with dependents.");
            DeletedIds.Add(id);
        }

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
