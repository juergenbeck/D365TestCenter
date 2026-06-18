using System;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E3 (ADR-0008) Core: parsing the Markdown definitions/README and rendering the
/// compact/full run report. Pure string logic (MarkdownReportGenerator,
/// MarkdownDocument); the CLI-side IO is covered in ReportBuilderTests.
/// </summary>
public class MarkdownReportGeneratorTests
{
    const string DefMd =
        "---\n" +
        "id: DYN10000-TC8\n" +
        "titel: \"Account-Adressvererbung beim Anlegen\"\n" +
        "status: aktiv\n" +
        "---\n\n" +
        "## Zweck\n\nBeleg, dass der Kontakt die Adresse erbt. Zweiter Satz hier.\n\n" +
        "## Datenkonstellation\n\n- 1 account\n- 1 contact\n\n" +
        "## Vorbedingungen\n\nPlugin aktiv.\n\n" +
        "## Ablauf\n\n1. Setup\n2. Trigger\n\n" +
        "## Erwartetes Ergebnis\n\nmarkant_adresse ist gesetzt.\n\n" +
        "## Ergebnis-Historie\n\n| x | y |\n";

    const string ReadmeMd =
        "# DYN-10000 Integrationstests: Adressvererbung\n\n" +
        "> Quelle: konzeptionell\n\n" +
        "## Worum es geht\n\nErster Absatz erklärt das Thema.\n\nZweiter Absatz mit Details.\n\n" +
        "## Träger-Modell\n\n| Modus | Träger |\n|---|---|\n| skript | Smoke |\n";

    // ── MarkdownDocument ─────────────────────────────────────────────

    [Theory]
    [InlineData("id: DEMO-1", "id", "DEMO-1")]
    [InlineData("titel: \"Mit Quotes\"", "titel", "Mit Quotes")]
    [InlineData("titel: 'Single'", "titel", "Single")]
    [InlineData("k: plain value", "k", "plain value")]
    public void ReadScalar_StripsQuotes(string fm, string key, string expected)
        => Assert.Equal(expected, MarkdownDocument.ReadScalar(fm, key));

    [Fact]
    public void ReadScalar_AbsentKey_ReturnsNull()
        => Assert.Null(MarkdownDocument.ReadScalar("id: X", "titel"));

    [Fact]
    public void SplitSections_Level3StaysInsideLevel2()
    {
        var body = "## A\n\ntext a\n\n### sub\n\nmore a\n\n## B\n\ntext b\n";
        var s = MarkdownDocument.SplitSections(body);
        Assert.Contains("text a", s["A"]);
        Assert.Contains("### sub", s["A"]);
        Assert.Contains("more a", s["A"]);
        Assert.Equal("text b", s["B"]);
    }

    // ── ParseDefinition / ParseReadme ────────────────────────────────

    [Fact]
    public void ParseDefinition_ReadsIdTitelAndSections()
    {
        var doc = MarkdownReportGenerator.ParseDefinition(DefMd);
        Assert.Equal("DYN10000-TC8", doc.Id);
        Assert.Equal("Account-Adressvererbung beim Anlegen", doc.Titel);
        Assert.Contains("erbt", doc.Sections["Zweck"]);
        Assert.Contains("1 account", doc.Sections["Datenkonstellation"]);
        Assert.Contains("markant_adresse", doc.Sections["Erwartetes Ergebnis"]);
    }

    [Fact]
    public void ParseReadme_ReadsTitleIntroCarrier()
    {
        var s = MarkdownReportGenerator.ParseReadme(ReadmeMd);
        Assert.Equal("DYN-10000 Integrationstests: Adressvererbung", s.Titel);
        Assert.Contains("Erster Absatz", s.Intro);
        Assert.Contains("Zweiter Absatz", s.Intro);
        Assert.Contains("skript", s.Carrier);
    }

    // ── Render ───────────────────────────────────────────────────────

    static ReportModel SampleModel()
    {
        var doc = MarkdownReportGenerator.ParseDefinition(DefMd);
        var suite = MarkdownReportGenerator.ParseReadme(ReadmeMd);
        return new ReportModel
        {
            SuiteTitle = suite.Titel ?? "",
            SuiteIntro = suite.Intro,
            SuiteCarrier = suite.Carrier,
            RunDate = "2026-06-18",
            Env = "dev",
            RunId = Guid.Parse("787a059e-8c6a-f111-a826-7c1e528427dd"),
            Filter = "DYN10000-*",
            Total = 1,
            Passed = 1,
            DurationSeconds = 16,
            Items =
            {
                new ReportItem
                {
                    TestId = doc.Id!,
                    Titel = doc.Titel,
                    Outcome = TestOutcome.Passed,
                    DurationMs = 15861,
                    Sections = doc.Sections
                }
            }
        };
    }

