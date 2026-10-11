using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using D365TestCenter.Core.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>
/// The pack pipeline (build-pack, import-pack, pack reader) must transport ISO date strings
/// verbatim. With Newtonsoft's default DateParseHandling.DateTime, JObject.Parse turned
/// "2026-06-01T08:00:00.0000000Z" into a date token that was written back as
/// "2026-06-01T08:00:00Z", so the definition in Dataverse no longer matched the Markdown source
/// and an Equals assert against the O-format of the actual value failed.
/// </summary>
public class VerbatimJsonTests
{
    private const string IsoSeven = "2026-06-01T08:00:00.0000000Z";
    private const string IsoOffset = "2026-06-01T10:00:00+02:00";

    private static readonly string Definition =
        "{ \"testId\": \"ISO-01\", \"title\": \"ISO\", \"steps\": [ " +
        "{ \"stepNumber\": 1, \"action\": \"Assert\", \"field\": \"f\", \"operator\": \"Equals\", \"value\": \"" + IsoSeven + "\" }, " +
        "{ \"stepNumber\": 2, \"action\": \"Assert\", \"field\": \"g\", \"operator\": \"Equals\", \"value\": \"" + IsoOffset + "\" } ] }";

    [Fact]
    public void DefaultJObjectParse_RewritesIsoString_DocumentsTheDefect()
    {
        // Pins why the helper exists: the default parser does not keep the text.
        var defaultParsed = JObject.Parse(Definition).ToString(Formatting.None);
        Assert.DoesNotContain(IsoSeven, defaultParsed);
    }

    [Fact]
    public void ParseObject_KeepsIsoStringsVerbatim()
    {
        var obj = VerbatimJson.ParseObject(Definition);
        var values = obj["steps"]!.Select(s => s["value"]!).ToList();

        Assert.All(values, v => Assert.Equal(JTokenType.String, v.Type));
        Assert.Equal(IsoSeven, values[0].Value<string>());
        Assert.Equal(IsoOffset, values[1].Value<string>());
        Assert.Contains(IsoSeven, obj.ToString(Formatting.None));
        Assert.Contains(IsoOffset, obj.ToString(Formatting.None));
    }

    [Fact]
    public void ParseObject_TrailingContent_Throws()
    {
        Assert.Throws<JsonReaderException>(() => VerbatimJson.ParseObject("{ \"a\": 1 } x"));
        Assert.Throws<JsonReaderException>(() => VerbatimJson.ParseObject("[ 1 ]"));
    }

    [Fact]
    public void ImportPack_BuildDefinitionJson_KeepsIsoStringsVerbatim()
    {
        var path = Path.Combine(Path.GetTempPath(), $"verbatim-{System.Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ \"testCases\": [ " + Definition + " ] }");
            var tc = Assert.Single(ImportPack.LoadTestCases(path));

            var def = ImportPack.BuildDefinitionJson(tc);

            Assert.Contains(IsoSeven, def);
            Assert.Contains(IsoOffset, def);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PackBuilder_BuildTestCase_KeepsIsoStringsVerbatim()
    {
        var md =
            "---\n" +
            "id: ISO-01\n" +
            "titel: \"ISO\"\n" +
            "status: aktiv\n" +
            "---\n\n" +
            "## D365TestCenter-Definition\n\n```json\n" + Definition + "\n```\n";

        var tc = PackBuilder.BuildTestCase(md, "ISO-01.md", new List<PackLintFinding>());

        Assert.NotNull(tc);
        var json = tc!.ToString(Formatting.None);
        Assert.Contains(IsoSeven, json);
        Assert.Contains(IsoOffset, json);
    }

    [Fact]
    public void PackFileReader_Read_KeepsIsoStringsVerbatim()
    {
        var tc = Assert.Single(PackFileReader.Read("{ \"testCases\": [ " + Definition + " ] }"));

        Assert.Equal(IsoSeven, tc.Steps[0].Value);
        Assert.Equal(IsoOffset, tc.Steps[1].Value);
    }
}
