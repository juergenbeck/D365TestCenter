using D365TestCenter.Core;
using Newtonsoft.Json;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// Tests für TestDataFactory.ResolveTemplateData.
/// Die Factory delegiert an PlaceholderEngine.ResolveAll.
/// Einzelne Platzhalter-Typen werden in PlaceholderEngineTests geprüft.
/// </summary>
public class TestDataFactoryTests
{
    private readonly TestDataFactory _factory = new();

    [Fact]
    public void ResolveTemplateData_ResolvesStringValues()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var data = new Dictionary<string, object?>
        {
            ["firstname"] = "{PREFIX}_{TESTID}_First",
            ["count"] = 42
        };

        var resolved = _factory.ResolveTemplateData(data, ctx);

        Assert.Equal("ITT_TC01_First", resolved["firstname"]);
        Assert.Equal(42, resolved["count"]);
    }

    [Fact]
    public void ResolveTemplateData_ResolvesJsonElementStrings()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var json = JsonConvert.DeserializeObject<Dictionary<string, object?>>(
            """{"firstname": "{PREFIX}_Name"}""");

        var resolved = _factory.ResolveTemplateData(json!, ctx);

        Assert.Equal("ITT_Name", resolved["firstname"]);
    }

    [Fact]
    public void ResolveTemplateData_NullInput_ReturnsEmptyDict()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var resolved = _factory.ResolveTemplateData(null!, ctx);

        Assert.NotNull(resolved);
        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveTemplateData_Timestamp_IsReplaced()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var data = new Dictionary<string, object?>
        {
            ["externalId"] = "ITT_{TIMESTAMP}"
        };

        var resolved = _factory.ResolveTemplateData(data, ctx);

        Assert.DoesNotContain("{TIMESTAMP}", (string)resolved["externalId"]!);
        Assert.StartsWith("ITT_", (string)resolved["externalId"]!);
    }

    [Fact]
    public void ResolveTemplateData_Generated_ProducesConsistentValues()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var data = new Dictionary<string, object?>
        {
            ["extId"] = "{GENERATED:myExt}",
            ["extIdCopy"] = "{GENERATED:myExt}"
        };

        var resolved = _factory.ResolveTemplateData(data, ctx);

        // Beide Felder müssen denselben generierten Wert erhalten
        Assert.Equal(resolved["extId"], resolved["extIdCopy"]);
        Assert.NotNull(resolved["extId"]);
    }

    [Fact]
    public void ResolveTemplateData_Guid_Produces8Chars()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var data = new Dictionary<string, object?>
        {
            ["uniqueId"] = "{GUID}"
        };

        var resolved = _factory.ResolveTemplateData(data, ctx);

        Assert.Equal(8, ((string)resolved["uniqueId"]!).Length);
    }

    [Fact]
    public void ResolveTemplateData_MultiplePlaceholders_AllResolved()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var data = new Dictionary<string, object?>
        {
            ["fullName"] = "{PREFIX}_{TESTID}_{GUID}"
        };

        var resolved = _factory.ResolveTemplateData(data, ctx);
        var value = (string)resolved["fullName"]!;

        Assert.DoesNotContain("{", value);
        Assert.StartsWith("ITT_TC01_", value);
    }

    [Fact]
    public void ResolveTemplateData_EmptyDict_ReturnsEmpty()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var resolved = _factory.ResolveTemplateData(new Dictionary<string, object?>(), ctx);

        Assert.NotNull(resolved);
        Assert.Empty(resolved);
    }
}
