using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Pins the typed Entity identity contract used by homogeneous xMultiple requests.
/// </summary>
public class ExecuteRequestEntityIdentityTests
{
    private static readonly Guid Account1Id = Guid.Parse("00000000-0000-0000-0000-000000000101");
    private static readonly Guid Account2Id = Guid.Parse("00000000-0000-0000-0000-000000000102");

    private static TestCase LoadTestCase(string json)
    {
        var settings = new JsonSerializerSettings
        {
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore
        };
        return JsonConvert.DeserializeObject<TestCase>(json, settings)!;
    }

    [Fact]
    public void ExecuteRequest_CreateMultipleThenUpdateMultiple_ResolvesEntityRefsAndCollectionName()
    {
        const string json = """
        {
          "testId": "ENTITY-XMULTIPLE",
          "title": "Typed Entity identity for xMultiple",
          "steps": [
            { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "account1",
              "fields": { "name": "Account 1" } },
            { "stepNumber": 2, "action": "CreateRecord", "entity": "accounts", "alias": "account2",
              "fields": { "name": "Account 2" } },
            { "stepNumber": 3, "action": "ExecuteRequest", "requestName": "CreateMultiple",
              "fields": { "Targets": { "$type": "EntityCollection", "entities": [
                { "$type": "Entity", "entity": "account", "fields": { "name": "Bulk 1" } },
                { "$type": "Entity", "entity": "account", "fields": { "name": "Bulk 2" } }
              ] } } },
            { "stepNumber": 4, "action": "ExecuteRequest", "requestName": "UpdateMultiple",
              "fields": { "Targets": { "$type": "EntityCollection", "entities": [
                { "$type": "Entity", "entity": "account", "ref": "account1", "fields": { "name": "Updated 1" } },
                { "$type": "Entity", "entity": "account", "ref": "account2", "fields": { "name": "Updated 2" } }
              ] } } }
          ]
        }
        """;

        var service = new RecordingOrganizationService(Account1Id, Account2Id);
        var result = new TestRunner(service).RunAll(
            new List<TestCase> { LoadTestCase(json) });

        Assert.Equal(1, result.PassedCount);
        Assert.Equal(2, service.Requests.Count);

        var createTargets = Assert.IsType<EntityCollection>(service.Requests[0]["Targets"]);
        Assert.Equal("account", createTargets.EntityName);
        Assert.Equal(2, createTargets.Entities.Count);
        Assert.All(createTargets.Entities, entity => Assert.Equal(Guid.Empty, entity.Id));

        var updateTargets = Assert.IsType<EntityCollection>(service.Requests[1]["Targets"]);
        Assert.Equal("account", updateTargets.EntityName);
        Assert.Collection(updateTargets.Entities,
            entity =>
            {
                Assert.Equal(Account1Id, entity.Id);
                Assert.Equal("Updated 1", entity.GetAttributeValue<string>("name"));
            },
            entity =>
            {
                Assert.Equal(Account2Id, entity.Id);
                Assert.Equal("Updated 2", entity.GetAttributeValue<string>("name"));
            });
    }

    [Fact]
    public void ExecuteRequest_EntityWithFixedId_SetsEntityId()
    {
        const string json = """
        {
          "testId": "ENTITY-FIXED-ID",
          "title": "Typed Entity fixed identity",
          "steps": [
            { "stepNumber": 1, "action": "ExecuteRequest", "requestName": "UpdateMultiple",
              "fields": { "Targets": { "$type": "EntityCollection", "entities": [
                { "$type": "Entity", "entity": "account",
                  "id": "00000000-0000-0000-0000-000000000103",
                  "fields": { "name": "Updated" } }
              ] } } }
          ]
        }
        """;

        var service = new RecordingOrganizationService();
        var result = new TestRunner(service).RunAll(
            new List<TestCase> { LoadTestCase(json) });

        Assert.Equal(1, result.PassedCount);
        var targets = Assert.IsType<EntityCollection>(Assert.Single(service.Requests)["Targets"]);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000103"),
            Assert.Single(targets.Entities).Id);
    }

    [Fact]
    public void ExecuteRequest_EntityWithRefAndId_IsRejected()
    {
        const string json = """
        {
          "testId": "ENTITY-AMBIGUOUS-ID",
          "title": "Typed Entity ambiguous identity",
          "steps": [
            { "stepNumber": 1, "action": "CreateRecord", "entity": "accounts", "alias": "account1",
              "fields": { "name": "Account 1" } },
            { "stepNumber": 2, "action": "ExecuteRequest", "requestName": "UpdateMultiple",
              "fields": { "Targets": { "$type": "EntityCollection", "entities": [
                { "$type": "Entity", "entity": "account", "ref": "account1",
                  "id": "00000000-0000-0000-0000-000000000103", "fields": {} }
              ] } } }
          ]
        }
        """;

        var result = new TestRunner(new RecordingOrganizationService(Account1Id)).RunAll(
            new List<TestCase> { LoadTestCase(json) });

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("accepts either 'ref'", result.Results[0].ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteRequest_EntityCollectionWithMixedLogicalNames_IsRejected()
    {
        const string json = """
        {
          "testId": "ENTITY-MIXED-COLLECTION",
          "title": "Typed Entity mixed collection",
          "steps": [
            { "stepNumber": 1, "action": "ExecuteRequest", "requestName": "CreateMultiple",
              "fields": { "Targets": { "$type": "EntityCollection", "entities": [
                { "$type": "Entity", "entity": "account", "fields": {} },
                { "$type": "Entity", "entity": "contact", "fields": {} }
              ] } } }
          ]
        }
        """;

        var result = new TestRunner(new RecordingOrganizationService()).RunAll(
            new List<TestCase> { LoadTestCase(json) });

        Assert.Equal(1, result.ErrorCount);
        Assert.Contains("same logical name", result.Results[0].ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingOrganizationService : IOrganizationService
    {
        private readonly Queue<Guid> _createIds;

        public RecordingOrganizationService(params Guid[] createIds)
        {
            _createIds = new Queue<Guid>(createIds);
        }

        public List<OrganizationRequest> Requests { get; } = new();

        public Guid Create(Entity entity) => _createIds.Count > 0 ? _createIds.Dequeue() : Guid.NewGuid();

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (string.Equals(request.RequestName, "CreateMultiple", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.RequestName, "UpdateMultiple", StringComparison.OrdinalIgnoreCase))
                Requests.Add(request);
            return new OrganizationResponse();
        }

        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship,
            EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship,
            EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) =>
            throw new NotImplementedException();
        public EntityCollection RetrieveMultiple(QueryBase query) => new();
    }
}
