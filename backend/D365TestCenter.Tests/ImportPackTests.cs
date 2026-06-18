using System;
using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Core.Config;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// B5 (ADR-0008): import-pack create/update contract against jbe_testcase, pinned with a
/// recording IOrganizationService fake. Key invariant: jbe_enabled is written only on
/// CREATE (a new test defaults to enabled); an UPDATE must not touch the activation status,
/// because the pack carries no enabled flag - a deliberately disabled test stays disabled.
/// </summary>
public class ImportPackTests
{
    static List<JObject> One(string json) => new() { JObject.Parse(json) };

    [Fact]
    public void Import_NewTestCase_SetsEnabledTrueOnCreate()
    {
        var svc = new RecordingService();   // nothing exists -> CREATE
        var sum = ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"NEW-1\", \"title\": \"Neu\", \"documentation\": \"## Beschreibung\\n\\nx\", \"steps\": [] }"));

        Assert.Equal(1, sum.Created);
        Assert.Equal(0, sum.Updated);
        var e = Assert.Single(svc.Created);
        Assert.Equal("NEW-1", e["jbe_testid"]);
        Assert.True(e.Contains("jbe_enabled"));
        Assert.Equal(true, e["jbe_enabled"]);
    }

    [Fact]
    public void Import_ExistingTestCase_DoesNotTouchEnabledOnUpdate()
    {
        var svc = new RecordingService();
        svc.Existing["EXIST-1"] = Guid.NewGuid();   // pretend it exists (possibly disabled)
        var sum = ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"EXIST-1\", \"title\": \"Da\", \"documentation\": \"## Beschreibung\\n\\nx\", \"steps\": [] }"));

        Assert.Equal(0, sum.Created);
        Assert.Equal(1, sum.Updated);
        var e = Assert.Single(svc.Updated);
        Assert.False(e.Contains("jbe_enabled"));   // activation status left untouched
        Assert.Equal("## Beschreibung\n\nx", e["jbe_documentation"]);
        Assert.True(e.Contains("jbe_definitionjson"));
    }

    [Fact]
    public void Import_NoTestId_Skipped()
    {
        var svc = new RecordingService();
        var sum = ImportPack.Import(svc, new StandardCrmConfig(), One("{ \"title\": \"Ohne Id\", \"steps\": [] }"));

        Assert.Equal(1, sum.Skipped);
        Assert.Empty(svc.Created);
        Assert.Empty(svc.Updated);
    }

    /// <summary>Recording fake: resolves jbe_testid against <see cref="Existing"/>, records create/update entities.</summary>
    private sealed class RecordingService : IOrganizationService
    {
        public Dictionary<string, Guid> Existing { get; } = new(StringComparer.Ordinal);
        public List<Entity> Created { get; } = new();
        public List<Entity> Updated { get; } = new();

        public Guid Create(Entity entity) { Created.Add(entity); return Guid.NewGuid(); }
        public void Update(Entity entity) => Updated.Add(entity);

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            var result = new EntityCollection();
            if (query is QueryExpression qe)
            {
                var cond = qe.Criteria.Conditions.FirstOrDefault(c => c.AttributeName == "jbe_testid");
                if (cond?.Values.FirstOrDefault() is string testId && Existing.TryGetValue(testId, out var id))
                    result.Entities.Add(new Entity("jbe_testcase", id));
            }
            return result;
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }
}
