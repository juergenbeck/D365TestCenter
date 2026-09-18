using D365TestCenter.Cli;
using D365TestCenter.Cli.UiAutomation;
using Xunit;

namespace D365TestCenter.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = "Console output";
}

[Collection(ConsoleOutputCollection.Name)]
public sealed class UiStorageStateSetupTests
{
    [Theory]
    [InlineData("TEST")]
    [InlineData("UAT")]
    public void NonDevNotice_NamesEnvironmentAndKeepsScope(string umgebung)
    {
        var notice = string.Join(
            Environment.NewLine,
            StorageStateSetup.GetWriteEnabledEnvironmentNotice(umgebung));

        Assert.Contains(umgebung, notice);
        Assert.Contains("regelt das Projekt", notice);
        Assert.Contains("erweitert den beauftragten Testumfang nicht", notice);
    }

    // Hard guard: "-dev." and "-test." are always accepted; further environments
    // only when the caller names them via --allow-env.
    [Theory]
    [InlineData("https://contoso-dev.crm4.dynamics.com", "DEV")]
    [InlineData("https://contoso-test.crm4.dynamics.com", "TEST")]
    public void TryResolveEnvironment_AcceptsDevAndTest(string org, string erwartet)
    {
        Assert.True(StorageStateSetup.TryResolveEnvironment(org, null, out var umgebung));
        Assert.Equal(erwartet, umgebung);
    }

    [Theory]
    [InlineData("https://contoso-uat.crm4.dynamics.com", "uat", "UAT")]
    [InlineData("https://contoso-sandbox2.crm4.dynamics.com", " Sandbox2 ", "SANDBOX2")]
    [InlineData("https://contoso-dev.crm4.dynamics.com", "uat", "DEV")]
    public void TryResolveEnvironment_AcceptsExplicitlyAllowedEnvironment(string org, string allow, string erwartet)
    {
        Assert.True(StorageStateSetup.TryResolveEnvironment(org, [allow], out var umgebung));
        Assert.Equal(erwartet, umgebung);
    }

    [Theory]
    [InlineData("https://contoso-prod.crm4.dynamics.com")]
    [InlineData("https://contoso-perftest.crm4.dynamics.com")]
    [InlineData("https://contoso-uat.crm4.dynamics.com")]
    [InlineData("https://contoso.crm4.dynamics.com")]
    [InlineData("https://contoso-prod.crm4.dynamics.com/main.aspx?x=-test.")]
    [InlineData("https://contoso-prod.crm4.dynamics.com/-test./main.aspx")]
    [InlineData("contoso-test.crm4.dynamics.com")]
    [InlineData("")]
    public void TryResolveEnvironment_RefusesEverythingElse(string org)
    {
        Assert.False(StorageStateSetup.TryResolveEnvironment(org, null, out var umgebung));
        Assert.Equal(string.Empty, umgebung);
    }

    [Theory]
    [InlineData("https://contoso-prod.crm4.dynamics.com", "")]
    [InlineData("https://contoso-prod.crm4.dynamics.com", "-prod.")]
    [InlineData("https://contoso-prod.crm4.dynamics.com", "uat")]
    [InlineData("https://contoso-prod.crm4.dynamics.com", "prod")]
    [InlineData("https://contoso-prd.crm4.dynamics.com", "PRD")]
    public void TryResolveEnvironment_IgnoresEmptyOrMalformedMarkers(string org, string allow)
    {
        Assert.False(StorageStateSetup.TryResolveEnvironment(org, [allow], out _));
    }

    [Fact]
    public async Task UiSetupHelp_ListsAllowEnvOption()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var exitCode = await Program.Main(["ui-setup", "--help"]);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var help = output.ToString() + error.ToString();
        Assert.Contains("--allow-env", help);
        Assert.DoesNotContain("DEV-only", help);
    }
}