    [Fact]
    public void Render_Compact_TableWithPurposeExcerpt()
    {
        var md = MarkdownReportGenerator.Render(SampleModel(), ReportDetail.Compact);
        Assert.Contains("# Durchführungsbericht: DYN-10000 Integrationstests: Adressvererbung", md);
        Assert.Contains("1/1 PASS (16s)", md);
        Assert.Contains("| ID | Titel | Zweck | Ergebnis | Dauer |", md);
        Assert.Contains("DYN10000-TC8", md);
        Assert.Contains("Beleg, dass der Kontakt die Adresse erbt.", md);
        Assert.DoesNotContain("Zweiter Satz hier", md);          // only first sentence in the cell
        Assert.Contains("Erster Absatz erklärt das Thema.", md); // first paragraph only
        Assert.DoesNotContain("Zweiter Absatz mit Details", md);
        Assert.DoesNotContain("## Ergebnisse im Detail", md);
        Assert.DoesNotContain("## Träger-Modell", md);
    }

    [Fact]
    public void Render_Full_PerTestSectionsAndCarrier()
    {
        var md = MarkdownReportGenerator.Render(SampleModel(), ReportDetail.Full);
        Assert.Contains("## Ergebnisse im Detail", md);
        Assert.Contains("### DYN10000-TC8 - PASS (16s)", md);
        Assert.Contains("**Account-Adressvererbung beim Anlegen**", md);
        Assert.Contains("**Zweck**", md);
        Assert.Contains("Zweiter Satz hier", md);                 // full content, not excerpt
        Assert.Contains("**Datenkonstellation**", md);
        Assert.Contains("**Ablauf**", md);
        Assert.Contains("**Erwartetes Ergebnis**", md);
        Assert.Contains("## Träger-Modell", md);
        Assert.Contains("Zweiter Absatz mit Details", md);        // full intro
        Assert.DoesNotContain("Ergebnis-Historie", md);          // not in the whitelist
    }

    [Fact]
    public void Render_Full_ShowsErrorOnFailure()
    {
        var m = SampleModel();
        m.Passed = 0;
        m.Failed = 1;
        m.Items[0].Outcome = TestOutcome.Failed;
        m.Items[0].ErrorMessage = "Assert markant_adresse fehlgeschlagen";
        var md = MarkdownReportGenerator.Render(m, ReportDetail.Full);
        Assert.Contains("### DYN10000-TC8 - FAIL", md);
        Assert.Contains("**Fehler:** Assert markant_adresse fehlgeschlagen", md);
        Assert.Contains("0/1 FAIL", md);
    }

    [Fact]
    public void Render_Compact_FallsBackToBeschreibungWhenNoZweck()
    {
        // Bridge defs have no "Zweck"; the compact purpose column falls back to "Beschreibung". (Decision 22)
        var doc = MarkdownReportGenerator.ParseDefinition(
            "---\nid: TC01\ntitel: \"Bridge-Test\"\n---\n\n" +
            "## Beschreibung\n\nPrüft die FG-Propagation. Zweiter Satz hier.\n");
        var m = new ReportModel
        {
            SuiteTitle = "Bridge",
            RunDate = "2026-06-19",
            Total = 1,
            Passed = 1,
            DurationSeconds = 5,
            Items =
            {
                new ReportItem
                {
                    TestId = "TC01",
                    Titel = "Bridge-Test",
                    Outcome = TestOutcome.Passed,
                    DurationMs = 5000,
                    Sections = doc.Sections
                }
            }
        };
        var md = MarkdownReportGenerator.Render(m, ReportDetail.Compact);
        Assert.Contains("Prüft die FG-Propagation.", md);
        Assert.DoesNotContain("Zweiter Satz hier", md);   // only the first sentence in the cell
    }

    [Fact]
    public void Render_Compact_EscapesPipesInCells()
    {
        var m = SampleModel();
        m.Items[0].Titel = "A | B";
        var md = MarkdownReportGenerator.Render(m, ReportDetail.Compact);
        Assert.Contains("A \\| B", md);
    }

    [Fact]
    public void Render_NoReadme_NoIntroSection()
    {
        var m = SampleModel();
        m.SuiteTitle = "";
        m.SuiteIntro = null;
        m.SuiteCarrier = null;
        var md = MarkdownReportGenerator.Render(m, ReportDetail.Full);
        Assert.StartsWith("# Durchführungsbericht\n", md);
        Assert.DoesNotContain("## Worum es geht", md);
        Assert.DoesNotContain("## Träger-Modell", md);
    }

    [Fact]
    public void FirstSentence_FlattensAndCaps()
    {
        Assert.Equal("Satz eins.", MarkdownReportGenerator.FirstSentence("Satz eins.\nSatz zwei."));
        var capped = MarkdownReportGenerator.FirstSentence(new string('x', 300), 240);
        Assert.EndsWith("...", capped);
        Assert.True(capped.Length <= 243);
    }
}
