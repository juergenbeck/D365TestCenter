using System;
using System.IO;
using D365TestCenter.Cli;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E1 (ADR-0008): documentation pass-through. Core BuildDocumentation (whitelist)
/// and the CLI-side CollectDocumentation walk. The Dataverse write (SyncDocumentation)
/// needs a live org and is verified by the smoke, not here.
/// </summary>
public class DocSyncTests
{
    const string Md =
        "---\nid: DYN10000-TC8\ntitel: \"Adresse\"\n---\n\n" +
        "## Zweck\n\nKontakt erbt Adresse.\n\n" +
        "## Datenkonstellation\n\n- 1 account\n\n" +
        "## Erwartetes Ergebnis\n\nmarkant_adresse gesetzt.\n\n" +
        "## Ergebnis-Historie\n\n| x | y |\n\n" +
        "## D365TestCenter-Definition\n\n```json\n{}\n```\n";

    [Fact]
    public void BuildDocumentation_WhitelistedSectionsInOrder()
    {
        var doc = MarkdownReportGenerator.ParseDefinition(Md);
        var d = MarkdownReportGenerator.BuildDocumentation(doc);

        Assert.Contains("## Zweck", d);
        Assert.Contains("Kontakt erbt Adresse.", d);
        Assert.Contains("## Datenkonstellation", d);
        Assert.Contains("## Erwartetes Ergebnis", d);
        // not whitelisted -> excluded
        Assert.DoesNotContain("Ergebnis-Historie", d);
        Assert.DoesNotContain("D365TestCenter-Definition", d);
        // order preserved (FullSections order)
        Assert.True(d.IndexOf("## Zweck", StringComparison.Ordinal)
                    < d.IndexOf("## Datenkonstellation", StringComparison.Ordinal));
        Assert.True(d.IndexOf("## Datenkonstellation", StringComparison.Ordinal)
                    < d.IndexOf("## Erwartetes Ergebnis", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDocumentation_NoWhitelistedSection_Empty()
    {
        var doc = MarkdownReportGenerator.ParseDefinition(
            "---\nid: X\n---\n\n## Env-Scope und Freigabe\n\nDEV.\n");
        Assert.Equal("", MarkdownReportGenerator.BuildDocumentation(doc));
    }

    [Fact]
    public void BuildDocumentation_BridgeVocabulary_BeschreibungInPlaceholdersOut()
    {
        // Bridge (fg-testtool) defs carry their purpose under "Beschreibung"; their
        // Vorbereitung/Schritte/Erwartung sections are placeholders and must stay out. (Decision 22)
        var doc = MarkdownReportGenerator.ParseDefinition(
            "---\nid: TC01\n---\n\n" +
            "## Beschreibung\n\nPrüft die Last-Update-Wins-Propagation.\n\n" +
            "## Vorbereitung\n\nKonkrete Vorbereitungs-Schritte sind im D365TC-JSON kodiert.\n\n" +
            "## Schritte\n\nSiehe `steps` im D365TC-JSON unten.\n\n" +
            "## Erwartung\n\nSiehe `assertions` im D365TC-JSON unten.\n");
        var d = MarkdownReportGenerator.BuildDocumentation(doc);

        Assert.Contains("## Beschreibung", d);
        Assert.Contains("Last-Update-Wins", d);
        Assert.DoesNotContain("Vorbereitung", d);
        Assert.DoesNotContain("Schritte", d);
        Assert.DoesNotContain("assertions", d);
    }

    [Fact]
    public void CollectDocumentation_WalksAndFilters()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ds_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TC8.md"), Md);
            File.WriteAllText(Path.Combine(dir, "noid.md"), "## Zweck\n\nKein Frontmatter.\n");
            File.WriteAllText(Path.Combine(dir, "nodoc.md"),
                "---\nid: TC9\n---\n\n## Env-Scope und Freigabe\n\nDEV.\n");

            var collected = DocSync.CollectDocumentation(dir);

            Assert.Single(collected);
            Assert.Equal("DYN10000-TC8", collected[0].Id);
            Assert.Contains("## Zweck", collected[0].Documentation);
            Assert.Contains("## Erwartetes Ergebnis", collected[0].Documentation);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CollectDocumentation_MissingDir_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            DocSync.CollectDocumentation(
                Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N"))));
    }
}
