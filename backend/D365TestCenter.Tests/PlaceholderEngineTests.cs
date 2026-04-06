using D365TestCenter.Core;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für die PlaceholderEngine.
/// Prüft alle unterstützten Platzhalter-Typen:
/// {TESTID}, {PREFIX}, {TIMESTAMP}, {GUID}, {NOW_UTC},
/// {GENERATED:name}, {RECORD:alias}, {alias.id}, {alias.fields.field},
/// {RESULT:alias.field}, {ROW:field}.
/// </summary>
public class PlaceholderEngineTests
{
    private readonly PlaceholderEngine _engine = new();

    private static TestContext CreateContext(string testId = "TEST01")
    {
        return new TestContext { TestId = testId };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Statische Platzhalter
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_TestId_ReplacesCorrectly()
    {
        var ctx = CreateContext("TC42");
        var result = _engine.Resolve("test={TESTID}", ctx);
        Assert.Equal("test=TC42", result);
    }

    [Fact]
    public void Resolve_Prefix_ReplacesCorrectly()
    {
        var ctx = CreateContext();
        var result = _engine.Resolve("{PREFIX}_test", ctx);
        Assert.Equal("ITT_test", result);
    }

    [Fact]
    public void Resolve_Timestamp_ProducesNonEmptyValue()
    {
        var ctx = CreateContext();
        var result = _engine.Resolve("{TIMESTAMP}", ctx);
        Assert.NotEmpty(result);
        Assert.DoesNotContain("{TIMESTAMP}", result);
    }

    [Fact]
    public void Resolve_Guid_Produces8CharHex()
    {
        var ctx = CreateContext();
        var result = _engine.Resolve("{GUID}", ctx);
        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void Resolve_NowUtc_ProducesIso8601()
    {
        var ctx = CreateContext();
        var result = _engine.Resolve("{NOW_UTC}", ctx);
        Assert.Contains("T", result);
        Assert.True(DateTimeOffset.TryParse(result, out _));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  {GENERATED:name} - generierte und wiederverwendete Werte
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_Generated_SameNameReturnsSameValue()
    {
        var ctx = CreateContext();
        var first = _engine.Resolve("{GENERATED:myId}", ctx);
        var second = _engine.Resolve("{GENERATED:myId}", ctx);
        Assert.Equal(first, second);
        Assert.Equal(8, first.Length);
    }

    [Fact]
    public void Resolve_Generated_DifferentNamesReturnDifferentValues()
    {
        var ctx = CreateContext();
        var a = _engine.Resolve("{GENERATED:idA}", ctx);
        var b = _engine.Resolve("{GENERATED:idB}", ctx);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_Generated_ReusableInAssertionValue()
    {
        var ctx = CreateContext();
        var stepValue = _engine.Resolve("Test_{GENERATED:extId}", ctx);
        var assertionValue = _engine.Resolve("Test_{GENERATED:extId}", ctx);
        Assert.Equal(stepValue, assertionValue);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  {RECORD:alias} - Record-Registry
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_RecordAlias_FromRecordsRegistry()
    {
        var ctx = CreateContext();
        var recordId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        ctx.Records["bridge1"] = ("markant_bridge_pf_record", recordId);

        var result = _engine.Resolve("{RECORD:bridge1}", ctx);
        Assert.Equal("44444444-4444-4444-4444-444444444444", result);
    }

    [Fact]
    public void Resolve_RecordAlias_UnknownAlias_KeepsPlaceholder()
    {
        var ctx = CreateContext();
        var result = _engine.Resolve("{RECORD:unbekannt}", ctx);
        Assert.Equal("{RECORD:unbekannt}", result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  {alias.id} und {alias.fields.field} - Web-Resource-Format
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_AliasId_ReplacesFromRegistry()
    {
        var ctx = CreateContext();
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ctx.Records["cs1"] = ("contact", id);

        var result = _engine.Resolve("{cs1.id}", ctx);
        Assert.Equal("11111111-1111-1111-1111-111111111111", result);
    }

    [Fact]
    public void Resolve_AliasFields_ReplacesFromFoundRecords()
    {
        var ctx = CreateContext();
        var entity = new Entity("contact", Guid.NewGuid());
        entity["firstname"] = "MaxTest";
        ctx.Records["cs1"] = ("contact", entity.Id);
        ctx.FoundRecords["cs1"] = entity;

        var result = _engine.Resolve("{cs1.fields.firstname}", ctx);
        Assert.Equal("MaxTest", result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  {RESULT:alias.field} - Feldwert aus FoundRecords
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_ResultAlias_ExtractsFieldValue()
    {
        var ctx = CreateContext();
        var entity = new Entity("markant_fg_contactsource", Guid.NewGuid());
        entity["markant_firstname"] = "MaxTest";
        ctx.FoundRecords["foundCS"] = entity;

        var result = _engine.Resolve("{RESULT:foundCS.markant_firstname}", ctx);
        Assert.Equal("MaxTest", result);
    }

    [Fact]
    public void Resolve_ResultAlias_UnknownAlias_KeepsPlaceholder()
    {
        var ctx = CreateContext();
        var result = _engine.Resolve("{RESULT:unknown.field}", ctx);
        Assert.Equal("{RESULT:unknown.field}", result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  {ROW:field} - Datengetriebene Tests
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_Row_ReplacesFromDataRow()
    {
        var ctx = CreateContext();
        ctx.CurrentDataRow = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceCode"] = 4,
            ["name"] = "PisaTest"
        };

        var result = _engine.Resolve("Source={ROW:sourceCode}, Name={ROW:name}", ctx);
        Assert.Equal("Source=4, Name=PisaTest", result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ResolveAll - Dictionary-Auflösung
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveAll_ReplacesAllStringValues()
    {
        var ctx = CreateContext("TC99");
        var fields = new Dictionary<string, object?>
        {
            ["markant_firstname"] = "Test_{TESTID}",
            ["markant_status"] = 1,
            ["markant_externalid"] = "{GENERATED:ext}"
        };

        var resolved = _engine.ResolveAll(fields, ctx);

        Assert.Equal("Test_TC99", resolved["markant_firstname"]);
        Assert.Equal(1, resolved["markant_status"]); // int bleibt unverändert
        Assert.Equal(8, ((string)resolved["markant_externalid"]!).Length);
    }

    [Fact]
    public void ResolveAll_EmptyDictionary_ReturnsEmpty()
    {
        var ctx = CreateContext();
        var resolved = _engine.ResolveAll(new Dictionary<string, object?>(), ctx);
        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveAll_NullDictionary_ReturnsEmpty()
    {
        var ctx = CreateContext();
        var resolved = _engine.ResolveAll(null!, ctx);
        Assert.Empty(resolved);
    }
}
