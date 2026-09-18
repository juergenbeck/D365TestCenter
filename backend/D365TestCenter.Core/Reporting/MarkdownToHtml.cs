using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// Minimal, dependency-free Markdown-to-HTML converter for the E4 HTML/PDF report
/// (ADR-0008). Covers exactly the constructs the test-definition doc sections use
/// (definition-format.md): paragraphs, bullet/ordered lists (with indented
/// continuation lines), pipe tables, and inline <c>**bold**</c> / <c>*italic*</c> /
/// <c>`code`</c>. Not a general Markdown engine - no headings, blockquotes, links
/// or nesting (none appear in the doc sections). Pure string logic, HTML-escaped.
/// </summary>
public static class MarkdownToHtml
{
    // Placeholder markers for extracted code spans: control chars (U+0001/U+0002)
    // that never occur in real definition text, built via (char) cast so the
    // source stays plain ASCII (no invisible literals).
    static readonly string CodeMarker = ((char)1).ToString();
    static readonly string CodeTerminator = ((char)2).ToString();

    /// <summary>Converts a Markdown fragment to an HTML fragment (no surrounding document).</summary>
    public static string Convert(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var sb = new StringBuilder();
        int i = 0;
        while (i < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }

            if (IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                i = RenderTable(sb, lines, i);
            else if (IsBullet(lines[i]))
                i = RenderList(sb, lines, i, ordered: false);
            else if (IsOrdered(lines[i]))
                i = RenderList(sb, lines, i, ordered: true);
            else
                i = RenderParagraph(sb, lines, i);
        }
        return sb.ToString();
    }

    // ── block parsers ────────────────────────────────────────────────

    static int RenderParagraph(StringBuilder sb, string[] lines, int i)
    {
        var parts = new List<string>();
        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
               && !IsBullet(lines[i]) && !IsOrdered(lines[i]) && !IsTableRow(lines[i]))
        {
            parts.Add(lines[i].Trim());
            i++;
        }
        sb.Append("<p>").Append(ConvertInline(string.Join(" ", parts))).Append("</p>\n");
        return i;
    }

    static int RenderList(StringBuilder sb, string[] lines, int i, bool ordered)
    {
        var items = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i];
            var m = ordered ? OrderedRx.Match(line) : BulletRx.Match(line);
            if (m.Success)
            {
                items.Add(m.Groups[1].Value.Trim());
                i++;
            }
            else if (items.Count > 0 && !string.IsNullOrWhiteSpace(line) && char.IsWhiteSpace(line[0])
                     && !IsTableRow(line))
            {
                // Indented continuation line of the current item.
                items[items.Count - 1] += " " + line.Trim();
                i++;
            }
            else break;
        }

        string tag = ordered ? "ol" : "ul";
        sb.Append("<").Append(tag).Append(">\n");
        foreach (var it in items)
            sb.Append("<li>").Append(ConvertInline(it)).Append("</li>\n");
        sb.Append("</").Append(tag).Append(">\n");
        return i;
    }

    static int RenderTable(StringBuilder sb, string[] lines, int i)
    {
        var header = SplitRow(lines[i]);
        i += 2; // skip header + separator
        var body = new List<string[]>();
        while (i < lines.Length && IsTableRow(lines[i]))
        {
            body.Add(SplitRow(lines[i]));
            i++;
        }

        sb.Append("<table>\n<thead><tr>");
        foreach (var c in header) sb.Append("<th>").Append(ConvertInline(c)).Append("</th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        foreach (var row in body)
        {
            sb.Append("<tr>");
            foreach (var c in row) sb.Append("<td>").Append(ConvertInline(c)).Append("</td>");
            sb.Append("</tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
        return i;
    }

    // ── inline ───────────────────────────────────────────────────────

    /// <summary>Converts an inline Markdown fragment (bold/italic/code) to HTML, no block wrapper.</summary>
    public static string ConvertInline(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        // Pull out code spans first (replaced by control-char placeholders) so
        // their content is never touched by bold/italic and plain numbers like
        // "rank 50" are never mistaken for a placeholder.
        var codes = new List<string>();
        text = Regex.Replace(text, "`([^`]+)`", m =>
        {
            codes.Add(m.Groups[1].Value);
            return CodeMarker + (codes.Count - 1).ToString(CultureInfo.InvariantCulture) + CodeTerminator;
        });

        text = Escape(text);
        text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        text = Regex.Replace(text, @"\*([^*]+)\*", "<em>$1</em>");

        text = Regex.Replace(text, CodeMarker + @"(\d+)" + CodeTerminator,
            m => "<code>" + Escape(codes[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]) + "</code>");
        return text;
    }

    /// <summary>HTML-escapes a plain-text string (text content, not attributes).</summary>
    public static string Escape(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ── recognisers ──────────────────────────────────────────────────

    static readonly Regex BulletRx = new Regex(@"^\s*[-*]\s+(.*)$", RegexOptions.Compiled);
    static readonly Regex OrderedRx = new Regex(@"^\s*\d+\.\s+(.*)$", RegexOptions.Compiled);

    static bool IsBullet(string line) => BulletRx.IsMatch(line);
    static bool IsOrdered(string line) => OrderedRx.IsMatch(line);

    static bool IsTableRow(string line)
    {
        var t = line.Trim();
        return t.Length >= 2 && t[0] == '|' && t[t.Length - 1] == '|';
    }

    static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t.IndexOf('-') < 0) return false;
        return t.All(c => c == '|' || c == '-' || c == ':' || c == ' ');
    }

    static string[] SplitRow(string line)
    {
        var t = line.Trim().Trim('|');
        return t.Split('|').Select(c => c.Trim().Replace("\\|", "|")).ToArray();
    }
}
