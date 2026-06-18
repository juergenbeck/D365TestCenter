using System;
using System.Collections.Generic;
using System.IO;
using D365TestCenter.Cli;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E3 (ADR-0008) CLI-side assembly: directory walk + testId matching + suite
/// README pickup (ReportBuilder.BuildModel), on top of the Core renderer. The
/// Dataverse reads (LoadRunHeader/LoadResultsFromRun) need a live org and are
/// verified by the live smoke, not here.
/// </summary>
public class ReportBuilderTests
{
    const string Tc8 =
        "---\nid: DYN10000-TC8\ntitel: \"Adresse beim Anlegen\"\n---\n\n" +
        "## Zweck\n\nKontakt erbt Adresse beim Create.\n\n## Erwartetes Ergebnis\n\nmarkant_adresse gesetzt.\n";
    const string Tc1 =
        "---\nid: DYN10000-TC1\ntitel: \"Create setzt Adresse\"\n---\n\n## Zweck\n\nBasisfall.\n";
    const string Readme =
        "# DYN-10000 Adressvererbung\n\n## Worum es geht\n\nThema.\n\n## Träger-Modell\n\n| m | t |\n";

    static TestCaseResult Tc(string id, TestOutcome o, long ms, string? err = null)
        => new TestCaseResult { TestId = id, Outcome = o, DurationMs = ms, ErrorMessage = err };

    static string MakeDefs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "contact-address"));
        File.WriteAllText(Path.Combine(dir, "README.md"), Readme);
        File.WriteAllText(Path.Combine(dir, "contact-address", "DYN10000-TC8.md"), Tc8);
        File.WriteAllText(Path.Combine(dir, "contact-address", "DYN10000-TC1.md"), Tc1);
        return dir;
    }

    [Fact]
    public void BuildModel_MergesDocsResultsAndSuite()
    {
        var dir = MakeDefs();
        try
        {
            var header = new ReportBuilder.RunHeader
            {
                StartedOn = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc),
                CompletedOn = new DateTime(2026, 6, 18, 10, 0, 49, DateTimeKind.Utc),
                Filter = "DYN10000-*"
            };
            var results = new List<TestCaseResult>
            {
                Tc("DYN10000-TC8", TestOutcome.Passed, 16000),
                Tc("DYN10000-TC1", TestOutcome.Passed, 5000)
            };
            var m = ReportBuilder.BuildModel(header, results, dir, "dev",
                Guid.Parse("787a059e-8c6a-f111-a826-7c1e528427dd"));

            Assert.Equal("DYN-10000 Adressvererbung", m.SuiteTitle);
            Assert.Contains("Thema.", m.SuiteIntro);
            Assert.Equal("DYN10000-*", m.Filter);
            Assert.Equal("2026-06-18", m.RunDate);
            Assert.Equal(49, m.DurationSeconds);   // wall-clock from header
            Assert.Equal(2, m.Total);
            Assert.Equal(2, m.Passed);
            // sorted by testId: TC1 before TC8
            Assert.Equal("DYN10000-TC1", m.Items[0].TestId);
            Assert.Equal("DYN10000-TC8", m.Items[1].TestId);
            Assert.Equal("Adresse beim Anlegen", m.Items[1].Titel);
            Assert.Contains("erbt Adresse", m.Items[1].Sections["Zweck"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildModel_ResultWithoutDefinition_StillListed()
    {
        var dir = MakeDefs();
        try
        {
            var results = new List<TestCaseResult> { Tc("UNKNOWN-TC9", TestOutcome.Failed, 1000, "boom") };
            var m = ReportBuilder.BuildModel(null, results, dir, "dev", Guid.NewGuid());
            Assert.Single(m.Items);
            Assert.Equal("UNKNOWN-TC9", m.Items[0].TestId);
            Assert.Equal("", m.Items[0].Titel);
            Assert.Empty(m.Items[0].Sections);
            Assert.Equal("boom", m.Items[0].ErrorMessage);
            Assert.Equal(1, m.Failed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildModel_NoHeader_DurationFromSumOfResults()
    {
        var dir = MakeDefs();
        try
        {
            var results = new List<TestCaseResult>
            {
                Tc("DYN10000-TC8", TestOutcome.Passed, 16000),
                Tc("DYN10000-TC1", TestOutcome.Passed, 5000)
            };
            var m = ReportBuilder.BuildModel(null, results, dir, "dev", Guid.NewGuid());
            Assert.Equal("", m.RunDate);
            Assert.Equal(21, m.DurationSeconds);   // 16000 + 5000 ms, no wall-clock available
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildModel_ThenRenderCompact_ProducesTable()
    {
        var dir = MakeDefs();
        try
        {
            var results = new List<TestCaseResult>
            {
                Tc("DYN10000-TC8", TestOutcome.Passed, 16000),
                Tc("DYN10000-TC1", TestOutcome.Passed, 5000)
            };
            var m = ReportBuilder.BuildModel(null, results, dir, "dev", Guid.NewGuid());
            var md = MarkdownReportGenerator.Render(m, ReportDetail.Compact);
            Assert.Contains("# Durchführungsbericht: DYN-10000 Adressvererbung", md);
            Assert.Contains("2/2 PASS", md);
            Assert.Contains("DYN10000-TC1", md);
            Assert.Contains("DYN10000-TC8", md);
            Assert.Contains("Basisfall.", md);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BuildModel_MissingDir_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            ReportBuilder.BuildModel(null, new List<TestCaseResult>(),
                Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N")),
                "dev", Guid.NewGuid()));
    }
}
