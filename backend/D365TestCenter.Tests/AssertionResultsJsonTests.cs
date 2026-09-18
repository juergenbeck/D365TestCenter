using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// FB-54: pins the shared jbe_assertionresults blob (AssertionResultsJson) that the
/// three result sinks now use. Assert entries must keep the exact legacy shape (no
/// 'action' property — WebResource parsers rely on it); FAILED cleanup steps are
/// appended with action:"Cleanup" so the audit comment can report leftover test
/// data after a Dataverse round-trip (RunResultLoader).
/// </summary>
public class AssertionResultsJsonTests
{
    static TestCaseResult ResultWith(params StepResult[] steps) => new()
    {
        TestId = "T1",
        StepResults = new List<StepResult>(steps)
    };

    [Fact]
    public void Build_AssertOnly_KeepsLegacyShape_WithoutActionProperty()
    {
        var json = AssertionResultsJson.Build(ResultWith(
            new StepResult { Action = "Assert", Description = "Firma gesetzt", Success = true, ActualDisplay = "JBE" },
            new StepResult { Action = "CreateRecord", Description = "ignored", Success = true }));

        Assert.Contains("\"description\":\"Firma gesetzt\"", json);
        Assert.Contains("\"passed\":true", json);
        // Legacy-Kompatibilität: Assert-Einträge tragen KEIN action-Feld,
        // damit bestehende Parser (WebResource-History) unverändert bleiben.
        Assert.DoesNotContain("\"action\"", json);
    }

    [Fact]
    public void Build_FailedCleanup_AppendsCleanupEntry()
    {
        var json = AssertionResultsJson.Build(ResultWith(
            new StepResult { Action = "Assert", Description = "ok", Success = true },
            new StepResult
            {
                Action = "Cleanup", Description = "Cleanup: 2 gelöscht, 1 fehlgeschlagen",
                Success = false, Message = "account 123: restrict-delete dependents"
            }));

        Assert.Contains("\"action\":\"Cleanup\"", json);
        Assert.Contains("\"passed\":false", json);
        Assert.Contains("restrict-delete dependents", json);
    }

    [Fact]
    public void Build_SuccessfulCleanup_IsOmitted()
    {
        var json = AssertionResultsJson.Build(ResultWith(
            new StepResult { Action = "Cleanup", Description = "Cleanup: 3 gelöscht, 0 fehlgeschlagen", Success = true }));

        Assert.Equal("[]", json);
    }

    [Fact]
    public void RoundTrip_ThroughRunResultLoader_YieldsAssertAndCleanupSteps()
    {
        // Schreibseite -> jbe_assertionresults -> Leseseite (RunResultLoader):
        // der Audit-Kommentar der sync-Pfade sieht denselben Cleanup-Fehler wie
        // ein Live-Lauf. Alt-Blob-Kompatibilität: Einträge ohne action = Assert.
        var blob = AssertionResultsJson.Build(ResultWith(
            new StepResult { Action = "Assert", Description = "Firma gesetzt", Success = true },
            new StepResult { Action = "Cleanup", Description = "Cleanup", Success = false, Message = "acc blocked" }));

        var svc = new SingleResultService(blob);
        var results = RunResultLoader.LoadResultsFromRun(svc, new StandardCrmConfig(), Guid.NewGuid());

        var steps = Assert.Single(results).StepResults;
        Assert.Equal(2, steps.Count);
        Assert.Equal("Assert", steps[0].Action);
        Assert.True(steps[0].Success);
        Assert.Equal("Cleanup", steps[1].Action);
        Assert.False(steps[1].Success);
        Assert.Equal("acc blocked", steps[1].Message);

        // Und der geteilte Audit-Builder macht daraus die Warnung:
        var model = AuditCommentBuilder.BuildModel(null, steps, null);
        Assert.Equal("acc blocked", model.CleanupWarning);
        Assert.Single(model.Checked); // der Cleanup-Eintrag verschmutzt die Assert-Liste nicht
    }

    /// <summary>Fake: liefert genau ein jbe_testrunresult mit dem gegebenen Assertions-Blob.</summary>
    sealed class SingleResultService : IOrganizationService
    {
        readonly string _assertionsJson;
        public SingleResultService(string assertionsJson) => _assertionsJson = assertionsJson;

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            var e = new Entity("jbe_testrunresult", Guid.NewGuid());
            e["jbe_testid"] = "T1";
            e["jbe_assertionresults"] = _assertionsJson;
            var ec = new EntityCollection();
            ec.Entities.Add(e);
            return ec;
        }

        public Guid Create(Entity entity) => Guid.NewGuid();
        public void Delete(string entityName, Guid id) { }
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public void Update(Entity entity) { }
        public OrganizationResponse Execute(OrganizationRequest request) => new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
