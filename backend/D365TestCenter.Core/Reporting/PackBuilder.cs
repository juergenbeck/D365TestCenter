using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core.Reporting;

/// <summary>Severity of a <see cref="PackLintFinding"/>.</summary>
public enum PackLintSeverity { Info, Warning, Error }

/// <summary>A single lint finding produced while building a pack from a definition.</summary>
public sealed class PackLintFinding
{
    public PackLintSeverity Severity { get; set; }
    public string Source { get; set; } = "";   // definition file name or id
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Result of building a suite pack: the pack JSON plus lint findings and counts.</summary>
public sealed class PackBuildResult
{
    public JObject Pack { get; set; } = new JObject();
    public List<PackLintFinding> Findings { get; } = new();
    public int Scanned { get; set; }       // definitions seen
    public int TestCaseCount { get; set; } // definitions that produced a test case
    public bool HasErrors => Findings.Any(f => f.Severity == PackLintSeverity.Error);
}

/// <summary>
/// B5 (ADR-0008): builds an importable suite pack from the Markdown test definitions.
/// Replaces the Markant PowerShell tooling (Build-D365TC-Pack.ps1 / Test-Definitionen.ps1)
/// with a generic, pure Core implementation (ADR-0003). Per definition it takes the
/// embedded ```json block as the executable test case and enriches it with the
/// documentation (whitelisted doc sections, <see cref="MarkdownReportGenerator.BuildDocumentation"/>)
/// and userStories (front-matter ticket + weitere_tickets), so the documentation travels
/// with the pack and import-pack can write jbe_documentation in one step. Pure: no IO.
/// </summary>
public static class PackBuilder
{
    /// <summary>
    /// Pack/test-case properties that have their own jbe_testcase column and therefore must NOT appear
    /// in the executable definition JSON (jbe_definitionjson). SSOT shared by import-pack (strip before
    /// storing) and export-defs (strip when rendering the embedded block), so the block stays the pure
    /// engine definition and never duplicates column-backed data.
    /// </summary>
    public static readonly string[] ColumnBackedProperties =
    {
        "userStories", "documentation",
        "status", "domaene", "stufe", "verantwortlich", "geschaetzt_min", "zephyr_key", "env_scope", "tickets",
    };

    /// <summary>
    /// Builds a single test case object from a definition's Markdown, or null if the
    /// definition has no usable ```json block (a draft without a block is skipped with a
    /// warning; an unparsable block is an error). Appends lint findings to <paramref name="findings"/>.
    /// JSON-centric (JObject): all block properties are preserved, the documentation/userStories
    /// are inserted before the bulky steps array for readability.
    /// </summary>
    public static JObject? BuildTestCase(string markdown, string source, List<PackLintFinding> findings)
    {
        var norm = MarkdownDocument.Normalize(markdown);
        MarkdownDocument.TrySplitFrontmatter(norm, out var fm, out _);
        var id = MarkdownDocument.ReadScalar(fm, "id");
        var status = MarkdownDocument.ReadScalar(fm, "status");

        // Archived definitions never go into a pack (decision 22, Jürgen 2026-06-19): in the
        // Markant bestand the archived defs are exactly the old fg-testtool Q2 suites, which
        // are not runnable on the ADR-0004 engine and whose runnable equivalents exist as
        // standalone definitions. Skip before touching the JSON block (a suite has one).
        if (string.Equals(status, "archiviert", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PackLintFinding
            {
                Severity = PackLintSeverity.Info,
                Source = source,
                Code = "ARCHIVED_SKIPPED",
                Message = "Archivierte Definition - nicht ins Pack aufgenommen."
            });
            return null;
        }

        var jsonBlock = MarkdownDocument.ExtractJsonBlock(norm);
        if (string.IsNullOrWhiteSpace(jsonBlock))
        {
            // A file without both an id and a block is not a test (e.g. a README) and is
            // skipped silently. A test (id present) without a block is a warning, unless draft.
            if (!string.IsNullOrWhiteSpace(id) &&
                !string.Equals(status, "entwurf", StringComparison.OrdinalIgnoreCase))
                findings.Add(new PackLintFinding
                {
                    Severity = PackLintSeverity.Warning,
                    Source = source,
                    Code = "JSON_BLOCK_MISSING",
                    Message = "Keine ```json-Definition - Test wird nicht ins Pack aufgenommen."
                });
            return null;
        }

        JObject block;
        try
        {
            block = JObject.Parse(jsonBlock);
        }
        catch (Exception ex)
        {
            findings.Add(new PackLintFinding
            {
                Severity = PackLintSeverity.Error,
                Source = source,
                Code = "JSON_BLOCK_INVALID",
                Message = "JSON-Definition nicht parsebar: " + ex.Message
            });
            return null;
        }

        var jsonId = block.Value<string>("testId") ?? block.Value<string>("id");
        if (string.IsNullOrWhiteSpace(id))
            findings.Add(new PackLintFinding
            {
                Severity = PackLintSeverity.Error,
                Source = source,
                Code = "FRONTMATTER_ID_MISSING",
                Message = "Frontmatter-'id' fehlt."
            });
        else if (!string.IsNullOrWhiteSpace(jsonId) && !string.Equals(jsonId, id, StringComparison.Ordinal))
            findings.Add(new PackLintFinding
            {
                Severity = PackLintSeverity.Error,
                Source = source,
                Code = "JSON_ID_MISMATCH",
                Message = $"JSON-testId '{jsonId}' weicht von Frontmatter-id '{id}' ab."
            });

        // Documentation from the whitelisted sections (same source as sync-docs / full report).
        var doc = MarkdownReportGenerator.ParseDefinition(norm);
        var documentation = MarkdownReportGenerator.BuildDocumentation(doc);

        // Doc-completeness lint (informational): which whitelisted sections are missing.
        var missing = MarkdownReportGenerator.FullSections
            .Where(s => !doc.Sections.TryGetValue(s, out var c) || string.IsNullOrWhiteSpace(c))
            .ToList();
        if (missing.Count > 0 && missing.Count < MarkdownReportGenerator.FullSections.Length)
            findings.Add(new PackLintFinding
            {
                Severity = PackLintSeverity.Info,
                Source = source,
                Code = "DOC_SECTION_MISSING",
                Message = "Fehlende Doku-Sektionen: " + string.Join(", ", missing)
            });

        // Assemble: important fields first, then the remaining block properties (steps, etc.).
        var tc = new JObject();
        tc["testId"] = block["testId"]?.DeepClone() ?? (JToken)(id ?? jsonId ?? "");
        if (block["title"] != null) tc["title"] = block["title"]!.DeepClone();
        if (block["category"] != null) tc["category"] = block["category"]!.DeepClone();
        if (block["tags"] != null) tc["tags"] = block["tags"]!.DeepClone();

        var userStories = BlockScalarOrCsv(block, "userStories") ?? BuildUserStories(fm);
        if (!string.IsNullOrWhiteSpace(userStories)) tc["userStories"] = userStories;
        if (!string.IsNullOrWhiteSpace(documentation)) tc["documentation"] = documentation;

        // MVP-3 Phase 6a (ADR-0009): the dedicated metadata from the front-matter (A.1 mapping).
        // These keys live only in the front-matter (not the JSON block), so import-pack can map them
        // onto the jbe_testcase columns (jbe_lifecyclestatus/jbe_domain/...). Only non-empty values
        // are written so the pack stays lean; BuildDefinitionJson drops them again so they do not
        // leak into jbe_definitionjson (each has its own column). env_scope may be a YAML list, so it
        // is read as an array and comma-joined; tickets reuses the ticket + weitere_tickets CSV.
        SetIfPresent(tc, "status", MarkdownDocument.ReadScalar(fm, "status"));
        SetIfPresent(tc, "domaene", MarkdownDocument.ReadScalar(fm, "domaene"));
        SetIfPresent(tc, "stufe", MarkdownDocument.ReadScalar(fm, "stufe"));
        SetIfPresent(tc, "verantwortlich", MarkdownDocument.ReadScalar(fm, "verantwortlich"));
        SetIfPresent(tc, "geschaetzt_min", MarkdownDocument.ReadScalar(fm, "geschaetzt_min"));
        SetIfPresent(tc, "zephyr_key", MarkdownDocument.ReadScalar(fm, "zephyr_key"));
        SetIfPresent(tc, "env_scope", string.Join(",", MarkdownDocument.ReadArray(fm, "env_scope")));
        SetIfPresent(tc, "tickets", BuildUserStories(fm));   // ticket + weitere_tickets, comma-joined

        foreach (var p in block.Properties())
            if (tc[p.Name] == null) tc[p.Name] = p.Value.DeepClone();

        return tc;
    }

