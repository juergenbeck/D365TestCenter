using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Core.Reporting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// B5 (ADR-0008): Core pack building. Covers the new MarkdownDocument parser helpers
/// (ExtractJsonBlock, ReadArray) and the PackBuilder that turns Markdown definitions
/// into an importable suite pack with documentation. Pure string/JSON logic; the
/// CLI-side IO (directory walk, file write, Dataverse import) is covered elsewhere.
/// </summary>
public class PackBuilderTests
{
    // ── MarkdownDocument.ExtractJsonBlock ────────────────────────────

    [Fact]
    public void ExtractJsonBlock_SingleBlock_ReturnsObject()
    {
        var md = "## D365TestCenter-Definition\n\n```json\n{ \"testId\": \"X\" }\n```\n";
        Assert.Equal("{ \"testId\": \"X\" }", MarkdownDocument.ExtractJsonBlock(md));
    }

    [Fact]
    public void ExtractJsonBlock_NestedObject_CapturedWhole()
    {
        // The closing fence must anchor the non-greedy body so the inner "}" does not end the match.
        var md = "```json\n{ \"a\": { \"b\": 1 }, \"c\": [ { \"d\": 2 } ] }\n```\n";
        Assert.Equal("{ \"a\": { \"b\": 1 }, \"c\": [ { \"d\": 2 } ] }", MarkdownDocument.ExtractJsonBlock(md));
    }

    [Fact]
    public void ExtractJsonBlock_MultipleBlocks_LastWins()
    {
        var md = "```json\n{ \"first\": 1 }\n```\n\ntext\n\n```json\n{ \"last\": 2 }\n```\n";
        Assert.Equal("{ \"last\": 2 }", MarkdownDocument.ExtractJsonBlock(md));
    }

    [Fact]
    public void ExtractJsonBlock_NoBlock_ReturnsNull()
    {
        Assert.Null(MarkdownDocument.ExtractJsonBlock("## Zweck\n\nKein Block.\n"));
        Assert.Null(MarkdownDocument.ExtractJsonBlock(""));
    }

    // ── MarkdownDocument.ReadArray ───────────────────────────────────

    [Fact]
    public void ReadArray_Inline()
    {
        var fm = "suite_tags: [regression-cascade, recompute]\nausfuehrungs_modi: [skript, d365testcenter]";
        Assert.Equal(new[] { "regression-cascade", "recompute" }, MarkdownDocument.ReadArray(fm, "suite_tags"));
        Assert.Equal(new[] { "skript", "d365testcenter" }, MarkdownDocument.ReadArray(fm, "ausfuehrungs_modi"));
    }

    [Fact]
    public void ReadArray_Multiline_StripsQuotesAndStopsAtNextKey()
    {
        var fm = "ausfuehrungs_modi:\n  - skript\n  - \"d365testcenter\"\nticket: DYN-9149";
        Assert.Equal(new[] { "skript", "d365testcenter" }, MarkdownDocument.ReadArray(fm, "ausfuehrungs_modi"));
    }

    [Fact]
    public void ReadArray_AbsentKey_Empty()
        => Assert.Empty(MarkdownDocument.ReadArray("id: X", "suite_tags"));

    [Fact]
    public void ReadArray_InlineEmpty_Empty()
        => Assert.Empty(MarkdownDocument.ReadArray("suite_tags: []", "suite_tags"));

    // ── PackBuilder.BuildTestCase ────────────────────────────────────

    const string FullDef =
        "---\n" +
        "id: BR-CS-01\n" +
        "titel: \"ContactSource Mapping\"\n" +
        "status: aktiv\n" +
        "ticket: DYN-9149\n" +
        "weitere_tickets: [DYN-9558]\n" +
        "ausfuehrungs_modi: [skript, d365testcenter]\n" +
        "---\n\n" +
        "## Zweck\n\nMapping prüfen.\n\n" +
        "## Datenkonstellation\n\n- 1 account\n\n" +
        "## Vorbedingungen\n\nPlugin aktiv.\n\n" +
        "## Ablauf\n\n1. Setup\n\n" +
        "## Erwartetes Ergebnis\n\nGemappt.\n\n" +
        "## Ergebnis-Historie\n\n| x | y |\n\n" +
        "## D365TestCenter-Definition\n\n```json\n" +
        "{ \"testId\": \"BR-CS-01\", \"title\": \"ContactSource Mapping\", \"tags\": [\"bridge\"], " +
        "\"steps\": [ { \"stepNumber\": 1, \"action\": \"CreateRecord\" } ] }\n" +
        "```\n";

