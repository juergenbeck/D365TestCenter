using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Pinnt den $type/ColumnSet-Support im ExecuteRequest-Parameterbau:
/// ResolveTypedValue erzeugt aus { "$type": "ColumnSet", "columns": [...] }
/// bzw. { "$type": "ColumnSet", "allColumns": true } einen echten SDK-ColumnSet.
/// Voraussetzung für Plattformnachrichten mit Pflichtparameter ColumnSet,
/// z.B. GenerateQuoteFromOpportunity und ConvertQuoteToSalesOrder.
///
/// Belegt die Wirkung am abgefangenen OrganizationRequest (nicht über Log-Präsenz).
/// Gegenprobe: ohne den COLUMNSET-Case wirft ResolveTypedValue "Unbekannter $type"
/// -> Step-Error, die Positivtests sind rot (vor der Implementierung ausgeführt).
/// </summary>
public class ColumnSetResolveTests
{
    private static TestCase LoadTestCase(string json)
    {
        // MetadataPropertyHandling.Ignore wie im Produktionspfad, sonst frisst Newtonsoft "$type".
        var settings = new JsonSerializerSettings { MetadataPropertyHandling = MetadataPropertyHandling.Ignore };
        var tc = JsonConvert.DeserializeObject<TestCase>(json, settings);
        Assert.NotNull(tc);
        return tc!;
    }

    private static string GenerateQuoteCase(string columnSetJson) => $$"""
        {
            "id": "COLUMNSET-01",
            "title": "ExecuteRequest GenerateQuoteFromOpportunity mit $type ColumnSet",
            "enabled": true,
            "steps": [
                {
                    "stepNumber": 1, "action": "CreateRecord", "entity": "opportunities", "alias": "opp",
                    "fields": { "name": "JBE ColumnSet" }
                },
                {
                    "stepNumber": 2, "action": "ExecuteRequest", "requestName": "GenerateQuoteFromOpportunity",
                    "fields": {
                        "OpportunityId": { "$type": "Guid", "ref": "opp" },
                        "ColumnSet": {{columnSetJson}}
                    }
                }
            ]
        }
        """;

    private static (TestRunResult Result, RequestCaptureService Svc) Run(string columnSetJson)
    {
        var tc = LoadTestCase(GenerateQuoteCase(columnSetJson));
        var svc = new RequestCaptureService();
        var result = new TestRunner(svc).RunAll(new List<TestCase> { tc });
        return (result, svc);
    }

    [Fact]
    public void ExecuteRequest_ColumnSet_WithColumns_BuildsTypedParameter()
    {
        var (result, svc) = Run("""{ "$type": "ColumnSet", "columns": ["quoteid", "name"] }""");

        Assert.Equal(0, result.ErrorCount);
        Assert.NotNull(svc.CapturedRequest);
        Assert.Equal(svc.OpportunityId, Assert.IsType<Guid>(svc.CapturedRequest!["OpportunityId"]));

        var cs = Assert.IsType<ColumnSet>(svc.CapturedRequest["ColumnSet"]);
        Assert.False(cs.AllColumns);
        Assert.Equal(new[] { "quoteid", "name" }, cs.Columns);
    }

    [Fact]
    public void ExecuteRequest_ColumnSet_AllColumns_BuildsAllColumnsSet()
    {
        var (result, svc) = Run("""{ "$type": "ColumnSet", "allColumns": true }""");

        Assert.Equal(0, result.ErrorCount);
        var cs = Assert.IsType<ColumnSet>(svc.CapturedRequest!["ColumnSet"]);
        Assert.True(cs.AllColumns);
        Assert.Empty(cs.Columns);
    }

    [Fact]
    public void ExecuteRequest_ColumnSet_TypeNameIsCaseInsensitive()
    {
        var (result, svc) = Run("""{ "$type": "columnset", "columns": ["quoteid"] }""");

        Assert.Equal(0, result.ErrorCount);
        var cs = Assert.IsType<ColumnSet>(svc.CapturedRequest!["ColumnSet"]);
        Assert.Equal(new[] { "quoteid" }, cs.Columns);
    }

    // Grauzone: allColumns=false mit Spaltenliste ist gültig und ergibt die Spaltenliste,
    // kein AllColumns-Set.
    [Fact]
    public void ExecuteRequest_ColumnSet_AllColumnsFalseWithColumns_UsesColumns()
    {
        var (result, svc) = Run("""{ "$type": "ColumnSet", "allColumns": false, "columns": ["quoteid"] }""");

        Assert.Equal(0, result.ErrorCount);
        var cs = Assert.IsType<ColumnSet>(svc.CapturedRequest!["ColumnSet"]);
        Assert.False(cs.AllColumns);
        Assert.Equal(new[] { "quoteid" }, cs.Columns);
    }

    [Theory]
    [InlineData("""{ "$type": "ColumnSet" }""")]
    [InlineData("""{ "$type": "ColumnSet", "columns": [] }""")]
    [InlineData("""{ "$type": "ColumnSet", "columns": "quoteid" }""")]
    [InlineData("""{ "$type": "ColumnSet", "columns": ["quoteid", ""] }""")]
    [InlineData("""{ "$type": "ColumnSet", "allColumns": true, "columns": ["quoteid"] }""")]
    [InlineData("""{ "$type": "ColumnSet", "allColumns": false }""")]
    public void ExecuteRequest_ColumnSet_InvalidShape_IsStepErrorWithoutRequest(string columnSetJson)
    {
        var (result, svc) = Run(columnSetJson);

        // Der Parameterbau wirft VOR dem Execute: Step-Error, kein Request abgesetzt.
        Assert.Equal(1, result.ErrorCount);
        Assert.Null(svc.CapturedRequest);
    }

    /// <summary>
    /// Minimaler IOrganizationService-Fake: Create vergibt Ids, Execute fängt nur die
    /// Testnachricht ab. Metadata-Abfragen werden vom EntityMetadataCache zu null gefangen.
    /// </summary>
    private sealed class RequestCaptureService : IOrganizationService
    {
        public Guid OpportunityId { get; private set; }
        public OrganizationRequest? CapturedRequest { get; private set; }

        public Guid Create(Entity entity)
        {
            var id = Guid.NewGuid();
            if (entity.LogicalName == "opportunity") OpportunityId = id;
            return id;
        }

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (request.RequestName == "GenerateQuoteFromOpportunity")
                CapturedRequest = request;
            return new OrganizationResponse();
        }

        public void Delete(string entityName, Guid id) { }
        public void Update(Entity entity) { }
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => new Entity(entityName, id);
        public EntityCollection RetrieveMultiple(QueryBase query) => new EntityCollection();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) { }
    }
}