    /// <summary>
    /// Builds a suite pack <c>{ name, testCases: [...] }</c> from a set of definitions
    /// (source label + Markdown). Definitions are taken in the given order (the CLI sorts
    /// them for a deterministic pack). Collects all lint findings and counts.
    /// </summary>
    public static PackBuildResult BuildPack(IEnumerable<(string Source, string Markdown)> defs, string packName)
    {
        var result = new PackBuildResult();
        var testCases = new JArray();
        foreach (var (source, markdown) in defs)
        {
            result.Scanned++;
            var tc = BuildTestCase(markdown, source, result.Findings);
            if (tc != null) testCases.Add(tc);
        }
        result.TestCaseCount = testCases.Count;
        result.Pack = new JObject
        {
            ["name"] = string.IsNullOrWhiteSpace(packName) ? "TestPack" : packName,
            ["testCases"] = testCases
        };
        return result;
    }

    /// <summary>Sets a string property on the pack test case only when the value is non-empty (trimmed).</summary>
    static void SetIfPresent(JObject tc, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) tc[key] = value!.Trim();
    }

    /// <summary>
    /// Reads a block property as a scalar string, tolerating a JSON array (joined with commas) - some
    /// stored/edited definitions carry userStories as an array. Null when absent or json-null. Avoids
    /// the InvalidCastException that <c>JToken.Value&lt;string&gt;</c> throws on a JArray.
    /// </summary>
    static string? BlockScalarOrCsv(JObject block, string key)
    {
        var tok = block[key];
        if (tok == null || tok.Type == JTokenType.Null) return null;
        if (tok is JArray arr) return string.Join(",", arr.Select(t => t.ToString()));
        return tok.ToString();
    }

    /// <summary>Builds the userStories CSV from the front-matter ticket + weitere_tickets.</summary>
    static string BuildUserStories(string frontmatter)
    {
        var parts = new List<string>();
        var ticket = MarkdownDocument.ReadScalar(frontmatter, "ticket");
        if (!string.IsNullOrWhiteSpace(ticket)) parts.Add(ticket!.Trim());
        foreach (var t in MarkdownDocument.ReadArray(frontmatter, "weitere_tickets"))
            if (!string.IsNullOrWhiteSpace(t) && !parts.Contains(t)) parts.Add(t);
        return string.Join(",", parts);
    }
}
