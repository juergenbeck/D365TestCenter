using System.Collections.Generic;
using System.Linq;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E6 (ADR-0008): the management inventory built from the Markdown definitions. Static
/// overview (front-matter facts, status/domain roll-ups) enriched with the run trend from
/// ergebnis_historie. Pure string logic; the CLI-side directory walk/IO is covered elsewhere.
/// </summary>
public class InventoryBuilderTests
{
    const string DsgvoDef =
        "---\n" +
        "id: DCC-CONTACT-01\n" +
        "titel: \"Master-Cascade Contact\"\n" +
        "domaene: master-cascade\n" +
        "status: aktiv\n" +
        "suite_tags: [regression-cascade]\n" +
        "ticket: DYN-9149\n" +
        "weitere_tickets: [DYN-9558, DYN-9474]\n" +
        "d365tc_lauf_status: verifiziert\n" +
        "ergebnis_historie:\n" +
        "  - { datum: 2026-06-16, env: dev, modus: d365testcenter, ergebnis: \"8/8 PASS (47s)\" }\n" +
        "  - { datum: 2026-05-12, env: dev, modus: skript, ergebnis: \"16/16 PASS (90s)\" }\n" +
        "---\n\n## Zweck\n\nx\n";

    const string BridgeDef =
        "---\n" +
        "id: TC01\n" +
        "titel: \"FG-Propagation\"\n" +
        "domaene: fieldgovernance\n" +
        "status: aktiv\n" +
        "suite_tags: [regression-fg]\n" +
        "---\n\n## Beschreibung\n\nx\n";

    [Fact]
    public void BuildEntry_ParsesFrontmatterAndHistory()
    {
        var e = InventoryBuilder.BuildEntry(DsgvoDef, "master-cascade/DCC-CONTACT-01.md")!;

        Assert.Equal("DCC-CONTACT-01", e.Id);
        Assert.Equal("Master-Cascade Contact", e.Titel);
        Assert.Equal("master-cascade", e.Domaene);
        Assert.Equal("aktiv", e.Status);
        Assert.Equal(new[] { "regression-cascade" }, e.SuiteTags);
        Assert.Equal("DYN-9149, DYN-9558, DYN-9474", e.Ticket);   // ticket + weitere_tickets
        Assert.Equal("verifiziert", e.LaufStatus);
        Assert.Equal(2, e.History.Count);
    }

    [Fact]
    public void BuildEntry_NoId_ReturnsNull()
    {
        Assert.Null(InventoryBuilder.BuildEntry("# Readme\n\nKein Frontmatter.\n", "README.md"));
        Assert.Null(InventoryBuilder.BuildEntry("---\ntitel: \"ohne id\"\n---\n\nx\n", "x.md"));
    }

    [Fact]
    public void Trend_AllPass()
    {
        var e = InventoryBuilder.BuildEntry(DsgvoDef, "x.md")!;
        Assert.Equal("2x PASS", InventoryBuilder.Trend(e.History));
        Assert.Equal("2026-06-16 (DEV) 8/8 PASS (47s)", InventoryBuilder.LastRun(e.History));
    }

    [Fact]
    public void Trend_MixedAndEmpty()
    {
        var history = new List<HistoryEntry>
        {
            new() { Datum = "2026-06-15", Env = "dev", Ergebnis = "8/8 PASS (47s)" },
            new() { Datum = "2026-06-16", Env = "dev", Ergebnis = "7/8 FAIL (50s)" },
        };
        Assert.Equal("2 Läufe, 1x nicht-PASS", InventoryBuilder.Trend(history));
        Assert.Equal("2026-06-16 (DEV) 7/8 FAIL (50s)", InventoryBuilder.LastRun(history));   // newest by date

        Assert.Equal("-", InventoryBuilder.Trend(new List<HistoryEntry>()));
        Assert.Equal("-", InventoryBuilder.LastRun(new List<HistoryEntry>()));
    }

