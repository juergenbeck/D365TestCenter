using System;
using System.Collections.Generic;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using D365TestCenter.Core.Reporting;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// ADR-0009 Phase 4: the Dataverse source for jbe_GenerateReport. The renderer/assembly
/// (MarkdownReportGenerator) is unchanged; these tests pin (1) the source-agnostic BuildModel
/// assembly and (2) reading docs/results/header from Dataverse via the FakeDataverse, so the
/// Custom-API path produces the same model the CLI report does from the Markdown tree.
/// </summary>
public class DataverseReportSourceTests
{
    static readonly StandardCrmConfig Cfg = new();

    static TestCaseResult Res(string id, TestOutcome o, long ms, string? err = null)
        => new TestCaseResult { TestId = id, Outcome = o, DurationMs = ms, ErrorMessage = err };

    [Fact]
    public void BuildModel_SourceAgnostic_MergesDocsAndCountsKpis()
    {
        var docs = new Dictionary<string, DefinitionDoc>(StringComparer.OrdinalIgnoreCase)
        {
            ["TC-8"] = new DefinitionDoc { Id = "TC-8", Titel = "Achter", Sections = { ["Zweck"] = "Achter Zweck" } },
        };
        var results = new List<TestCaseResult>
        {
            Res("TC-8", TestOutcome.Passed, 16000),
            Res("TC-1", TestOutcome.Failed, 5000, "boom"),
        };

        var model = MarkdownReportGenerator.BuildModel(
            new DateTime(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 21, 10, 0, 30, DateTimeKind.Utc),
            "TC-*", results, docs, suite: null, "dev",
            Guid.Parse("787a059e-8c6a-f111-a826-7c1e528427dd"));

        Assert.Equal(2, model.Total);
        Assert.Equal(1, model.Passed);
        Assert.Equal(1, model.Failed);
        Assert.Equal(30, model.DurationSeconds);              // wall-clock from header
        Assert.Equal("TC-1", model.Items[0].TestId);          // sorted by id (ordinal)
        Assert.Equal("", model.Items[0].Titel);               // no doc -> untitled, still listed
        Assert.Equal("Achter", model.Items[1].Titel);
        Assert.Contains("Achter Zweck", model.Items[1].Sections["Zweck"]);
        Assert.Equal("", model.SuiteTitle);                   // no suite README in Dataverse
    }

    [Fact]
    public void BuildModel_NoHeaderTimes_DurationFromSum()
    {
        var results = new List<TestCaseResult> { Res("A", TestOutcome.Passed, 16000), Res("B", TestOutcome.Passed, 5000) };
        var model = MarkdownReportGenerator.BuildModel(
            null, null, null, results, new Dictionary<string, DefinitionDoc>(), null, "dev", Guid.NewGuid());
        Assert.Equal(21, model.DurationSeconds);              // 16000 + 5000 ms, no wall-clock
        Assert.Equal("", model.RunDate);
    }

    [Fact]
    public void LoadDocs_ParsesDocumentationSections_FromTestCase()
    {
        var fake = new FakeDataverse();
        fake.Seed(new Entity("jbe_testcase")
        {
            ["jbe_testid"] = "TC-8",
            ["jbe_title"] = "Achter",
            ["jbe_documentation"] = "## Zweck\n\nKontakt erbt Adresse.\n\n## Erwartetes Ergebnis\n\nGesetzt.",
        });

        var docs = DataverseReportSource.LoadDocs(fake, new[] { "TC-8", "MISSING" });

        Assert.True(docs.ContainsKey("TC-8"));
        Assert.False(docs.ContainsKey("MISSING"));            // no record -> absent (report lists it untitled)
        Assert.Equal("Achter", docs["TC-8"].Titel);
        Assert.Contains("erbt Adresse", docs["TC-8"].Sections["Zweck"]);
        Assert.Contains("Gesetzt", docs["TC-8"].Sections["Erwartetes Ergebnis"]);
    }

    [Fact]
    public void LoadDocs_EmptyIds_ReturnsEmpty()
    {
        Assert.Empty(DataverseReportSource.LoadDocs(new FakeDataverse(), Array.Empty<string>()));
    }

    [Fact]
    public void BuildModel_EndToEnd_FromDataverse()
    {
        var fake = new FakeDataverse();
        var runId = Guid.NewGuid();
        fake.Seed(new Entity("jbe_testrun")
        {
            Id = runId,
            ["jbe_startedon"] = new DateTime(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc),
            ["jbe_completedon"] = new DateTime(2026, 6, 21, 9, 0, 20, DateTimeKind.Utc),
            ["jbe_testcasefilter"] = "TC-*",
        });
        fake.Seed(new Entity("jbe_testrunresult")
        {
            ["jbe_testrunid"] = new EntityReference("jbe_testrun", runId),
            ["jbe_testid"] = "TC-8",
            ["jbe_outcome"] = new OptionSetValue(Cfg.OutcomePassed),
            ["jbe_durationms"] = 16000,
        });
        fake.Seed(new Entity("jbe_testrunresult")
        {
            ["jbe_testrunid"] = new EntityReference("jbe_testrun", runId),
            ["jbe_testid"] = "TC-1",
            ["jbe_outcome"] = new OptionSetValue(Cfg.OutcomeFailed),
            ["jbe_durationms"] = 5000,
            ["jbe_errormessage"] = "boom",
        });
        fake.Seed(new Entity("jbe_testcase")
        {
            ["jbe_testid"] = "TC-8",
            ["jbe_title"] = "Achter",
            ["jbe_documentation"] = "## Zweck\n\nAchter Zweck.",
        });

        var model = DataverseReportSource.BuildModel(fake, Cfg, runId, "dev");

        Assert.Equal(2, model.Total);
        Assert.Equal(1, model.Passed);
        Assert.Equal(1, model.Failed);
        Assert.Equal(20, model.DurationSeconds);              // wall-clock from the run header
        Assert.Equal("TC-*", model.Filter);
        Assert.Equal("TC-1", model.Items[0].TestId);          // sorted
        Assert.Equal("boom", model.Items[0].ErrorMessage);
        Assert.Equal("Achter", model.Items[1].Titel);
        Assert.Contains("Achter Zweck", model.Items[1].Sections["Zweck"]);

        var md = MarkdownReportGenerator.Render(model, ReportDetail.Compact);
        Assert.Contains("1/2", md);
        Assert.Contains("TC-8", md);
    }
}
