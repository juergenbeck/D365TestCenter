using D365TestCenter.Core;
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