    [Fact]
    public void Render_RollupsTableAndAdditiveTrend()
    {
        var model = InventoryBuilder.Build(new[]
        {
            ("master-cascade/DCC-CONTACT-01.md", DsgvoDef),
            ("fieldgovernance/TC01.md", BridgeDef),
        });
        var md = InventoryBuilder.Render(model);

        Assert.Contains("Gesamt: 2 Test-Definitionen.", md);
        Assert.Contains("## Status-Verteilung", md);
        Assert.Contains("## Domänen-Verteilung", md);
        Assert.Contains("## Lauf-Status-Verteilung", md);          // present: one def carries d365tc_lauf_status
        Assert.Contains("| ID | Titel | Stufe | Status | Suite-Tags | Ticket | Verantw. | Min | Quelle | Letzter Lauf | Trend | Datei |", md);
        Assert.Contains("2x PASS", md);                            // DSGVO def with history
        Assert.Contains("DCC-CONTACT-01", md);
        Assert.Contains("TC01", md);

        // additive: the Bridge def (no history) shows "-" in the trend cells, then the file link
        var tc01Row = md.Split('\n').First(l => l.StartsWith("| TC01 "));
        Assert.EndsWith("| - | - | [fieldgovernance/TC01.md](fieldgovernance/TC01.md) |", tc01Row.TrimEnd());
    }

    const string FullMetaDef =
        "---\n" +
        "id: BR-CS-01\n" +
        "titel: \"Bridge CS\"\n" +
        "domaene: bridge\n" +
        "stufe: 2\n" +
        "status: aktiv\n" +
        "suite_tags: [regression-bridge]\n" +
        "verantwortlich: jbe\n" +
        "geschaetzt_min: 5\n" +
        "quelle: DYN-10000\n" +
        "---\n\n## Beschreibung\n\nx\n";

    [Fact]
    public void BuildEntry_ParsesAdditiveMarkantFields()
    {
        var e = InventoryBuilder.BuildEntry(FullMetaDef, "bridge/BR-CS-01.md")!;
        Assert.Equal("2", e.Stufe);
        Assert.Equal("jbe", e.Verantwortlich);
        Assert.Equal("5", e.GeschaetztMin);
        Assert.Equal("DYN-10000", e.Quelle);
        Assert.Equal("bridge/BR-CS-01.md", e.Datei);
    }

    [Fact]
    public void Render_AdditiveColumns_FilledAndEmpty()
    {
        var model = InventoryBuilder.Build(new[]
        {
            ("bridge/BR-CS-01.md", FullMetaDef),
            ("fieldgovernance/TC01.md", BridgeDef),   // carries none of the extra fields
        });
        var md = InventoryBuilder.Render(model);

        var brRow = md.Split('\n').First(l => l.StartsWith("| BR-CS-01 "));
        Assert.Contains("| 2 |", brRow);              // Stufe
        Assert.Contains("| jbe |", brRow);            // Verantwortlich
        Assert.Contains("| 5 |", brRow);              // Min
        Assert.Contains("| DYN-10000 |", brRow);      // Quelle
        Assert.Contains("[bridge/BR-CS-01.md](bridge/BR-CS-01.md)", brRow);   // Datei-Link

        // additive: the def without these fields renders empty cells, still has its file link
        var tc01Row = md.Split('\n').First(l => l.StartsWith("| TC01 "));
        Assert.Contains("[fieldgovernance/TC01.md](fieldgovernance/TC01.md)", tc01Row);
    }

    [Fact]
    public void Render_NoLaufStatusAnywhere_OmitsThatRollup()
    {
        var model = InventoryBuilder.Build(new[] { ("fieldgovernance/TC01.md", BridgeDef) });
        var md = InventoryBuilder.Render(model);
        Assert.DoesNotContain("## Lauf-Status-Verteilung", md);
    }
}
