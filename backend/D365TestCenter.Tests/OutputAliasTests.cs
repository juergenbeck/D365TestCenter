using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests fuer A4 — ExecuteRequest-Output als Alias verfuegbar machen.
/// Siehe ZastrPay-Feedback v1 Anforderung A4.
///
/// Pattern: {alias.outputs.X} und {alias.outputs.X[type=Y]} fuer
/// EntityReferenceCollection-Filter.
/// </summary>
public class OutputAliasTests
{
    [Fact]
    public void TestStep_OutputAlias_DeserializesFromJson()
    {
        const string json = """
        {
            "stepNumber": 2,
            "action": "ExecuteRequest",
            "requestName": "QualifyLead",
            "outputAlias": "qresult"
        }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Equal("qresult", step!.OutputAlias);
    }

    [Fact]
    public void TestStep_NoOutputAlias_StaysNull()
    {
        const string json = """
        { "action": "CreateRecord", "entity": "accounts", "fields": { "name": "Test" } }
        """;

        var step = JsonConvert.DeserializeObject<TestStep>(json);

        Assert.NotNull(step);
        Assert.Null(step!.OutputAlias);
    }

    [Fact]
    public void TestContext_OutputAliases_IsEmpty()
    {
        var ctx = new TestContext();

        Assert.NotNull(ctx.OutputAliases);
        Assert.Empty(ctx.OutputAliases);
    }

    [Fact]
    public void Resolve_SimpleStringOutput_ReturnsValue()
    {
        var ctx = new TestContext();
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["AccountId"] = "abc-123"
        };

        var engine = new PlaceholderEngine();
        var result = engine.Resolve("Account: {qres.outputs.AccountId}", ctx);

        Assert.Equal("Account: abc-123", result);
    }

    [Fact]
    public void Resolve_GuidOutput_ReturnsGuidString()
    {
        var ctx = new TestContext();
        var guid = Guid.NewGuid();
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["Id"] = guid
        };

        var engine = new PlaceholderEngine();
        var result = engine.Resolve("{qres.outputs.Id}", ctx);

        Assert.Equal(guid.ToString(), result);
    }

    [Fact]
    public void Resolve_EntityReferenceOutput_ReturnsId()
    {
        var ctx = new TestContext();
        var er = new EntityReference("account", Guid.NewGuid());
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["Account"] = er
        };

        var engine = new PlaceholderEngine();
        var result = engine.Resolve("{qres.outputs.Account}", ctx);

        Assert.Equal(er.Id.ToString(), result);
    }

    [Fact]
    public void Resolve_EntityReferenceCollectionWithTypeFilter_ReturnsMatchingId()
    {
        var ctx = new TestContext();
        var accountId = Guid.NewGuid();
        var contactId = Guid.NewGuid();
        var coll = new EntityReferenceCollection
        {
            new EntityReference("account", accountId),
            new EntityReference("contact", contactId)
        };
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["CreatedEntityReferences"] = coll
        };

        var engine = new PlaceholderEngine();
        var resAcc = engine.Resolve("{qres.outputs.CreatedEntityReferences[type=account]}", ctx);
        var resCon = engine.Resolve("{qres.outputs.CreatedEntityReferences[type=contact]}", ctx);

        Assert.Equal(accountId.ToString(), resAcc);
        Assert.Equal(contactId.ToString(), resCon);
    }

    [Fact]
    public void Resolve_EntityReferenceCollectionWithUnknownType_Throws()
    {
        var ctx = new TestContext();
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["CreatedEntityReferences"] = new EntityReferenceCollection
            {
                new EntityReference("account", Guid.NewGuid())
            }
        };

        var engine = new PlaceholderEngine();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Resolve("{qres.outputs.CreatedEntityReferences[type=lead]}", ctx));

        Assert.Contains("type='lead'", ex.Message);
    }

    [Fact]
    public void Resolve_TypeFilterOnNonCollection_Throws()
    {
        var ctx = new TestContext();
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["Single"] = new EntityReference("account", Guid.NewGuid())
        };

        var engine = new PlaceholderEngine();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Resolve("{qres.outputs.Single[type=account]}", ctx));

        Assert.Contains("nur bei EntityReferenceCollection", ex.Message);
    }

    [Fact]
    public void Resolve_UnknownAlias_LeavesPlaceholderUnchanged()
    {
        // Wenn der outputAlias nicht im ctx liegt, soll der Platzhalter
        // unveraendert bleiben — analog zum {alias.id}-Verhalten. Der Test
        // bricht spaeter im Step ab, nicht hier.
        var ctx = new TestContext();
        var engine = new PlaceholderEngine();

        var result = engine.Resolve("Bind: {missing.outputs.X}", ctx);

        Assert.Equal("Bind: {missing.outputs.X}", result);
    }

    [Fact]
    public void Resolve_KnownAliasUnknownKey_Throws()
    {
        var ctx = new TestContext();
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["AccountId"] = "abc-123"
        };

        var engine = new PlaceholderEngine();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            engine.Resolve("{qres.outputs.WrongKey}", ctx));

        Assert.Contains("WrongKey", ex.Message);
        Assert.Contains("AccountId", ex.Message);
    }

    [Fact]
    public void Resolve_OptionSetValueOutput_ReturnsValueAsString()
    {
        var ctx = new TestContext();
        ctx.OutputAliases["qres"] = new Dictionary<string, object?>
        {
            ["StatusCode"] = new OptionSetValue(105710001)
        };

        var engine = new PlaceholderEngine();
        var result = engine.Resolve("{qres.outputs.StatusCode}", ctx);

        Assert.Equal("105710001", result);
    }
}