    // Archived fg-testtool suite (old Q2 format): has a JSON block, but a suite, not a test.
    // Must be skipped before the block is parsed (decision 22).
    const string ArchivedSuiteDef =
        "---\n" +
        "id: create-source\n" +
        "status: archiviert\n" +
        "---\n\n" +
        "## Beschreibung\n\ncreate-source\n\n" +
        "## D365TestCenter-Definition\n\n```json\n" +
        "{ \"suiteId\": \"FG-CreateSource\", \"testCases\": [ { \"id\": \"TCC01\", \"steps\": [] } ] }\n" +
        "```\n";

    [Fact]
    public void BuildTestCase_Archived_SkippedWithInfoNoError()
    {
        var findings = new List<PackLintFinding>();
        var tc = PackBuilder.BuildTestCase(ArchivedSuiteDef, "create-source.md", findings);

        Assert.Null(tc);
        Assert.Contains(findings, f => f.Code == "ARCHIVED_SKIPPED" && f.Severity == PackLintSeverity.Info);
        // The suite block is never parsed, so no JSON/mismatch error is produced.
        Assert.DoesNotContain(findings, f => f.Severity == PackLintSeverity.Error);
    }

    [Fact]
    public void BuildTestCase_EnrichesWithDocAndUserStories()
    {
        var findings = new List<PackLintFinding>();
        var tc = PackBuilder.BuildTestCase(FullDef, "BR-CS-01.md", findings);

        Assert.NotNull(tc);
        Assert.Equal("BR-CS-01", tc!.Value<string>("testId"));
        Assert.Equal("DYN-9149,DYN-9558", tc.Value<string>("userStories"));   // ticket + weitere_tickets
        var documentation = tc.Value<string>("documentation") ?? "";
        Assert.Contains("## Zweck", documentation);
        Assert.Contains("## Erwartetes Ergebnis", documentation);
        Assert.DoesNotContain("Ergebnis-Historie", documentation);            // not whitelisted
        Assert.NotNull(tc["steps"]);                                          // executable steps preserved
        Assert.DoesNotContain(findings, f => f.Severity == PackLintSeverity.Error);
    }

    [Fact]
    public void BuildTestCase_DocumentationBeforeSteps()
    {
        var tc = PackBuilder.BuildTestCase(FullDef, "BR-CS-01.md", new List<PackLintFinding>());
        var names = tc!.Properties().Select(p => p.Name).ToList();
        Assert.True(names.IndexOf("documentation") < names.IndexOf("steps"));
    }

    [Fact]
    public void BuildTestCase_NoBlock_NonDraft_WarnsAndSkips()
    {
        var findings = new List<PackLintFinding>();
        var tc = PackBuilder.BuildTestCase("---\nid: X\nstatus: aktiv\n---\n\n## Zweck\n\nNur Doku.\n", "X.md", findings);
        Assert.Null(tc);
        Assert.Contains(findings, f => f.Code == "JSON_BLOCK_MISSING" && f.Severity == PackLintSeverity.Warning);
    }

    [Fact]
    public void BuildTestCase_NoBlock_Draft_NoWarning()
    {
        var findings = new List<PackLintFinding>();
        var tc = PackBuilder.BuildTestCase("---\nid: X\nstatus: entwurf\n---\n\n## Zweck\n\nDraft.\n", "X.md", findings);
        Assert.Null(tc);
        Assert.Empty(findings);
    }

    [Fact]
    public void BuildTestCase_InvalidJson_Error()
    {
        var findings = new List<PackLintFinding>();
        var tc = PackBuilder.BuildTestCase("---\nid: X\n---\n\n```json\n{ \"broken\": }\n```\n", "X.md", findings);
        Assert.Null(tc);
        Assert.Contains(findings, f => f.Code == "JSON_BLOCK_INVALID" && f.Severity == PackLintSeverity.Error);
    }

    [Fact]
    public void BuildTestCase_IdMismatch_Error()
    {
        var findings = new List<PackLintFinding>();
        var tc = PackBuilder.BuildTestCase(
            "---\nid: AAA\n---\n\n```json\n{ \"testId\": \"BBB\", \"steps\": [] }\n```\n", "X.md", findings);
        Assert.Contains(findings, f => f.Code == "JSON_ID_MISMATCH" && f.Severity == PackLintSeverity.Error);
    }

    [Fact]
    public void BuildTestCase_BlockUserStories_WinOverFrontmatter()
    {
        var def = "---\nid: X\nticket: DYN-1\n---\n\n```json\n{ \"testId\": \"X\", \"userStories\": \"STORY-9\", \"steps\": [] }\n```\n";
        var tc = PackBuilder.BuildTestCase(def, "X.md", new List<PackLintFinding>());
        Assert.Equal("STORY-9", tc!.Value<string>("userStories"));
    }

