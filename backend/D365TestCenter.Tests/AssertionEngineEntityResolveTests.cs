using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Block 1 C v5.3.11: AssertionEngine.ResolveEntity-Symmetrie-Fix.
///
/// Bug (Markant T3, 2026-05-18): EvaluateQueryAssertion und
/// EvaluateGenericRecordAssertion reichen assertion.Entity ungeprüft an
/// GenericRecordWaiter.BuildQuery bzw. service.Retrieve weiter. Wenn der
/// Test-Autor den EntitySetName (Plural) statt LogicalName (Singular) im
/// JSON schreibt:
///
///   - EntityMetadataCache.GetMetadata("Plural") schlägt fehl (Plattform-
///     RetrieveEntityRequest wirft "Entity does not exist") und liefert
///     null. Damit fällt der FB-32-metadata-aware-Pfad in
///     GenericRecordWaiter.ConvertString in den Legacy-Branch, der
///     GUID-förmige Strings auf String-Feldern fälschlich zu Guid
///     konvertiert (FB-32-Lücke greift wieder).
///   - service.Retrieve("Plural", ...) wirft "Entity does not exist".
///
/// TestRunner.ResolveEntity (Zeile 713-718) ruft konsistent
/// EntityMetadataCache.ResolveLogicalName auf und löst Plural transparent
/// nach Singular auf. AssertionEngine hatte diese Symmetrie nicht.
///
/// Fix: AssertionEngine.ResolveEntityName(entityName) ruft
/// _metadataCache.ResolveLogicalName(entityName) wenn ein Cache da ist,
/// und gibt sonst entityName unverändert zurück (Backward-Compat für den
/// Default-Konstruktor ohne Cache).
/// </summary>
public class AssertionEngineEntityResolveTests
{
    // ════════════════════════════════════════════════════════════════
    //  RecordingService: zeichnet jeden Query/Retrieve-Call auf,
    //  liefert leere Responses zurück.
    // ════════════════════════════════════════════════════════════════

    private sealed class RecordingService : IOrganizationService
    {
        public List<string> RetrieveMultipleEntityNames { get; } = new();
        public List<(string entityName, Guid id)> RetrieveCalls { get; } = new();

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            if (query is QueryExpression qe)
                RetrieveMultipleEntityNames.Add(qe.EntityName);
            return new EntityCollection();
        }

        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            RetrieveCalls.Add((entityName, id));
            return new Entity(entityName, id);
        }

        public Guid Create(Entity entity) => Guid.NewGuid();
        public void Update(Entity entity) { }
        public void Delete(string entityName, Guid id) { }
        public OrganizationResponse Execute(OrganizationRequest request) =>
            new OrganizationResponse();
        public void Associate(string entityName, Guid entityId, Relationship rel, EntityReferenceCollection r) { }
        public void Disassociate(string entityName, Guid entityId, Relationship rel, EntityReferenceCollection r) { }
    }

    private static EntityMetadataCache CacheWith(
        string singularLogicalName, params string[] attributes)
    {
        var seed = new Dictionary<string, Dictionary<string, AttributeTypeCode>>
        {
            [singularLogicalName] = new Dictionary<string, AttributeTypeCode>()
        };
        foreach (var attr in attributes)
            seed[singularLogicalName][attr] = AttributeTypeCode.String;
        return EntityMetadataCache.CreateForTesting(seed);
    }

    // ════════════════════════════════════════════════════════════════
    //  Bug-Pin: Plural-EntitySetName muss zu Singular aufgelöst werden.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void QueryAssertion_PluralEntityName_WithCache_ResolvesToSingular()
    {
        var svc = new RecordingService();
        var cache = CacheWith("markant_gdprpseudonymmap", "markant_originalvalue");
        var engine = new AssertionEngine(cache);

        var assertion = new TestAssertion
        {
            Target = "Query",
            Entity = "markant_gdprpseudonymmaps",  // Plural-EntitySetName
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "markant_originalvalue", Operator = "eq", Value = "abc" }
            },
            Operator = "Exists"
        };

        engine.Evaluate(assertion, new TestContext(), svc);

        Assert.Single(svc.RetrieveMultipleEntityNames);
        Assert.Equal("markant_gdprpseudonymmap", svc.RetrieveMultipleEntityNames[0]);
    }

    [Fact]
    public void QueryAssertion_StandardEntitySetName_AccountsResolvedToAccount()
    {
        var svc = new RecordingService();
        var cache = new EntityMetadataCache(svc);  // KnownEntitySetNames-Map greift
        var engine = new AssertionEngine(cache);

        var assertion = new TestAssertion
        {
            Target = "Query",
            Entity = "accounts",  // Standard-Plural
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "name", Operator = "eq", Value = "Foo" }
            },
            Operator = "Exists"
        };

        engine.Evaluate(assertion, new TestContext(), svc);

        Assert.Equal("account", svc.RetrieveMultipleEntityNames[0]);
    }

    // ════════════════════════════════════════════════════════════════
    //  Regress: bereits korrekt verwendete Singular-Form bleibt erhalten.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void QueryAssertion_SingularEntityName_WithCache_StaysSingular()
    {
        var svc = new RecordingService();
        var cache = CacheWith("markant_gdprpseudonymmap", "markant_field");
        var engine = new AssertionEngine(cache);

        var assertion = new TestAssertion
        {
            Target = "Query",
            Entity = "markant_gdprpseudonymmap",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "markant_field", Operator = "eq", Value = "x" }
            },
            Operator = "Exists"
        };

        engine.Evaluate(assertion, new TestContext(), svc);

        Assert.Equal("markant_gdprpseudonymmap", svc.RetrieveMultipleEntityNames[0]);
    }

    // ════════════════════════════════════════════════════════════════
    //  Backward-Compat: Default-Konstruktor ohne Cache reicht entityName
    //  unverändert weiter (ohne Cache läuft sowieso der Legacy-Pfad).
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void QueryAssertion_WithoutCache_EntityNameUnchanged()
    {
        var svc = new RecordingService();
        var engine = new AssertionEngine();  // ohne MetadataCache

        var assertion = new TestAssertion
        {
            Target = "Query",
            Entity = "markant_gdprpseudonymmaps",
            Filter = new List<FilterCondition>
            {
                new FilterCondition { Field = "markant_field", Operator = "eq", Value = "x" }
            },
            Operator = "Exists"
        };

        engine.Evaluate(assertion, new TestContext(), svc);

        Assert.Equal("markant_gdprpseudonymmaps", svc.RetrieveMultipleEntityNames[0]);
    }

    // ════════════════════════════════════════════════════════════════
    //  Record-Assertion: explizites assertion.Entity = Plural muss
    //  beim service.Retrieve als Singular ankommen.
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void RecordAssertion_ExplicitPluralEntity_WithCache_ResolvesToSingular()
    {
        var svc = new RecordingService();
        var cache = CacheWith("markant_gdprpseudonymmap", "markant_field");
        var engine = new AssertionEngine(cache);

        var ctx = new TestContext();
        var id = Guid.NewGuid();
        ctx.RegisterRecord("alias1", "markant_gdprpseudonymmap", id);

        var assertion = new TestAssertion
        {
            Target = "Record",
            RecordRef = "alias1",
            Entity = "markant_gdprpseudonymmaps",  // Plural explizit gesetzt
            Field = "markant_field",
            Operator = "IsNotNull"
        };

        engine.Evaluate(assertion, ctx, svc);

        Assert.Single(svc.RetrieveCalls);
        Assert.Equal("markant_gdprpseudonymmap", svc.RetrieveCalls[0].entityName);
    }
}
