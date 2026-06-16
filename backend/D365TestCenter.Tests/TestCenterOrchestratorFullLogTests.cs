using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Block 1 L v5.3.11: Orchestrator-Log-Buffer + Engine-Log werden in jbe_fulllog
/// gemerged.
///
/// Bug (Markant T9, Bridge 2026-05-18): jbe_fulllog enthielt nur die TestRunner-
/// internen Step-Logs, nicht aber die Orchestrator-Logs (Filter-Banner,
/// Lade-Phase, Cold-Start-Hint, Ergebnis-Block). Damit waren MetadataCache-
/// Lazy-Load-Fehler im Header sowie das Ergebnis-Banner in Dataverse unsichtbar.
///
/// Fix: TestCenterOrchestrator führt zusätzlich zum externen Console-Hook einen
/// internen StringBuilder-Buffer. Beim Final-Update wird der Buffer mit
/// result.FullLog gemerged und unter jbe_fulllog persistiert. Außerdem
/// schreibt der Empty-Pfad und der Fatal-Catch-Pfad jetzt ebenfalls
/// jbe_fulllog.
/// </summary>
public class TestCenterOrchestratorFullLogTests
{
    // Mock-Service: 0 testcases (leerer RetrieveMultiple-Pfad).
    private sealed class EmptyTestCaseService : IOrganizationService
    {
        public List<Dictionary<string, object?>> Updates { get; } = new();

        public Guid Create(Entity entity) => Guid.NewGuid();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            new Entity(entityName, id);
        public void Update(Entity entity)
        {
            Updates.Add(entity.Attributes.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public void Delete(string entityName, Guid id) { }
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship rel, EntityReferenceCollection r) { }
        public void Disassociate(string entityName, Guid entityId, Relationship rel, EntityReferenceCollection r) { }
    }

    // Mock-Service: 1 jbe_testcase mit Wait-Step (kein Service-Call im Test).
    private sealed class SingleWaitTestCaseService : IOrganizationService
    {
        public Guid TestRunId { get; } = Guid.NewGuid();
        public List<Dictionary<string, object?>> Updates { get; } = new();
        public List<string> Created { get; } = new();

        public Guid Create(Entity entity)
        {
            Created.Add(entity.LogicalName);
            return entity.LogicalName == "jbe_testrun" ? TestRunId : Guid.NewGuid();
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            var e = new Entity(entityName, id);
            if (entityName == "jbe_testrun")
            {
                e["jbe_testcasefilter"] = "*";
                e["jbe_keeprecords"] = false;
                e["jbe_passed"] = 0;
                e["jbe_failed"] = 0;
                e["jbe_total"] = 0;
            }
            return e;
        }

        public void Update(Entity entity)
        {
            Updates.Add(entity.Attributes.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            var ec = new EntityCollection();
            var tc = new Entity("jbe_testcase", Guid.NewGuid());
            tc["jbe_testid"] = "T1";
            tc["jbe_title"] = "Smoke";
            tc["jbe_enabled"] = true;
            tc["jbe_definitionjson"] =
                "{\"id\":\"T1\",\"title\":\"Smoke\",\"enabled\":true," +
                "\"steps\":[{\"stepNumber\":1,\"action\":\"Wait\",\"waitSeconds\":0}]}";
            ec.Entities.Add(tc);
            return ec;
        }

        public void Delete(string entityName, Guid id) { }
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship rel, EntityReferenceCollection r) { }
        public void Disassociate(string entityName, Guid entityId, Relationship rel, EntityReferenceCollection r) { }
    }

    private static string ExtractFinalFullLog(List<Dictionary<string, object?>> updates)
    {
        var withLog = updates.LastOrDefault(u => u.ContainsKey("jbe_fulllog"));
        Assert.NotNull(withLog);
        return (string)withLog!["jbe_fulllog"]!;
    }

    // ════════════════════════════════════════════════════════════════
    //  Bug-Pin: Orchestrator-Header muss in jbe_fulllog landen.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void EmptyTestSet_FullLog_ContainsOrchestratorHeader()
    {
        var svc = new EmptyTestCaseService();
        var orchestrator = new TestCenterOrchestrator(svc, new StandardCrmConfig());

        orchestrator.RunNewTestRun("*", false);

        var fullLog = ExtractFinalFullLog(svc.Updates);
        Assert.Contains("TestCenter Run", fullLog);
        Assert.Contains("Filter: *", fullLog);
        Assert.Contains("Keine Testfälle gefunden", fullLog);
    }

    [Fact]
    public void SingleTestCase_FullLog_ContainsOrchestratorAndEngineLogs()
    {
        var svc = new SingleWaitTestCaseService();
        var orchestrator = new TestCenterOrchestrator(svc, new StandardCrmConfig());

        orchestrator.RunNewTestRun("*", false);

        var fullLog = ExtractFinalFullLog(svc.Updates);

        // Orchestrator-Logs (Header + Final-Block)
        Assert.Contains("TestCenter Run", fullLog);
        Assert.Contains("ERGEBNIS:", fullLog);
        // Engine-Marker (Trennzeile)
        Assert.Contains("--- Engine-Log ---", fullLog);
        // Engine-Log (TestRunner-Header)
        Assert.Contains("INTEGRATIONSTEST", fullLog);
    }

    [Fact]
    public void EmptyTestSet_FullLog_HasTimestampPrefix()
    {
        var svc = new EmptyTestCaseService();
        var orchestrator = new TestCenterOrchestrator(svc, new StandardCrmConfig());

        orchestrator.RunNewTestRun("*", false);

        var fullLog = ExtractFinalFullLog(svc.Updates);
        // HH:mm:ss.fff-Prefix-Pattern in jeder Log-Zeile
        Assert.Matches(@"\[\d{2}:\d{2}:\d{2}\.\d{3}\]", fullLog);
    }

    // ════════════════════════════════════════════════════════════════
    //  Backlog I (v5.3.14): Result-Records werden deterministisch nach
    //  RunAll geschrieben, nicht mehr per-Test im OnTestCompleted-Event.
    //  Pin gegen das Befund-1-Symptom (LMApp, Bridge 2026-05-18): ein
    //  Sync-/Custom-API-Run schrieb 0 jbe_testrunresult-Records. Genau ein
    //  Result-Record pro Testfall muss entstehen.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void SingleTestCase_WritesExactlyOneResultRecord()
    {
        var svc = new SingleWaitTestCaseService();
        var orchestrator = new TestCenterOrchestrator(svc, new StandardCrmConfig());

        orchestrator.RunNewTestRun("*", false);

        Assert.Equal(1, svc.Created.Count(e => e == "jbe_testrunresult"));
    }
}