    // ── PackBuilder.BuildPack ────────────────────────────────────────

    [Fact]
    public void BuildPack_AssemblesSuitePack_SkipsDraftWithoutBlock()
    {
        var defs = new[]
        {
            ("BR-CS-01.md", FullDef),
            ("draft.md", "---\nid: D\nstatus: entwurf\n---\n\n## Zweck\n\nx\n"),
        };
        var result = PackBuilder.BuildPack(defs, "MyPack");

        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.TestCaseCount);
        Assert.False(result.HasErrors);
        Assert.Equal("MyPack", result.Pack.Value<string>("name"));
        var tcs = (JArray)result.Pack["testCases"]!;
        Assert.Single(tcs);
        Assert.Equal("BR-CS-01", tcs[0]!.Value<string>("testId"));
    }

    [Fact]
    public void BuildPack_ExcludesArchivedDefinitions()
    {
        var defs = new[]
        {
            ("BR-CS-01.md", FullDef),
            ("create-source.md", ArchivedSuiteDef),   // archived suite -> excluded
        };
        var result = PackBuilder.BuildPack(defs, "Bridge");

        Assert.Equal(2, result.Scanned);
        Assert.Equal(1, result.TestCaseCount);
        Assert.False(result.HasErrors);
        var tcs = (JArray)result.Pack["testCases"]!;
        Assert.Single(tcs);
        Assert.Equal("BR-CS-01", tcs[0]!.Value<string>("testId"));
        Assert.Contains(result.Findings, f => f.Code == "ARCHIVED_SKIPPED");
    }

    // ── PackBuild (CLI walk + IO) ────────────────────────────────────

    [Fact]
    public void CollectDefinitions_ExcludesUnderscoreArchivReadme_Sorted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bridge"));
        Directory.CreateDirectory(Path.Combine(dir, "_generated"));
        Directory.CreateDirectory(Path.Combine(dir, "archiv"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "bridge", "BR-02.md"), "---\nid: BR-02\n---\n");
            File.WriteAllText(Path.Combine(dir, "bridge", "BR-01.md"), "---\nid: BR-01\n---\n");
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Readme\n");
            File.WriteAllText(Path.Combine(dir, "_generated", "GEN.md"), "---\nid: GEN\n---\n");
            File.WriteAllText(Path.Combine(dir, "archiv", "OLD.md"), "---\nid: OLD\n---\n");

            var sources = PackBuild.CollectDefinitions(dir).Select(d => d.Source).ToList();

            Assert.Equal(new[] { "bridge/BR-01.md", "bridge/BR-02.md" }, sources);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Build_ProducesPackWithEnrichedTestCase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "BR-CS-01.md"), FullDef);

            var result = PackBuild.Build(dir, "Bridge");

            Assert.Equal(1, result.TestCaseCount);
            Assert.False(result.HasErrors);
            var tcs = (JArray)result.Pack["testCases"]!;
            Assert.Equal("BR-CS-01", tcs[0]!.Value<string>("testId"));
            Assert.Contains("## Zweck", tcs[0]!.Value<string>("documentation"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── ImportPack (pure parts) ──────────────────────────────────────

    [Fact]
    public void BuildDefinitionJson_MapsTestIdToId_DropsColumnFields()
    {
        var tc = JObject.Parse(
            "{ \"testId\": \"BR-CS-01\", \"title\": \"T\", \"tags\": [\"bridge\"], " +
            "\"userStories\": \"DYN-1\", \"documentation\": \"## Zweck\", " +
            "\"steps\": [ { \"stepNumber\": 1 } ] }");

        var def = JObject.Parse(ImportPack.BuildDefinitionJson(tc));

        Assert.Equal("BR-CS-01", def.Value<string>("id"));   // testId -> id (engine model uses "id")
        Assert.Null(def["testId"]);                          // dropped
        Assert.Null(def["userStories"]);                     // own column jbe_userstories
        Assert.Null(def["documentation"]);                   // own column jbe_documentation
        Assert.NotNull(def["steps"]);                        // executable steps kept
        Assert.Equal("T", def.Value<string>("title"));
    }

    [Fact]
    public void LoadTestCases_ReadsArrayFromPack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var packFile = Path.Combine(dir, "pack.json");
            File.WriteAllText(packFile,
                "{ \"name\": \"P\", \"testCases\": [ { \"testId\": \"A\" }, { \"testId\": \"B\" } ] }");

            var tcs = ImportPack.LoadTestCases(packFile);

            Assert.Equal(2, tcs.Count);
            Assert.Equal("A", tcs[0].Value<string>("testId"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
