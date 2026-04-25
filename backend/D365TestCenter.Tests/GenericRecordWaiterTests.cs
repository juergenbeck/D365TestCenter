using D365TestCenter.Core;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer GenericRecordWaiter.BuildQuery — insbesondere die orderBy-
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

        Assert.Contains("Ungueltige Sortierrichtung", ex.Message);
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
    //  Kombination + Kompatibilitaet
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
        // muessen unveraendert funktionieren — kein Order, kein TopCount.
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
    //  GUID-foermige Strings auf String/Memo-Feldern bleiben Strings,
    //  statt zu Guid konvertiert zu werden (was nie matchen wuerde).
    // ================================================================

    [Fact]
    public void Filter_StringFieldWithGuidValue_StaysAsString()
    {
        // markant_recordid ist ein String-Feld, das GUIDs als Strings speichert.
        // Ohne FB-32-Fix wuerde der Wert zu Guid konvertiert und der Filter
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
        // wuerde der Lookup-Filter nicht matchen.
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
        // Wenn der Cache fuer das Feld keinen Typ kennt, faellt die Conversion
        // auf den Legacy-Pfad zurueck (Guid-first).
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
}
