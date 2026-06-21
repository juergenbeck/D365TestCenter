using System.Collections.Generic;
using D365TestCenter.Core.Reporting;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// MVP-3 (ADR-0009 Phase 6b): the pure export-defs Markdown writer. The key guarantee is the
/// round-trip - the rendered mirror parses back through MarkdownDocument/PackBuilder to the same
/// metadata and steps, so export-defs and build-pack/import-pack agree. Also pins the reverse
/// formatting rules (ticket split, inline arrays, id -> testId in the block).
/// </summary>
public class DefinitionMdWriterTests
{
    static DefinitionMirror FullMirror() => new()
    {
        Id = "META-01",
        Titel = "ContactSource: Mapping",   // contains ':' -> must be quoted
        Status = "aktiv",
        Domaene = "DSGVO",
        Stufe = "2",
        Verantwortlich = "Jürgen",
        Tickets = "DYN-9149,DYN-9558",
        EnvScope = "dev,test",
        GeschaetztMin = "15",
        ZephyrKey = "DYN-T994",
        SuiteTags = "bridge,regression",
        Documentation = "## Zweck\n\nMapping prüfen.",
        DefinitionJson = "{\"id\":\"META-01\",\"title\":\"T\",\"steps\":[{\"stepNumber\":1,\"action\":\"CreateRecord\"}]}"
    };

    [Fact]
    public void Render_FrontmatterKeysAndArrays()
    {
        var md = DefinitionMdWriter.Render(FullMirror());

        Assert.Contains("id: META-01", md);
        Assert.Contains("status: aktiv", md);
        Assert.Contains("domaene: DSGVO", md);
        Assert.Contains("stufe: 2", md);
        Assert.Contains("ticket: DYN-9149", md);              // first ticket
        Assert.Contains("weitere_tickets: [DYN-9558]", md);   // remainder as array
        Assert.Contains("env_scope: [dev, test]", md);        // inline array (round-trips via ReadArray)
        Assert.Contains("suite_tags: [bridge, regression]", md);
        Assert.Contains("titel: \"ContactSource: Mapping\"", md);   // quoted (contains ':')
        Assert.Contains("## Zweck", md);
        Assert.Contains("> Autogeneriert", md);
    }

    [Fact]
    public void Render_DefinitionBlock_NormalizesIdToTestId()
    {
        var md = DefinitionMdWriter.Render(FullMirror());
        var block = MarkdownDocument.ExtractJsonBlock(md);
        var obj = JObject.Parse(block!);

        Assert.Equal("META-01", obj.Value<string>("testId"));   // stored id -> canonical testId
        Assert.Null(obj["id"]);
        Assert.NotNull(obj["steps"]);
    }

    [Fact]
    public void Render_RoundTripsThroughPackBuilder()
    {
        var md = DefinitionMdWriter.Render(FullMirror());
        var tc = PackBuilder.BuildTestCase(md, "META-01.md", new List<PackLintFinding>());

        Assert.NotNull(tc);
        Assert.Equal("META-01", tc!.Value<string>("testId"));
        Assert.Equal("aktiv", tc.Value<string>("status"));
        Assert.Equal("DSGVO", tc.Value<string>("domaene"));
        Assert.Equal("2", tc.Value<string>("stufe"));
        Assert.Equal("Jürgen", tc.Value<string>("verantwortlich"));
        Assert.Equal("15", tc.Value<string>("geschaetzt_min"));
        Assert.Equal("DYN-T994", tc.Value<string>("zephyr_key"));
        Assert.Equal("dev,test", tc.Value<string>("env_scope"));           // array -> CSV again
        Assert.Equal("DYN-9149,DYN-9558", tc.Value<string>("tickets"));    // ticket + weitere_tickets rejoined
        Assert.NotNull(tc["steps"]);
    }

    [Fact]
    public void Render_DefinitionBlock_StripsColumnBackedProps()
    {
        // Stored jbe_definitionjson may still carry column-backed facts (userStories array,
        // documentation, status). The canonical block must drop them (they live in front-matter) so
        // the mirror is not duplicated and re-parses cleanly through build-pack.
        var m = new DefinitionMirror
        {
            Id = "X",
            DefinitionJson = "{\"id\":\"X\",\"userStories\":[\"DYN-1\"],\"documentation\":\"d\",\"status\":\"aktiv\",\"steps\":[]}"
        };

        var md = DefinitionMdWriter.Render(m);
        var block = JObject.Parse(MarkdownDocument.ExtractJsonBlock(md)!);

        Assert.Equal("X", block.Value<string>("testId"));
        Assert.Null(block["userStories"]);     // column-backed -> stripped from the block
        Assert.Null(block["documentation"]);
        Assert.Null(block["status"]);
        Assert.NotNull(block["steps"]);

        // And the whole mirror re-parses through build-pack without throwing.
        var tc = PackBuilder.BuildTestCase(md, "X.md", new List<PackLintFinding>());
        Assert.NotNull(tc);
        Assert.Equal("X", tc!.Value<string>("testId"));
    }

    [Fact]
    public void Render_EmptyOptionalFields_Omitted()
    {
        var md = DefinitionMdWriter.Render(new DefinitionMirror { Id = "X", DefinitionJson = "{\"id\":\"X\",\"steps\":[]}" });

        Assert.Contains("id: X", md);
        Assert.DoesNotContain("domaene:", md);
        Assert.DoesNotContain("status:", md);
        Assert.DoesNotContain("env_scope:", md);
        Assert.Contains("```json", md);
    }

    [Fact]
    public void Render_UnparsableDefinition_EmittedVerbatim()
    {
        var md = DefinitionMdWriter.Render(new DefinitionMirror { Id = "X", DefinitionJson = "{ broken" });
        Assert.Contains("{ broken", md);
    }
}
