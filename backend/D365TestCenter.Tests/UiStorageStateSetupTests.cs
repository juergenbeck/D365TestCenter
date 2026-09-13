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
    [InlineData("CDHTEST")]
    public void WriteEnabledNotice_AllowsReadAndWriteWithoutPerActionApproval(string umgebung)
    {
        var notice = string.Join(
            Environment.NewLine,
            StorageStateSetup.GetWriteEnabledEnvironmentNotice(umgebung));

        Assert.Contains(umgebung, notice);
        Assert.Contains("Lese- und Schreibschritte", notice);
        Assert.Contains("keine gesonderte Freigabe je Aktion", notice);
        Assert.Contains("erweitert den beauftragten Testumfang nicht", notice);
        Assert.DoesNotContain("ausschließlich LESENDE", notice);
    }

    // Hard guard (ADR-2026-09-12-1152): DEV, TEST und CDHTEST sind zugelassen,
    // alles andere bleibt gesperrt. CDHTEST kam am 12.09.2026 dazu, weil es seit
    // dem 08.08.2026 dieselbe dauerhafte Schreibfreigabe trägt wie TEST.
    [Theory]
    [InlineData("https://markant-dev.crm4.dynamics.com", "DEV")]
    [InlineData("https://markant-test.crm4.dynamics.com", "TEST")]
    [InlineData("https://markant-cdhtest.crm4.dynamics.com", "CDHTEST")]
    public void TryResolveEnvironment_AcceptsDevTestAndCdhTest(string org, string erwartet)
    {
        Assert.True(StorageStateSetup.TryResolveEnvironment(org, out var umgebung));
        Assert.Equal(erwartet, umgebung);
    }

    [Theory]
    [InlineData("https://markant-prod.crm4.dynamics.com")]
    [InlineData("https://markant-datatest.crm4.dynamics.com")]
    [InlineData("https://markant-accept.crm4.dynamics.com")]
    [InlineData("https://example.crm4.dynamics.com")]
    [InlineData("https://markant-prod.crm4.dynamics.com/main.aspx?x=-cdhtest.")]
    [InlineData("https://markant-prod.crm4.dynamics.com/-test./main.aspx")]
    [InlineData("markant-cdhtest.crm4.dynamics.com")]
    [InlineData("")]
    public void TryResolveEnvironment_RefusesEverythingElse(string org)
    {
        Assert.False(StorageStateSetup.TryResolveEnvironment(org, out var umgebung));
        Assert.Equal(string.Empty, umgebung);
    }

    [Fact]
    public async Task UiSetupHelp_ListsDevTestAndCdhTest()
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
        Assert.Contains("DEV, TEST or CDHTEST", help);
        Assert.DoesNotContain("DEV-only", help);
    }
}
