using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Core.Config;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// MVP-3 (ADR-0009 Phase 6b): the export-defs CLI walk/IO. The decisive invariant is the round-trip
/// with build-pack: the no-domain fallback folder must NOT be "_"-prefixed, otherwise CollectDefinitions
/// (which skips "_"-/archive directories) would make the whole no-domain mirror invisible to a re-import.
/// Pins the bug that the live DEV smoke surfaced (all 207 un-migrated defs landed in the fallback folder).
/// </summary>
public class ExportDefsTests
{
    [Fact]
    public void Export_GroupsByDomain_AndRoundTripsThroughCollectDefinitions()
    {
        var withDomain = new Entity("jbe_testcase", Guid.NewGuid())
        {
            ["jbe_testid"] = "BR-01",
            ["jbe_domain"] = "Bridge",
            ["jbe_definitionjson"] = "{\"id\":\"BR-01\",\"steps\":[]}"
        };
        var noDomain = new Entity("jbe_testcase", Guid.NewGuid())
        {
            ["jbe_testid"] = "GEN-01",
            ["jbe_definitionjson"] = "{\"id\":\"GEN-01\",\"steps\":[]}"
        };
        var svc = new FakeService(new[] { withDomain, noDomain });

        var dir = Path.Combine(Path.GetTempPath(), "exp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var sum = ExportDefs.Export(svc, new MarkantConfig(), dir, "*");

            Assert.Equal(2, sum.Written);
            Assert.True(File.Exists(Path.Combine(dir, "Bridge", "BR-01.md")));
            Assert.True(File.Exists(Path.Combine(dir, "ohne-domaene", "GEN-01.md")));   // NOT "_ohne-domaene"

            // Round-trip: build-pack's walk must see BOTH files (no-domain folder is not "_"-excluded).
            var sources = PackBuild.CollectDefinitions(dir).Select(d => d.Source).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "Bridge/BR-01.md", "ohne-domaene/GEN-01.md" }, sources);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Export_NoTestId_Skipped()
    {
        var svc = new FakeService(new[] { new Entity("jbe_testcase", Guid.NewGuid()) });   // no jbe_testid
        var dir = Path.Combine(Path.GetTempPath(), "exp_" + Guid.NewGuid().ToString("N"));
        try
        {
            var sum = ExportDefs.Export(svc, new MarkantConfig(), dir, "*");
            Assert.Equal(0, sum.Written);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
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
