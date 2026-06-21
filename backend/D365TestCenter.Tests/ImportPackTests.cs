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

    // ── MVP-3 Phase 6a: dedicated metadata mapping (A.1) ─────────────

    [Fact]
    public void Import_MapsMetadataFields_OnCreate()
    {
        var svc = new RecordingService();
        ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"M-1\", \"title\": \"M\", \"status\": \"aktiv\", \"domaene\": \"DSGVO\", " +
            "\"stufe\": \"2\", \"verantwortlich\": \"JB\", \"tickets\": \"DYN-1,DYN-2\", " +
            "\"env_scope\": \"dev,test\", \"geschaetzt_min\": \"15\", \"zephyr_key\": \"DYN-T9\", \"steps\": [] }"));

        var e = Assert.Single(svc.Created);
        Assert.Equal(105710001, ((OptionSetValue)e["jbe_lifecyclestatus"]).Value);   // aktiv
        Assert.Equal("DSGVO", e["jbe_domain"]);
        Assert.Equal(2, (int)e["jbe_testlevel"]);                                     // int parsed
        Assert.Equal("JB", e["jbe_owner"]);
        Assert.Equal("DYN-1,DYN-2", e["jbe_tickets"]);
        Assert.Equal("dev,test", e["jbe_envscope"]);
        Assert.Equal(15, (int)e["jbe_estimatedminutes"]);
        Assert.Equal("DYN-T9", e["jbe_zephyrkey"]);
    }

    [Fact]
    public void Import_MapsMetadataFields_OnUpdate_LeavesEnabledUntouched()
    {
        var svc = new RecordingService();
        svc.Existing["M-2"] = Guid.NewGuid();
        ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"M-2\", \"title\": \"M\", \"status\": \"instabil\", \"steps\": [] }"));

        var e = Assert.Single(svc.Updated);
        Assert.Equal(105710002, ((OptionSetValue)e["jbe_lifecyclestatus"]).Value);   // instabil
        Assert.False(e.Contains("jbe_enabled"));                                     // activation untouched
    }

    [Fact]
    public void Import_NonNumericStufe_SkippedNoThrow()
    {
        var svc = new RecordingService();
        ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"M-3\", \"stufe\": \"Tier-1\", \"steps\": [] }"));

        var e = Assert.Single(svc.Created);
        Assert.False(e.Contains("jbe_testlevel"));   // non-numeric -> skipped, no exception
    }

    [Fact]
    public void Import_UnknownStatus_LifecycleSkipped()
    {
        var svc = new RecordingService();
        ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"M-4\", \"status\": \"bogus\", \"steps\": [] }"));

        var e = Assert.Single(svc.Created);
        Assert.False(e.Contains("jbe_lifecyclestatus"));
    }

    [Fact]
    public void Import_NoMetadata_OnlyBaseFields()
    {
        var svc = new RecordingService();
        ImportPack.Import(svc, new StandardCrmConfig(), One(
            "{ \"testId\": \"M-5\", \"title\": \"M\", \"steps\": [] }"));

        var e = Assert.Single(svc.Created);
        Assert.False(e.Contains("jbe_domain"));
        Assert.False(e.Contains("jbe_lifecyclestatus"));
        Assert.False(e.Contains("jbe_testlevel"));
    }

    [Theory]
    [InlineData("entwurf", 105710000)]
    [InlineData("aktiv", 105710001)]
    [InlineData("instabil", 105710002)]
    [InlineData("historisch", 105710003)]
    [InlineData("archiviert", 105710004)]
    [InlineData("AKTIV", 105710001)]   // case-insensitive
    public void MapLifecycle_Keywords(string keyword, int expected)
        => Assert.Equal(expected, ImportPack.MapLifecycle(keyword));

    [Fact]
    public void MapLifecycle_Unknown_Null()
        => Assert.Null(ImportPack.MapLifecycle("nope"));

    [Fact]
    public void BuildDefinitionJson_DropsMetadataColumns()
    {
        var tc = JObject.Parse(
            "{ \"testId\": \"M-6\", \"status\": \"aktiv\", \"domaene\": \"D\", \"stufe\": \"1\", " +
            "\"tickets\": \"DYN-1\", \"env_scope\": \"dev\", \"zephyr_key\": \"Z\", " +
            "\"verantwortlich\": \"V\", \"geschaetzt_min\": \"5\", \"steps\": [ { \"stepNumber\": 1 } ] }");

        var def = JObject.Parse(ImportPack.BuildDefinitionJson(tc));

        Assert.Equal("M-6", def.Value<string>("id"));
        foreach (var k in new[] { "status", "domaene", "stufe", "tickets", "env_scope", "zephyr_key", "verantwortlich", "geschaetzt_min" })
            Assert.Null(def[k]);
        Assert.NotNull(def["steps"]);   // executable steps kept
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
