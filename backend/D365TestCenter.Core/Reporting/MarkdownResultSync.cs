using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// One entry of the <c>ergebnis_historie</c> front-matter list of a test
/// definition (E2 round-trip, ADR-0008). <c>Datum</c> is "yyyy-MM-dd",
/// <c>Env</c> the front-matter spelling (lower-case, e.g. "dev"/"test"),
/// <c>Ergebnis</c> the formatted result string (e.g. "1/1 PASS (47s)").
/// </summary>
public sealed class HistoryEntry
{
    public string Datum { get; set; } = "";
    public string Env { get; set; } = "";
    public string Modus { get; set; } = "d365testcenter";
    public string Ergebnis { get; set; } = "";
}

/// <summary>
/// E2 (ADR-0008): writes test-run results back into the Markdown test
/// definitions. The front-matter <c>ergebnis_historie</c> is the single source
/// of truth; the body "## Ergebnis-Historie" table is rendered from it between
/// AUTO markers, so the human-readable table never drifts from the structured
/// data. Pure string transforms, no Dataverse/IO dependency (the CLI does IO).
/// </summary>
public static class MarkdownResultSync
{
    public const string MarkerStart =
        "<!-- AUTO:ergebnis-historie (sync-results pflegt diesen Block, nicht von Hand editieren) -->";
    public const string MarkerEnd = "<!-- /AUTO:ergebnis-historie -->";

    // Matches a YAML inline-map list item:
    //   - { datum: 2026-06-17, env: dev, modus: d365testcenter, ergebnis: "1/1 PASS (47s)" }
    static readonly Regex EntryRegex = new Regex(
        "-\\s*\\{\\s*datum:\\s*(?<datum>[^,}]+?)\\s*,\\s*env:\\s*(?<env>[^,}]+?)\\s*,\\s*" +
        "modus:\\s*(?<modus>[^,}]+?)\\s*,\\s*ergebnis:\\s*\"(?<ergebnis>[^\"]*)\"\\s*\\}",
        RegexOptions.Compiled);

    /// <summary>Reads the front-matter <c>id</c> (for matching testId == id). Null if absent.</summary>
    public static string? ReadFrontmatterId(string markdown)
    {
        if (markdown == null) return null;
        if (!MarkdownDocument.TrySplitFrontmatter(MarkdownDocument.Normalize(markdown), out var fm, out _))
            return null;
        return MarkdownDocument.ReadScalar(fm, "id");
    }

