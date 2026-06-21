using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für GenericRecordWaiter.BuildQuery — insbesondere die orderBy-
/// und top-Erweiterungen (1c Option A aus
/// D365TestCenter-Workspace/03_implementation/findrecord-orderby-fetchxml.md).
/// </summary>
public class GenericRecordWaiterTests
{
    // ================================================================
    //  orderBy
    // ================================================================

    [Fact]
    public void OrderBy_SingleFieldAsc_AddsAscendingOrder()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "modifiedon asc");

        Assert.Single(q.Orders);
        Assert.Equal("modifiedon", q.Orders[0].AttributeName);
        Assert.Equal(OrderType.Ascending, q.Orders[0].OrderType);
    }

    [Fact]
    public void OrderBy_SingleFieldDesc_AddsDescendingOrder()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "modifiedon desc");

        Assert.Single(q.Orders);
        Assert.Equal(OrderType.Descending, q.Orders[0].OrderType);
    }

    [Fact]
    public void OrderBy_FieldWithoutDirection_DefaultsToAsc()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "modifiedon");

        Assert.Single(q.Orders);
        Assert.Equal(OrderType.Ascending, q.Orders[0].OrderType);
    }

    [Fact]
    public void OrderBy_MultipleFields_AddsOrdersInSequence()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "statecode asc, modifiedon desc");

        Assert.Equal(2, q.Orders.Count);
        Assert.Equal("statecode", q.Orders[0].AttributeName);
        Assert.Equal(OrderType.Ascending, q.Orders[0].OrderType);
        Assert.Equal("modifiedon", q.Orders[1].AttributeName);
        Assert.Equal(OrderType.Descending, q.Orders[1].OrderType);
    }

    [Fact]
    public void OrderBy_Null_NoOrdersAdded()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: null);

        Assert.Empty(q.Orders);
    }

    [Fact]
    public void OrderBy_EmptyString_NoOrdersAdded()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "");

        Assert.Empty(q.Orders);
    }

    [Fact]
    public void OrderBy_InvalidDirection_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GenericRecordWaiter.BuildQuery(
                "contact", new List<FilterCondition>(), null,
                orderBy: "modifiedon sideways"));

        Assert.Contains("Ungültige Sortierrichtung", ex.Message);
    }

    [Fact]
    public void OrderBy_TrailingCommaIgnored()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "modifiedon desc,");

        Assert.Single(q.Orders);
    }

    [Fact]
    public void OrderBy_CaseInsensitiveDirection()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "modifiedon DESC, createdon AsC");

        Assert.Equal(OrderType.Descending, q.Orders[0].OrderType);
        Assert.Equal(OrderType.Ascending, q.Orders[1].OrderType);
    }

    // ================================================================
    //  top
    // ================================================================

    [Fact]
    public void Top_NotSet_LeavesTopCountUnset()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null);

        Assert.Null(q.TopCount);
    }

    [Fact]
    public void Top_SetExplicitly_AppliedOnQuery()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            topCount: 5);

        Assert.Equal(5, q.TopCount);
    }

    [Fact]
    public void Top_One_AppliedOnQuery()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            topCount: 1);

        Assert.Equal(1, q.TopCount);
    }

    // ================================================================
    //  Kombination + Kompatibilität
    // ================================================================

    [Fact]
    public void OrderByPlusTop_BothApplied()
    {
        var q = GenericRecordWaiter.BuildQuery(
            "contact", new List<FilterCondition>(), null,
            orderBy: "createdon desc",
            topCount: 1);

        Assert.Single(q.Orders);
        Assert.Equal(OrderType.Descending, q.Orders[0].OrderType);
        Assert.Equal(1, q.TopCount);
    }

    [Fact]
    public void OriginalSignature_NoOrderByNoTop_StaysBackwardCompatible()
    {
        // Bestehende Aufrufe (z.B. AssertionEngine, die TopCount separat setzt)
        // müssen unverändert funktionieren — kein Order, kein TopCount.
        var q = GenericRecordWaiter.BuildQuery(
            "contact",
            new List<FilterCondition>
            {
                new() { Field = "statecode", Operator = "eq", Value = 1 }
            },
            null);

        Assert.Empty(q.Orders);
        Assert.Null(q.TopCount);
        Assert.Single(q.Criteria.Conditions);
    }

    // ================================================================
    //  JSON-Deserialisierung der TestStep-Felder
    // ================================================================

    // ================================================================
    //  FB-32: Metadata-aware ConvertString
    //  GUID-förmige Strings auf String/Memo-Feldern bleiben Strings,
    //  statt zu Guid konvertiert zu werden (was nie matchen würde).
    // ================================================================

    [Fact]
    public void Filter_StringFieldWithGuidValue_StaysAsString()
    {
        // markant_recordid ist ein String-Feld, das GUIDs als Strings speichert.
        // Ohne FB-32-Fix würde der Wert zu Guid konvertiert und der Filter
        // matcht nichts (Dataverse vergleicht Guid gegen String-Feld).
        var cache = EntityMetadataCache.CreateForTesting(
            new Dictionary<string, Dictionary<string, AttributeTypeCode>>
            {
                ["markant_gdprpseudonymmap"] = new()
                {
                    ["markant_recordid"] = AttributeTypeCode.String
                }
            });

        var filters = new List<FilterCondition>
        {
            new() { Field = "markant_recordid", Operator = "eq",
                    Value = "12345678-1234-1234-1234-123456789012" }
        };

        var query = GenericRecordWaiter.BuildQuery(
            "markant_gdprpseudonymmap", filters, null,
            metadataCache: cache);

        Assert.Single(query.Criteria.Conditions);
        var cond = query.Criteria.Conditions[0];
        var value = cond.Values[0];
        Assert.IsType<string>(value);
        Assert.Equal("12345678-1234-1234-1234-123456789012", (string)value);
    }

    [Fact]
    public void Filter_LookupFieldWithGuidValue_ConvertsToGuid()
    {
        // Lookup-Felder sollen weiterhin auf Guid konvertiert werden — sonst
        // würde der Lookup-Filter nicht matchen.
        var cache = EntityMetadataCache.CreateForTesting(
            new Dictionary<string, Dictionary<string, AttributeTypeCode>>
            {
                ["contact"] = new()
                {
                    ["parentcustomerid"] = AttributeTypeCode.Lookup
                }
            });

        var filters = new List<FilterCondition>
        {
            new() { Field = "parentcustomerid", Operator = "eq",
                    Value = "12345678-1234-1234-1234-123456789012" }
        };

        var query = GenericRecordWaiter.BuildQuery(
            "contact", filters, null, metadataCache: cache);

        var cond = query.Criteria.Conditions[0];
        Assert.IsType<Guid>(cond.Values[0]);
        Assert.Equal(Guid.Parse("12345678-1234-1234-1234-123456789012"), (Guid)cond.Values[0]);
    }

    [Fact]
    public void Filter_NoMetadataCache_LegacyAutoConversion()
    {
        // Backward-Compatibility: ohne Cache wird wie bisher zu Guid
        // konvertiert (Legacy-Verhalten — vor FB-32-Fix).
        var filters = new List<FilterCondition>
        {
            new() { Field = "any_field", Operator = "eq",
                    Value = "12345678-1234-1234-1234-123456789012" }
        };

        var query = GenericRecordWaiter.BuildQuery(
            "any_entity", filters, null);

        var cond = query.Criteria.Conditions[0];
        Assert.IsType<Guid>(cond.Values[0]);
    }

    [Fact]
    public void Filter_StringFieldWithIntValue_StaysAsString()
    {
        // String-Feld + Zahl-String: bleibt String (auch hier muss die
        // Auto-Conversion respektieren dass das Feld ein String ist).
        var cache = EntityMetadataCache.CreateForTesting(
            new Dictionary<string, Dictionary<string, AttributeTypeCode>>
            {
                ["myentity"] = new() { ["myfield"] = AttributeTypeCode.String }
            });

        var filters = new List<FilterCondition>
        {
            new() { Field = "myfield", Operator = "eq", Value = "42" }
        };

        var query = GenericRecordWaiter.BuildQuery(
            "myentity", filters, null, metadataCache: cache);

        Assert.IsType<string>(query.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public void Filter_PicklistFieldWithIntValue_ConvertsToInt()
    {
        // Picklist/State/Status: Zahl-Strings werden zu int (als rohe int-
        // Filter-Bedingung; OptionSetValue-Wrapping macht Dataverse intern).
        var cache = EntityMetadataCache.CreateForTesting(
            new Dictionary<string, Dictionary<string, AttributeTypeCode>>
            {
                ["contact"] = new() { ["statecode"] = AttributeTypeCode.State }
            });

        var filters = new List<FilterCondition>
        {
            new() { Field = "statecode", Operator = "eq", Value = "1" }
        };

        var query = GenericRecordWaiter.BuildQuery(
            "contact", filters, null, metadataCache: cache);

        Assert.IsType<int>(query.Criteria.Conditions[0].Values[0]);
        Assert.Equal(1, (int)query.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public void Filter_UnknownFieldInMetadata_FallsBackToLegacy()
    {
        // Wenn der Cache für das Feld keinen Typ kennt, fällt die Conversion
        // auf den Legacy-Pfad zurück (Guid-first).
        var cache = EntityMetadataCache.CreateForTesting(
            new Dictionary<string, Dictionary<string, AttributeTypeCode>>
            {
                ["contact"] = new() { ["other_field"] = AttributeTypeCode.String }
            });

        var filters = new List<FilterCondition>
        {
            new() { Field = "unbekannt", Operator = "eq",
                    Value = "12345678-1234-1234-1234-123456789012" }
        };

        var query = GenericRecordWaiter.BuildQuery(
            "contact", filters, null, metadataCache: cache);

        // Legacy-Verhalten: Guid.TryParse erfolgreich, also Guid.
        Assert.IsType<Guid>(query.Criteria.Conditions[0].Values[0]);
    }

    [Fact]
    public void TestStep_OrderByAndTop_DeserializeFromJson()
    {
        const string json = """
        {
            "stepNumber": 1,
            "action": "WaitForRecord",
            "entity": "systemusers",
            "filter": [{"field":"statecode","operator":"eq","value":1}],
            "orderBy": "modifiedon asc",
            "top": 1,
            "alias": "oldestDisabled"
        }
        """;

        var step = Newtonsoft.Json.JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("modifiedon asc", step!.OrderBy);
        Assert.Equal(1, step.Top);
    }

    // ================================================================
    //  WaitForRecordAbsence (WaitForNotExists)
    // ================================================================

    [Fact]
    public void WaitForRecordAbsence_RecordAlreadyGone_ReturnsTrueOnFirstPoll()
    {
        var svc = new FakeCountService(0); // query yields zero matches immediately
        var ok = new GenericRecordWaiter().WaitForRecordAbsence(
            svc, "contact", new List<FilterCondition>(),
            timeoutSeconds: 5, pollingIntervalMs: 5);

        Assert.True(ok);
        Assert.Equal(1, svc.RetrieveMultipleCalls);
    }

    [Fact]
    public void WaitForRecordAbsence_DisappearsAfterPolls_ReturnsTrue()
    {
        // present on the first two polls, gone on the third (async delete completes)
        var svc = new FakeCountService(1, 1, 0);
        var ok = new GenericRecordWaiter().WaitForRecordAbsence(
            svc, "contact", new List<FilterCondition>(),
            timeoutSeconds: 5, pollingIntervalMs: 5);

        Assert.True(ok);
        Assert.Equal(3, svc.RetrieveMultipleCalls);
    }

    [Fact]
    public void WaitForRecordAbsence_RecordStays_TimesOutFalse()
    {
        // record never disappears -> poll until the (short) timeout, return false
        var svc = new FakeCountService(1); // sticky: always one match
        var ok = new GenericRecordWaiter().WaitForRecordAbsence(
            svc, "contact", new List<FilterCondition>(),
            timeoutSeconds: 1, pollingIntervalMs: 50);

        Assert.False(ok);
        Assert.True(svc.RetrieveMultipleCalls >= 1);
    }

    [Fact]
    public void WaitForRecordAbsence_UsesMinimalColumnSet()
    {
        // Count-only check: the absence waiter must not over-fetch all columns.
        var svc = new FakeCountService(0);
        new GenericRecordWaiter().WaitForRecordAbsence(
            svc, "contact", new List<FilterCondition>(),
            timeoutSeconds: 5, pollingIntervalMs: 5);

        Assert.NotNull(svc.LastQuery);
        Assert.False(svc.LastQuery!.ColumnSet.AllColumns);
        Assert.Empty(svc.LastQuery.ColumnSet.Columns);
    }

    [Fact]
    public void WaitForRecordAbsence_PassesFilterIntoQuery()
    {
        var svc = new FakeCountService(0);
        var filters = new List<FilterCondition>
        {
            new() { Field = "lastname", Operator = "eq", Value = "Composite Address" }
        };
        new GenericRecordWaiter().WaitForRecordAbsence(
            svc, "contact", filters, timeoutSeconds: 5, pollingIntervalMs: 5);

        Assert.NotNull(svc.LastQuery);
        Assert.Equal("contact", svc.LastQuery!.EntityName);
        Assert.Single(svc.LastQuery.Criteria.Conditions);
        Assert.Equal("lastname", svc.LastQuery.Criteria.Conditions[0].AttributeName);
    }

    /// <summary>
    /// IOrganizationService double for the absence-waiter: RetrieveMultiple returns
    /// a configurable sequence of match-counts. When the queue is exhausted it repeats
    /// the last value (sticky), so a single-element ctor models a record that never
    /// disappears. Captures the last QueryExpression for column-set assertions.
    /// </summary>
    private sealed class FakeCountService : IOrganizationService
    {
        private readonly Queue<int> _counts;
        private readonly int _last;
        public int RetrieveMultipleCalls { get; private set; }
        public QueryExpression? LastQuery { get; private set; }

        public FakeCountService(params int[] counts)
        {
            _counts = new Queue<int>(counts);
            _last = counts.Length > 0 ? counts[counts.Length - 1] : 0;
        }

        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            RetrieveMultipleCalls++;
            LastQuery = query as QueryExpression;
            var count = _counts.Count > 0 ? _counts.Dequeue() : _last;
            var ec = new EntityCollection();
            for (var i = 0; i < count; i++)
                ec.Entities.Add(new Entity("contact", Guid.NewGuid()));
            return ec;
        }

        public OrganizationResponse Execute(OrganizationRequest request) => throw new NotImplementedException();
        public Guid Create(Entity entity) => throw new NotImplementedException();
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => throw new NotImplementedException();
        public void Update(Entity entity) => throw new NotImplementedException();
        public void Delete(string entityName, Guid id) => throw new NotImplementedException();
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) => throw new NotImplementedException();
    }
}
