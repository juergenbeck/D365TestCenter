using itt.IntegrationTests.Core;
using Newtonsoft.Json;
using Xunit;

namespace itt.IntegrationTests.Tests;

public class TestDataFactoryTests
{
    private readonly TestDataFactory _factory = new();

    [Fact]
    public void ResolvePlaceholders_Timestamp_ReplacesWithCurrentTime()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("Test_{TIMESTAMP}", ctx);

        Assert.DoesNotContain("{TIMESTAMP}", result);
        Assert.StartsWith("Test_", result);
        Assert.True(result.Length > 10);
    }

    [Fact]
    public void ResolvePlaceholders_TestId_ReplacesCorrectly()
    {
        var ctx = new TestContext { TestId = "TC42" };
        var result = _factory.ResolvePlaceholders("{PREFIX}_{TESTID}", ctx);

        Assert.Equal("ITT_TC42", result);
    }

    [Fact]
    public void ResolvePlaceholders_Guid_Returns8Characters()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("{GUID}", ctx);

        Assert.Equal(8, result.Length);
        Assert.DoesNotContain("{", result);
    }

    [Fact]
    public void ResolvePlaceholders_Prefix_ReturnsITT()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("{PREFIX}", ctx);

        Assert.Equal("ITT", result);
    }

    [Fact]
    public void ResolvePlaceholders_NowUtc_ReturnsIso8601()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("{NOW_UTC}", ctx);

        Assert.Contains("T", result);
        Assert.True(DateTimeOffset.TryParse(result, out _));
    }

    [Fact]
    public void ResolvePlaceholders_ContactId_ReplacesGuid()
    {
        var contactId = Guid.NewGuid();
        var ctx = new TestContext { TestId = "TC01", ContactId = contactId };
        var result = _factory.ResolvePlaceholders("{CONTACT_ID}", ctx);

        Assert.Equal(contactId.ToString(), result);
    }

    [Fact]
    public void ResolvePlaceholders_FakerFirstName_ReturnsNonEmpty()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("{FAKER:FirstName}", ctx);

        Assert.DoesNotContain("{FAKER:", result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void ResolvePlaceholders_FakerUnknown_KeepsPlaceholder()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("{FAKER:UnknownToken}", ctx);

        Assert.Equal("{FAKER:UnknownToken}", result);
    }

    [Fact]
    public void ResolvePlaceholders_NullTemplate_ReturnsNull()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders(null!, ctx);

        Assert.Null(result);
    }

    [Fact]
    public void ResolvePlaceholders_EmptyTemplate_ReturnsEmpty()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("", ctx);

        Assert.Equal("", result);
    }

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
    public void GenerateBogusContactData_ContainsAllRequiredFields()
    {
        var data = _factory.GenerateBogusContactData();

        Assert.True(data.ContainsKey("firstname"));
        Assert.True(data.ContainsKey("lastname"));
        Assert.True(data.ContainsKey("emailaddress1"));
        Assert.True(data.ContainsKey("telephone1"));
        Assert.True(data.ContainsKey("jobtitle"));
        Assert.All(data.Values, v => Assert.NotNull(v));
    }

    [Fact]
    public void GenerateBogusAccountData_ContainsName()
    {
        var data = _factory.GenerateBogusAccountData();

        Assert.True(data.ContainsKey("name"));
        Assert.NotNull(data["name"]);
        Assert.NotEmpty((string)data["name"]!);
    }

    [Fact]
    public void ResolvePlaceholders_MultiplePlaceholders_AllResolved()
    {
        var ctx = new TestContext { TestId = "TC01" };
        var result = _factory.ResolvePlaceholders("{PREFIX}_{TESTID}_{GUID}", ctx);

        Assert.DoesNotContain("{", result);
        Assert.StartsWith("ITT_TC01_", result);
    }
}
