using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// MVP-3 (ADR-0009 Phase 6b): the Dataverse reverse-read for export-defs. Pins that a jbe_testcase
/// record maps to a DefinitionMirror with the lifecycle VALUE turned back into its keyword (not the
/// localized label), the int fields stringified, and the inventory filter vocabulary honored.
/// </summary>
public class DataverseDefinitionSourceTests
{
    static Entity Tc(string id, Action<Entity>? setup = null)
    {
        var e = new Entity("jbe_testcase", Guid.NewGuid()) { ["jbe_testid"] = id };
        setup?.Invoke(e);
        return e;
    }

    [Fact]
    public void MapMirror_MapsAllFields()
    {
        var e = Tc("M-1", x =>
        {
            x["jbe_title"] = "T";
            x["jbe_lifecyclestatus"] = new OptionSetValue(WorkerSchema.LifecycleActive);
            x["jbe_domain"] = "DSGVO";
            x["jbe_testlevel"] = 2;
            x["jbe_owner"] = "JB";
            x["jbe_tickets"] = "DYN-1,DYN-2";
            x["jbe_envscope"] = "dev,test";
            x["jbe_estimatedminutes"] = 15;
            x["jbe_zephyrkey"] = "Z9";
            x["jbe_tags"] = "bridge";
            x["jbe_documentation"] = "## Zweck";
            x["jbe_definitionjson"] = "{\"id\":\"M-1\"}";
        });

        var m = DataverseDefinitionSource.MapMirror(e)!;

        Assert.Equal("M-1", m.Id);
        Assert.Equal("aktiv", m.Status);          // OptionSet value -> keyword (language-independent)
        Assert.Equal("DSGVO", m.Domaene);
        Assert.Equal("2", m.Stufe);
        Assert.Equal("JB", m.Verantwortlich);
        Assert.Equal("DYN-1,DYN-2", m.Tickets);
        Assert.Equal("dev,test", m.EnvScope);
        Assert.Equal("15", m.GeschaetztMin);
        Assert.Equal("Z9", m.ZephyrKey);
        Assert.Equal("bridge", m.SuiteTags);
        Assert.Equal("## Zweck", m.Documentation);
        Assert.Equal("{\"id\":\"M-1\"}", m.DefinitionJson);
    }

    [Fact]
    public void MapMirror_MissingFields_Empty()
    {
        var m = DataverseDefinitionSource.MapMirror(Tc("M-2"))!;

        Assert.Equal("M-2", m.Id);
        Assert.Equal("", m.Status);
        Assert.Equal("", m.Domaene);
        Assert.Equal("", m.Stufe);
        Assert.Equal("", m.DefinitionJson);
    }

    [Fact]
    public void MapMirror_NoTestId_Null()
        => Assert.Null(DataverseDefinitionSource.MapMirror(new Entity("jbe_testcase")));

    [Theory]
    [InlineData("*", true)]
    [InlineData("tag:bridge", true)]
    [InlineData("tag:other", false)]
    [InlineData("domain:DSGVO", true)]
    [InlineData("domaene:Other", false)]
    [InlineData("M-1", true)]
    [InlineData("M-*", true)]
    [InlineData("X-*", false)]
    public void MatchesFilter_Vocabulary(string filter, bool expected)
    {
        var m = new DefinitionMirror { Id = "M-1", Domaene = "DSGVO", SuiteTags = "bridge,regression" };
        Assert.Equal(expected, DataverseDefinitionSource.MatchesFilter(m, filter));
    }

    [Fact]
    public void Load_AppliesFilterAndMaps()
    {
        var svc = new FakeService(new[]
        {
            Tc("A-1", x => x["jbe_domain"] = "X"),
            Tc("B-1", x => x["jbe_domain"] = "Y"),
        });

        var result = DataverseDefinitionSource.Load(svc, "A-*");

        Assert.Single(result);
        Assert.Equal("A-1", result[0].Id);
    }

    private sealed class FakeService : IOrganizationService
    {
        readonly EntityCollection _all = new();
        public FakeService(IEnumerable<Entity> es) { foreach (var e in es) _all.Entities.Add(e); }
        public EntityCollection RetrieveMultiple(QueryBase query) => _all;

        public Guid Create(Entity entity) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }
}
