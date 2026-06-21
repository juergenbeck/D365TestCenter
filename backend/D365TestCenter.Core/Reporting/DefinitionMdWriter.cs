using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// MVP-3 (ADR-0009 Phase 6b) pure writer that renders a <see cref="DefinitionMirror"/> back into the
/// canonical Markdown test-definition format (front-matter + documentation + ```json block), the
/// inverse of <see cref="PackBuilder.BuildTestCase"/>/<see cref="MarkdownDocument"/>. No IO - the CLI
/// (export-defs) owns the directory walk and file write. Round-trip contract: the output parses back
/// through MarkdownDocument/PackBuilder to the same id/status/domaene/tickets/env_scope and steps.
///
/// Two reverse-formatting rules make the output round-trip cleanly:
/// - <c>tickets</c> (CSV) is split into the scalar <c>ticket</c> (first) + <c>weitere_tickets</c> array
///   (rest), so InventoryBuilder/PackBuilder rejoin them unchanged.
/// - <c>env_scope</c>/<c>suite_tags</c> are written as INLINE arrays (<c>[a, b]</c>), because
///   <see cref="MarkdownDocument.ReadArray"/> reads a bare CSV scalar as a single element.
/// - the definition JSON is normalized so the block carries <c>testId</c> (hand-authored convention),
///   not the stored <c>id</c>.
/// </summary>
public static class DefinitionMdWriter
{
    /// <summary>Renders the full Markdown mirror (front-matter, autogeneration note, documentation, json block). LF endings.</summary>
    public static string Render(DefinitionMirror m)
    {
        if (m == null) throw new ArgumentNullException(nameof(m));

        var sb = new StringBuilder();
        sb.Append("---\n").Append(RenderFrontmatter(m)).Append("\n---\n\n");
        sb.Append("> Autogeneriert (export-defs). Dataverse ist SSOT - nicht von Hand editieren.\n\n");

        var doc = (m.Documentation ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();
        if (doc.Length > 0) sb.Append(doc).Append("\n\n");

        sb.Append("## D365TestCenter-Definition\n\n```json\n").Append(RenderDefinitionBlock(m.DefinitionJson)).Append("\n```\n");
        return sb.ToString();
    }

    /// <summary>
    /// Renders the YAML front-matter body (without the fencing "---") in a deterministic key order.
    /// Empty fields are omitted. Scalars are quoted only when needed; tickets is split into
    /// ticket + weitere_tickets; env_scope and suite_tags are inline arrays.
    /// </summary>
    public static string RenderFrontmatter(DefinitionMirror m)
    {
        var lines = new List<string>();
        AddScalar(lines, "id", m.Id);
        AddScalar(lines, "titel", m.Titel);
        AddScalar(lines, "status", m.Status);
        AddScalar(lines, "domaene", m.Domaene);
        AddScalar(lines, "stufe", m.Stufe);
        AddScalar(lines, "verantwortlich", m.Verantwortlich);

        var tickets = SplitCsv(m.Tickets);
        if (tickets.Count > 0) AddScalar(lines, "ticket", tickets[0]);
        if (tickets.Count > 1) lines.Add("weitere_tickets: " + InlineArray(tickets.Skip(1)));

        var envScope = SplitCsv(m.EnvScope);
        if (envScope.Count > 0) lines.Add("env_scope: " + InlineArray(envScope));

        AddScalar(lines, "geschaetzt_min", m.GeschaetztMin);
        AddScalar(lines, "zephyr_key", m.ZephyrKey);

        var tags = SplitCsv(m.SuiteTags);
        if (tags.Count > 0) lines.Add("suite_tags: " + InlineArray(tags));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Normalizes the stored definition JSON for the embedded block into the canonical, pure executable
    /// shape: re-keys <c>id</c> to <c>testId</c> (testId first), and drops any column-backed property
    /// (<see cref="PackBuilder.ColumnBackedProperties"/>) that some stored/edited definitions still carry
    /// - those facts live in the front-matter, so duplicating them in the block would also break the
    /// build-pack round-trip (e.g. a userStories array). Pretty-printed. If the JSON does not parse it is
    /// emitted verbatim so the mirror stays faithful and the round-trip surfaces the problem.
    /// </summary>
    static string RenderDefinitionBlock(string? definitionJson)
    {
        var raw = (definitionJson ?? "").Trim();
        if (raw.Length == 0) return "{}";
        try
        {
            var src = JObject.Parse(raw);
            var testId = src.Value<string>("testId") ?? src.Value<string>("id");

            var obj = new JObject();
            if (!string.IsNullOrEmpty(testId)) obj["testId"] = testId;
            foreach (var p in src.Properties())
            {
                if (p.Name == "id" || p.Name == "testId") continue;
                if (PackBuilder.ColumnBackedProperties.Contains(p.Name)) continue;   // lives in front-matter
                obj[p.Name] = p.Value;
            }
            return obj.ToString(Formatting.Indented);
        }
        catch
        {
            return raw;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    static void AddScalar(List<string> lines, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) lines.Add(key + ": " + QuoteIfNeeded(value!.Trim()));
    }

    static string InlineArray(IEnumerable<string> items)
        => "[" + string.Join(", ", items.Select(i => QuoteIfNeeded(i.Trim()))) + "]";

    /// <summary>
    /// Double-quotes a scalar when it would otherwise be ambiguous YAML: contains a structural char
    /// (<c>: # [ ] , " '</c>), has leading/trailing space, or is empty. Inner quotes are escaped.
    /// </summary>
    static string QuoteIfNeeded(string v)
    {
        bool needs = v.Length == 0
            || v != v.Trim()
            || v.IndexOfAny(new[] { ':', '#', '[', ']', ',', '"', '\'' }) >= 0;
        if (!needs) return v;
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    static List<string> SplitCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new List<string>();
        return csv!.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
