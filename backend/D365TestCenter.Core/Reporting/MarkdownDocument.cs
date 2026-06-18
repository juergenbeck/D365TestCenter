using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// Shared, pure helpers for the Markdown test-definition format used by the
/// reporting features (E2 round-trip and E3 report, ADR-0008): YAML front-matter
/// splitting, scalar reads, and "## " section extraction. No IO, no Dataverse.
/// Single source of truth so the round-trip and the report parse the format the
/// same way.
/// </summary>
public static class MarkdownDocument
{
    /// <summary>Normalizes line endings to "\n" (CRLF/CR -> LF).</summary>
    public static string Normalize(string s) =>
        s == null ? "" : s.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Splits a normalized Markdown string into its leading YAML front-matter
    /// block and the body. Returns false if there is no "---"-fenced front-matter.
    /// Expects already-normalized input (see <see cref="Normalize"/>).
    /// </summary>
    public static bool TrySplitFrontmatter(string normalizedMd, out string frontmatter, out string body)
    {
        frontmatter = "";
        body = "";
        if (normalizedMd == null) return false;
        var lines = normalizedMd.Split('\n');
        if (lines.Length < 2 || lines[0].TrimEnd() != "---") return false;
        int close = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") { close = i; break; }
        }
        if (close < 0) return false;
        frontmatter = string.Join("\n", lines.Skip(1).Take(close - 1));
        body = string.Join("\n", lines.Skip(close + 1));
        return true;
    }

    /// <summary>
    /// Reads a top-level scalar value from a front-matter block (e.g. "id" or
    /// "titel"). Trims surrounding single or double quotes. Null if the key is
    /// absent. Only matches a key at the start of a line (no nested list items).
    /// </summary>
    public static string? ReadScalar(string frontmatter, string key)
    {
        if (string.IsNullOrEmpty(frontmatter) || string.IsNullOrEmpty(key)) return null;
        var m = Regex.Match(frontmatter,
            @"^[ \t]*" + Regex.Escape(key) + @":[ \t]*(?<v>.*?)[ \t]*$",
            RegexOptions.Multiline);
        if (!m.Success) return null;
        return StripQuotes(m.Groups["v"].Value.Trim());
    }

    /// <summary>
    /// Extracts the level-2 ("## ") sections of a body into heading -> content.
    /// Content is everything up to the next "## " heading, trimmed. Level-3+
    /// headings stay part of the enclosing section. First occurrence wins on
    /// duplicate headings. Comparison is case-insensitive.
    /// </summary>
    public static Dictionary<string, string> SplitSections(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(body)) return result;

        var lines = Normalize(body).Split('\n');
        string? currentHeading = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (currentHeading != null && !result.ContainsKey(currentHeading))
                result[currentHeading] = string.Join("\n", buffer).Trim();
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^##[ \t]+(?<h>.+?)[ \t]*$");
            if (m.Success)
            {
                Flush();
                currentHeading = m.Groups["h"].Value.Trim();
            }
            else if (currentHeading != null)
            {
                buffer.Add(line);
            }
        }
        Flush();
        return result;
    }

    /// <summary>Reads the first level-1 ("# ") heading text of a body. Null if absent.</summary>
    public static string? ReadFirstHeading(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return null;
        var m = Regex.Match(Normalize(markdown), @"^#[ \t]+(?<h>.+?)[ \t]*$", RegexOptions.Multiline);
        return m.Success ? m.Groups["h"].Value.Trim() : null;
    }

    static string StripQuotes(string v)
    {
        if (v.Length >= 2 &&
            ((v[0] == '"' && v[v.Length - 1] == '"') || (v[0] == '\'' && v[v.Length - 1] == '\'')))
            return v.Substring(1, v.Length - 2);
        return v;
    }
}
