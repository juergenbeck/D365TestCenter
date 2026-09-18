using System.Text;
using D365TestCenter.Cli.UiAutomation;
using D365TestCenter.Core.Reporting;
using Newtonsoft.Json;

namespace D365TestCenter.Cli;

/// <summary>
/// ADR 2026-09-16-1957: file IO of the evidence documentation. Writes and reads
/// <c>evidence.json</c> and renders <c>testdokumentation.html</c> and
/// <c>testdokumentation.docx</c> next to it (images are resolved relative to the directory).
/// </summary>
public static class EvidenceOutput
{
    public const string JsonFile = "evidence.json";
    public const string HtmlFile = "testdokumentation.html";
    public const string DocxFile = "testdokumentation.docx";

    static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        // Keep date-looking strings (e.g. step messages) as they are.
        DateParseHandling = DateParseHandling.None,
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() }
    };

    public static void WriteJson(string directory, EvidenceDocument doc) =>
        File.WriteAllText(Path.Combine(directory, JsonFile), JsonConvert.SerializeObject(doc, Settings), new UTF8Encoding(false));

    public static EvidenceDocument ReadJson(string directory)
    {
        var path = Path.Combine(directory, JsonFile);
        if (!File.Exists(path)) throw new FileNotFoundException($"{JsonFile} nicht gefunden in {directory}", path);
        return JsonConvert.DeserializeObject<EvidenceDocument>(File.ReadAllText(path, Encoding.UTF8), Settings)
               ?? throw new InvalidDataException($"{path} ist leer oder ungültig.");
    }

    /// <summary>Renders HTML and DOCX from the document; returns the two written paths.</summary>
    public static (string Html, string Docx) Render(string directory, EvidenceDocument doc)
    {
        var full = Path.GetFullPath(directory);
        byte[]? Load(string rel)
        {
            var p = Path.GetFullPath(Path.Combine(full, rel.Replace('/', Path.DirectorySeparatorChar)));
            // Only files inside the evidence directory are embedded.
            if (!p.StartsWith(full, StringComparison.OrdinalIgnoreCase) || !File.Exists(p)) return null;
            return File.ReadAllBytes(p);
        }

        var htmlPath = Path.Combine(full, HtmlFile);
        File.WriteAllText(htmlPath, EvidenceHtmlRenderer.Render(doc, Load), new UTF8Encoding(false));
        var docxPath = Path.Combine(full, DocxFile);
        EvidenceDocxRenderer.Render(doc, Load, docxPath);
        return (htmlPath, docxPath);
    }

    /// <summary>Builds, writes and renders the documentation of a finished run.</summary>
    public static EvidenceDocument WriteRun(
        EvidenceCollector collector, Core.TestRunResult result, IEnumerable<Core.TestCase> definitions,
        string org, string env, string filter, Action<string> log)
    {
        var doc = EvidenceDocumentBuilder.Build(result, definitions, collector.Items, collector.Notes,
            org, env, filter, DateTime.UtcNow);
        WriteJson(collector.Directory, doc);
        var (html, docx) = Render(collector.Directory, doc);
        log($"  Testdokumentation: {doc.TestCases.Count} Testfälle, {doc.TestCases.Sum(t => t.Items.Count)} Belege, " +
            $"{doc.TestCases.Sum(t => t.Items.Sum(i => i.Parts.Count))} Bilder");
        log($"    {Path.Combine(collector.Directory, JsonFile)}");
        log($"    {html}");
        log($"    {docx}");
        return doc;
    }
}
