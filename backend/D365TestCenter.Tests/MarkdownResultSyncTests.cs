using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E2 (ADR-0008): round-trip of run results into the Markdown test definitions.
/// Front-matter ergebnis_historie is SSOT, body table rendered between markers.
/// </summary>
public class MarkdownResultSyncTests
{
    const string Fixture =
        "---\n" +
        "id: DEMO-TC1\n" +
        "titel: \"Demo\"\n" +
        "status: aktiv\n" +
        "d365tc_lauf_status: entwurf\n" +
        "ergebnis_historie:\n" +
        "  - { datum: 2026-06-17, env: dev, modus: d365testcenter, ergebnis: \"1/1 PASS (47s)\" }\n" +
        "---\n" +
        "\n" +
        "## Zweck\n" +
        "\n" +
        "Demo.\n" +
        "\n" +
        "## Ergebnis-Historie\n" +
        "\n" +
        "| Datum | Env | Modus | Ergebnis |\n" +
        "|---|---|---|---|\n" +
        "| 2026-06-17 | DEV | d365testcenter | 1/1 PASS (47s) |\n" +
        "\n" +
        "## D365TestCenter-Definition\n" +
        "\n" +
        "(json)\n";

    static HistoryEntry Entry(string datum, string env, string ergebnis, string modus = "d365testcenter")
        => new HistoryEntry { Datum = datum, Env = env, Modus = modus, Ergebnis = ergebnis };

    static TestCaseResult Tc(TestOutcome outcome, long ms = 1000)
        => new TestCaseResult { TestId = "DEMO-TC1", Outcome = outcome, DurationMs = ms };

    [Fact]
    public void Sync_AddsNewEntry_ToFrontmatter()
    {
        var result = MarkdownResultSync.Sync(Fixture, Entry("2026-06-18", "dev", "1/1 PASS (50s)"));
        Assert.Contains("datum: 2026-06-18, env: dev, modus: d365testcenter, ergebnis: \"1/1 PASS (50s)\"", result);
        Assert.Contains("datum: 2026-06-17, env: dev", result); // old entry preserved
    }

    [Fact]
    public void Sync_Idempotent_ReplacesSameDayEnvMode()
    {
        var result = MarkdownResultSync.Sync(Fixture, Entry("2026-06-17", "dev", "1/1 PASS (99s)"));
        Assert.Equal(1, Regex.Matches(result, "datum: 2026-06-17, env: dev,").Count);
        Assert.Contains("1/1 PASS (99s)", result);
        Assert.DoesNotContain("1/1 PASS (47s)", result);
    }

    [Fact]
    public void Sync_SetsLaufStatusVerifiziert()
    {
        var result = MarkdownResultSync.Sync(Fixture, Entry("2026-06-18", "dev", "1/1 PASS (50s)"));
        Assert.Contains("d365tc_lauf_status: verifiziert", result);
        Assert.DoesNotContain("d365tc_lauf_status: entwurf", result);
    }

    [Fact]
    public void Sync_RendersBodyTable_BetweenMarkers()
    {
        var result = MarkdownResultSync.Sync(Fixture, Entry("2026-06-18", "dev", "1/1 PASS (50s)"));
        Assert.Contains(MarkdownResultSync.MarkerStart, result);
        Assert.Contains(MarkdownResultSync.MarkerEnd, result);
        Assert.Contains("| 2026-06-18 | DEV | d365testcenter | 1/1 PASS (50s) |", result);
    }

    [Fact]
    public void Sync_ReplacesExistingMarkers_NoDuplication()
    {
        var once = MarkdownResultSync.Sync(Fixture, Entry("2026-06-18", "dev", "1/1 PASS (50s)"));
        var twice = MarkdownResultSync.Sync(once, Entry("2026-06-19", "test", "1/1 PASS (12s)"));
        Assert.Equal(1, Regex.Matches(twice, Regex.Escape(MarkdownResultSync.MarkerStart)).Count);
        Assert.Equal(1, Regex.Matches(twice, Regex.Escape(MarkdownResultSync.MarkerEnd)).Count);
        Assert.Contains("| 2026-06-19 | TEST | d365testcenter | 1/1 PASS (12s) |", twice);
    }

    [Fact]
    public void Sync_NoHistorySection_UpdatesFrontmatterOnly()
    {
        const string noSection =
            "---\n" +
            "id: X\n" +
            "ergebnis_historie:\n" +
            "  - { datum: 2026-06-17, env: dev, modus: d365testcenter, ergebnis: \"1/1 PASS (47s)\" }\n" +
            "---\n\n## Zweck\n\nDemo.\n";
        var result = MarkdownResultSync.Sync(noSection, Entry("2026-06-18", "dev", "1/1 PASS (50s)"));
        Assert.Contains("datum: 2026-06-18, env: dev", result);
        Assert.DoesNotContain(MarkdownResultSync.MarkerStart, result);
    }

    [Fact]
    public void Sync_PreservesCrlf()
    {
        var crlf = Fixture.Replace("\n", "\r\n");
        var result = MarkdownResultSync.Sync(crlf, Entry("2026-06-18", "dev", "1/1 PASS (50s)"));
        Assert.Contains("\r\n", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", "")); // no bare LF remains
    }

    [Fact]
    public void Sync_NoFrontmatter_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MarkdownResultSync.Sync("# Just a heading\n\nNo front-matter.\n", Entry("2026-06-18", "dev", "x")));
    }

    [Fact]
    public void FormatErgebnis_AllPassed_IsPass()
    {
        Assert.Equal("1/1 PASS (47s)",
            MarkdownResultSync.FormatErgebnis(new List<TestCaseResult> { Tc(TestOutcome.Passed, 47000) }));
    }

    [Fact]
    public void FormatErgebnis_OneFailed_IsFail()
    {
        Assert.Equal("0/1 FAIL (1s)",
            MarkdownResultSync.FormatErgebnis(new List<TestCaseResult> { Tc(TestOutcome.Failed, 1000) }));
    }

    [Fact]
    public void FormatErgebnis_AnyError_IsError()
    {
        Assert.Equal("1/2 ERROR (2s)", MarkdownResultSync.FormatErgebnis(new List<TestCaseResult>
        {
            Tc(TestOutcome.Passed, 1000), Tc(TestOutcome.Error, 1000)
        }));
    }

    [Fact]
    public void BuildEntry_LowercasesEnv_FormatsDate()
    {
        var e = MarkdownResultSync.BuildEntry(
            new List<TestCaseResult> { Tc(TestOutcome.Passed, 5000) }, new DateTime(2026, 6, 18), "DEV");
        Assert.Equal("2026-06-18", e.Datum);
        Assert.Equal("dev", e.Env);
        Assert.Equal("d365testcenter", e.Modus);
        Assert.Equal("1/1 PASS (5s)", e.Ergebnis);
    }

    [Fact]
    public void ReadFrontmatterId_ReturnsId()
        => Assert.Equal("DEMO-TC1", MarkdownResultSync.ReadFrontmatterId(Fixture));
}