    /// <summary>Formats matched per-test results into "p/t STATUS (Ns)".</summary>
    public static string FormatErgebnis(IReadOnlyList<TestCaseResult> matched)
    {
        if (matched == null || matched.Count == 0) return "0/0 SKIP (0s)";
        int total = matched.Count;
        int passed = matched.Count(r => r.Outcome == TestOutcome.Passed);
        bool anyError = matched.Any(r => r.Outcome == TestOutcome.Error);
        string status = passed == total ? "PASS" : anyError ? "ERROR" : "FAIL";
        long ms = matched.Sum(r => r.DurationMs);
        long secs = (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
        return $"{passed}/{total} {status} ({secs}s)";
    }

    /// <summary>Builds a history entry from matched results, a run date, env and mode.</summary>
    public static HistoryEntry BuildEntry(
        IReadOnlyList<TestCaseResult> matched, DateTime runDate, string env, string mode = "d365testcenter")
    {
        return new HistoryEntry
        {
            Datum = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Env = (env ?? "").Trim().ToLowerInvariant(),
            Modus = string.IsNullOrWhiteSpace(mode) ? "d365testcenter" : mode.Trim(),
            Ergebnis = FormatErgebnis(matched)
        };
    }

    /// <summary>
    /// Syncs one entry into a definition's Markdown. Upserts the front-matter
    /// list (one entry per datum+env+modus, newest wins), sets
    /// <c>d365tc_lauf_status: verifiziert</c>, and re-renders the body table
    /// between AUTO markers (only if a "## Ergebnis-Historie" section exists).
    /// Throws <see cref="ArgumentException"/> if the text has no front-matter.
    /// Preserves the original line-ending style (CRLF/LF).
    /// </summary>
    public static string Sync(string markdown, HistoryEntry entry)
    {
        if (markdown == null) throw new ArgumentNullException(nameof(markdown));
        if (entry == null) throw new ArgumentNullException(nameof(entry));

        bool crlf = markdown.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
        string md = MarkdownDocument.Normalize(markdown);

        if (!MarkdownDocument.TrySplitFrontmatter(md, out var frontmatter, out var body))
            throw new ArgumentException("Markdown has no YAML front-matter block.", nameof(markdown));

        var entries = ParseHistory(frontmatter);
        Upsert(entries, entry);

        var newFrontmatter = WriteHistoryBlock(frontmatter, entries);
        newFrontmatter = SetLaufStatus(newFrontmatter, "verifiziert");

        var newBody = RenderBodyHistory(body, entries);

        string result = "---\n" + newFrontmatter + "\n---\n" + newBody;
        return crlf ? result.Replace("\n", "\r\n") : result;
    }

    /// <summary>Parses all <c>ergebnis_historie</c> inline-map entries from a front-matter block.</summary>
    public static List<HistoryEntry> ParseHistory(string frontmatter)
    {
        var list = new List<HistoryEntry>();
        if (string.IsNullOrEmpty(frontmatter)) return list;
        foreach (Match m in EntryRegex.Matches(frontmatter))
        {
            list.Add(new HistoryEntry
            {
                Datum = m.Groups["datum"].Value.Trim(),
                Env = m.Groups["env"].Value.Trim(),
                Modus = m.Groups["modus"].Value.Trim(),
                Ergebnis = m.Groups["ergebnis"].Value
            });
        }
        return list;
    }

    // ── helpers ──────────────────────────────────────────────────────

    static void Upsert(List<HistoryEntry> entries, HistoryEntry e)
    {
        int i = entries.FindIndex(x =>
            x.Datum == e.Datum
            && string.Equals(x.Env, e.Env, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Modus, e.Modus, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) entries[i] = e;
        else entries.Add(e);
    }

    static string WriteHistoryBlock(string frontmatter, List<HistoryEntry> entries)
    {
        var lines = frontmatter.Split('\n').ToList();

        var rendered = new List<string> { "ergebnis_historie:" };
        foreach (var e in entries)
            rendered.Add(
                $"  - {{ datum: {e.Datum}, env: {e.Env}, modus: {e.Modus}, ergebnis: \"{e.Ergebnis}\" }}");

        int keyIdx = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*ergebnis_historie:\s*(\[\s*\])?\s*$"));
        if (keyIdx < 0)
        {
            while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                lines.RemoveAt(lines.Count - 1);
            lines.AddRange(rendered);
            return string.Join("\n", lines);
        }

        int end = keyIdx + 1;
        while (end < lines.Count && Regex.IsMatch(lines[end], @"^\s*-\s")) end++;

        var result = new List<string>();
        result.AddRange(lines.Take(keyIdx));
        result.AddRange(rendered);
        result.AddRange(lines.Skip(end));
        return string.Join("\n", result);
    }

    static string SetLaufStatus(string frontmatter, string value)
    {
        var lines = frontmatter.Split('\n').ToList();
        int idx = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*d365tc_lauf_status:\s*"));
        if (idx >= 0)
        {
            lines[idx] = $"d365tc_lauf_status: {value}";
            return string.Join("\n", lines);
        }
        int histIdx = lines.FindIndex(l => Regex.IsMatch(l, @"^\s*ergebnis_historie:\s*"));
        var newLine = $"d365tc_lauf_status: {value}";
        if (histIdx >= 0) lines.Insert(histIdx, newLine);
        else lines.Add(newLine);
        return string.Join("\n", lines);
    }

    static string RenderBodyHistory(string body, List<HistoryEntry> entries)
    {
        string block = MarkerStart + "\n" + RenderTable(entries) + "\n" + MarkerEnd;

        int s = body.IndexOf(MarkerStart, StringComparison.Ordinal);
        int e = body.IndexOf(MarkerEnd, StringComparison.Ordinal);
        if (s >= 0 && e > s)
        {
            int eEnd = e + MarkerEnd.Length;
            return body.Substring(0, s) + block + body.Substring(eEnd);
        }

        var lines = body.Split('\n').ToList();
        int hdr = lines.FindIndex(l => Regex.IsMatch(l, @"^##\s+Ergebnis-Historie\s*$"));
        if (hdr >= 0)
        {
            int next = hdr + 1;
            while (next < lines.Count && !Regex.IsMatch(lines[next], @"^##\s")) next++;

            var result = new List<string>();
            result.AddRange(lines.Take(hdr + 1));
            result.Add("");
            result.Add(block);
            result.Add("");
            result.AddRange(lines.Skip(next));
            return string.Join("\n", result);
        }

        // No section: front-matter stays the SSOT, body untouched.
        return body;
    }

    static string RenderTable(List<HistoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("| Datum | Env | Modus | Ergebnis |\n");
        sb.Append("|---|---|---|---|");
        foreach (var e in entries)
            sb.Append($"\n| {e.Datum} | {e.Env.ToUpperInvariant()} | {e.Modus} | {e.Ergebnis} |");
        return sb.ToString();
    }
}
