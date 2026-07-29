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
    [Fact]
    public void TestEnvironmentNotice_AllowsReadAndWriteWithoutPerActionApproval()
    {
        var notice = string.Join(Environment.NewLine, StorageStateSetup.GetTestEnvironmentNotice());

        Assert.Contains("Lese- und Schreibschritte", notice);
        Assert.Contains("keine gesonderte Freigabe je Aktion", notice);
        Assert.Contains("erweitert den beauftragten Testumfang nicht", notice);
        Assert.DoesNotContain("ausschließlich LESENDE", notice);
    }

    [Fact]
    public async Task UiSetupHelp_ListsDevAndTest()
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
        Assert.Contains("DEV or TEST", help);
        Assert.DoesNotContain("DEV-only", help);
    }
}
