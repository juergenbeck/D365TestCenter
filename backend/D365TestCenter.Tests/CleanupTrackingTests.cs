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

    // ════════════════════════════════════════════════════════════════
    //  ADR-2026-07-23-0808: deklarative Cleanup-Kind-Beziehungen
    //  (cleanupChildren) + 404-Toleranz im Cleanup
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void CleanupChildren_DeletesDeclaredChildrenBeforeParent()
    {
        var svc = new RestrictCleanupService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "CHILD01",
                Title = "cleanupChildren räumt Restrict-Kinder vor dem Parent",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    // Simuliert die Test-LSP, an der ein Plugin asynchron Monatszeilen
                    // (Restrict-Delete-Kinder) erzeugt hat: ohne die Deklaration
                    // scheitert der Cleanup-Delete des Parents dauerhaft.
                    new()
                    {
                        StepNumber = 1, Action = "CreateRecord", Entity = "lm_bestellung", Alias = "lsp",
                        CleanupChildren = new List<CleanupChildRelation>
                        {
                            new() { Entity = "lm_umsatzplan", LookupField = "lm_bestellungid" }
                        }
                    }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        // Beide plugin-erzeugten Kinder wurden zur Cleanup-Zeit gefunden und gelöscht ...
        Assert.Equal(2, svc.DeletedChildIds.Count);
        // ... VOR dem Parent (sonst hätte dessen Restrict-Simulation geworfen):
        Assert.True(svc.ParentDeleted);
        Assert.Equal(0, result.CleanupFailedCount);
    }

    [Fact]
    public void CleanupChildren_WithoutDeclaration_ParentDeleteStillFails()
    {
        // Gegenprobe: ohne Deklaration bleibt das heutige Verhalten (Restrict-Blocker
        // wird als Cleanup-Fehler gezählt, die Kinder bleiben liegen).
        var svc = new RestrictCleanupService();
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "CHILD02",
                Title = "Ohne cleanupChildren blockt der Restrict-Delete weiter",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new() { StepNumber = 1, Action = "CreateRecord", Entity = "lm_bestellung", Alias = "lsp" }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        Assert.Empty(svc.DeletedChildIds);
        Assert.False(svc.ParentDeleted);
        Assert.Equal(1, result.CleanupFailedCount);
    }

    [Fact]
    public void CleanupChildren_ConcurrentDeleteConflict_ReconcilesInFollowUpRound()
    {
        // Live belegt (LM DEV): der async LST-Abbau-Job des Buchungs-Delete löscht
        // dieselben lm_umsatzplan-Kinder parallel -> "More than one concurrent Delete
        // requests detected". Der Cleanup wertet das nicht als Fehler, sondern prüft
        // in einer Folgerunde nach (Query leer = Ziel erreicht).
        var svc = new RestrictCleanupService { ConflictOnFirstChildDelete = true };
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "CHILD04",
                Title = "Konkurrierender Kind-Delete wird in Folgerunde rekonziliert",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new()
                    {
                        StepNumber = 1, Action = "CreateRecord", Entity = "lm_bestellung", Alias = "lsp",
                        CleanupChildren = new List<CleanupChildRelation>
                        {
                            new() { Entity = "lm_umsatzplan", LookupField = "lm_bestellungid" }
                        }
                    }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.CleanupFailedCount);
        // Kind 1 hat der "parallele Job" gelöscht (Konflikt), Kind 2 wir selbst:
        Assert.Single(svc.DeletedChildIds);
        Assert.True(svc.ParentDeleted);
    }

    [Fact]
    public void CleanupDelete_ObjectDoesNotExist_CountsAsAlreadyCleaned()
    {
        // Ein getrackter Record, den die Plattform-Cascade beim Delete eines
        // Eltern-Records bereits mitgenommen hat, wirft 404/ObjectDoesNotExist —
        // das ist kein Datenleck und darf die CLEANUP-WARNUNG nicht mehr erhöhen.
        var svc = new RestrictCleanupService { ThrowNotExistOnParentDelete = true };
        var runner = new TestRunner(svc);

        var result = runner.RunAll(new List<TestCase>
        {
            new()
            {
                Id = "CHILD03",
                Title = "404 beim Cleanup-Delete zählt als bereits geräumt",
                Enabled = true,
                Steps = new List<TestStep>
                {
                    new() { StepNumber = 1, Action = "CreateRecord", Entity = "lm_bestellung", Alias = "lsp" }
                }
            }
        });

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.CleanupFailedCount);
        Assert.False(svc.ParentDeleted); // der Delete kam nie durch — er warf 404
    }

    /// <summary>
    /// Fake für die cleanupChildren-Wirkungstests: Create legt den Parent an, an dem
    /// zwei "plugin-erzeugte" Kinder (lm_umsatzplan) hängen. RetrieveMultiple auf die
    /// Kind-Entität liefert die noch lebenden Kinder; Delete des Parents wirft, solange
    /// Kinder leben (Restrict-Simulation), bzw. optional 404/ObjectDoesNotExist.
    /// </summary>
    private sealed class RestrictCleanupService : IOrganizationService
    {
        public Guid ParentId { get; private set; }
        public readonly List<Guid> LivingChildIds = new();
        public readonly List<Guid> DeletedChildIds = new();
        public bool ParentDeleted { get; private set; }
        public bool ThrowNotExistOnParentDelete { get; set; }
        public bool ConflictOnFirstChildDelete { get; set; }

        public Guid Create(Entity entity)
        {
            ParentId = Guid.NewGuid();
            LivingChildIds.Add(Guid.NewGuid());
            LivingChildIds.Add(Guid.NewGuid());
            return ParentId;
        }

        public void Delete(string entityName, Guid id)
        {
            if (entityName == "lm_umsatzplan")
            {
                if (ConflictOnFirstChildDelete)
                {
                    // Simuliert den parallelen async-Job: ER löscht das Kind (es lebt
                    // nicht mehr), unser Delete-Versuch scheitert am Plattform-Konflikt.
                    ConflictOnFirstChildDelete = false;
                    LivingChildIds.Remove(id);
                    throw new InvalidOperationException(
                        $"More than one concurrent Delete requests detected for an Entity {id} and ObjectTypeCode 11553.");
                }
                LivingChildIds.Remove(id);
                DeletedChildIds.Add(id);
                return;
            }
            if (ThrowNotExistOnParentDelete)
            {
                var fault = new Microsoft.Xrm.Sdk.OrganizationServiceFault
                {
                    ErrorCode = unchecked((int)0x80040217),
                    Message = $"{entityName} With Id = {id} Does Not Exist"
                };
                throw new System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault>(
                    fault, new System.ServiceModel.FaultReason(fault.Message));
            }
            if (LivingChildIds.Count > 0)
                throw new InvalidOperationException(
                    "The object you tried to delete is associated with another object and cannot be deleted.");
            ParentDeleted = true;
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            var ec = new EntityCollection();
            if (query is QueryExpression qe && qe.EntityName == "lm_umsatzplan")
                foreach (var childId in LivingChildIds)
                    ec.Entities.Add(new Entity("lm_umsatzplan", childId));
            return ec;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public void Update(Entity entity) { }
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
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
