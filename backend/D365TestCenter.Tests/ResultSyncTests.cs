using System;
using System.Collections.Generic;
using System.IO;
using D365TestCenter.Cli;
using D365TestCenter.Core;
using D365TestCenter.Core.Config;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// E2 (ADR-0008) CLI-side wiring: directory walk + testId matching + file write
/// (ResultSync), on top of the Core MarkdownResultSync.
/// </summary>
public class ResultSyncTests
{
    const string Md =
        "---\n" +
        "id: DEMO-TC1\n" +
        "status: aktiv\n" +
        "d365tc_lauf_status: entwurf\n" +
        "ergebnis_historie:\n" +
        "  - { datum: 2026-06-17, env: dev, modus: d365testcenter, ergebnis: \"1/1 PASS (47s)\" }\n" +
        "---\n\n## Zweck\n\nDemo.\n";

    static TestCaseResult Tc(string id, TestOutcome o, long ms = 5000)
        => new TestCaseResult { TestId = id, Outcome = o, DurationMs = ms };

    [Fact]
    public void SyncDefinitions_MatchingId_UpdatesFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sub = Path.Combine(dir, "contact-address");
            Directory.CreateDirectory(sub);
            var file = Path.Combine(sub, "DEMO-TC1.md");
            File.WriteAllText(file, Md);

            var results = new List<TestCaseResult> { Tc("DEMO-TC1", TestOutcome.Passed, 50000) };
            var sum = ResultSync.SyncDefinitions(results, dir, new DateTime(2026, 6, 18), "dev");

            Assert.Equal(1, sum.Scanned);
            Assert.Equal(1, sum.Matched);
            Assert.Equal(1, sum.Updated);

            var written = File.ReadAllText(file);
            Assert.Contains(
                "datum: 2026-06-18, env: dev, modus: d365testcenter, ergebnis: \"1/1 PASS (50s)\"", written);
            Assert.Contains("d365tc_lauf_status: verifiziert", written);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SyncDefinitions_NoMatch_LeavesFileUntouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "DEMO-TC1.md");
            File.WriteAllText(file, Md);

            var results = new List<TestCaseResult> { Tc("OTHER-TC9", TestOutcome.Passed) };
            var sum = ResultSync.SyncDefinitions(results, dir, new DateTime(2026, 6, 18), "dev");

            Assert.Equal(1, sum.Scanned);
            Assert.Equal(0, sum.Matched);
            Assert.Equal(0, sum.Updated);
            Assert.Equal(Md, File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MapOutcome_MapsAllCodes()
    {
        var cfg = new StandardCrmConfig();
        Assert.Equal(TestOutcome.Passed, ResultSync.MapOutcome(cfg.OutcomePassed, cfg));
        Assert.Equal(TestOutcome.Failed, ResultSync.MapOutcome(cfg.OutcomeFailed, cfg));
        Assert.Equal(TestOutcome.Skipped, ResultSync.MapOutcome(cfg.OutcomeSkipped, cfg));
        Assert.Equal(TestOutcome.Error, ResultSync.MapOutcome(cfg.OutcomeError, cfg));
        Assert.Equal(TestOutcome.Error, ResultSync.MapOutcome(999999, cfg));
        Assert.Equal(TestOutcome.Error, ResultSync.MapOutcome(null, cfg));
    }

    [Theory]
    [InlineData("https://markant-dev.crm4.dynamics.com", "dev")]
    [InlineData("https://markant-test.crm4.dynamics.com", "test")]
    [InlineData("https://markant-datatest.crm4.dynamics.com", "datatest")]
    [InlineData("https://markant-cdhtest.crm4.dynamics.com", "cdhtest")]
    [InlineData("https://lmappdev.crm4.dynamics.com", "dev")]
    [InlineData("https://markant-prod.crm4.dynamics.com", "prod")]
    public void DeriveEnv_FromHost(string org, string expected)
        => Assert.Equal(expected, ResultSync.DeriveEnv(org));
}
