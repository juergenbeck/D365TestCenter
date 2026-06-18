using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace D365TestCenter.Core.Reporting;

/// <summary>Detail level of an E3 run report.</summary>
public enum ReportDetail
{
    /// <summary>Suite header + KPI summary + one table row per test (purpose excerpt).</summary>
    Compact,
    /// <summary>Suite header + KPI summary + one section per test with all doc sections.</summary>
    Full
}

/// <summary>Doc part of a single test, parsed from its Markdown definition (front-matter + body sections).</summary>
public sealed class DefinitionDoc
{
    public string? Id { get; set; }
    public string Titel { get; set; } = "";

    /// <summary>"## " section heading -> content, e.g. "Zweck" -> "...". Case-insensitive.</summary>
    public Dictionary<string, string> Sections { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Suite-level doc, parsed from the suite README (no front-matter).</summary>
public sealed class SuiteDoc
{
    public string? Titel { get; set; }
    public string? Intro { get; set; }    // "## Worum es geht"
    public string? Carrier { get; set; }  // "## Träger-Modell"
}

/// <summary>One test in a run report: its run result merged with its definition doc.</summary>
public sealed class ReportItem
{
    public string TestId { get; set; } = "";
    public string Titel { get; set; } = "";
    public TestOutcome Outcome { get; set; }
    public long DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, string> Sections { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The full input model for one run report.</summary>
public sealed class ReportModel
{
    public string SuiteTitle { get; set; } = "";
    public string? SuiteIntro { get; set; }
    public string? SuiteCarrier { get; set; }

    public string RunDate { get; set; } = "";   // yyyy-MM-dd
    public string Env { get; set; } = "";
    public Guid RunId { get; set; }
    public string Filter { get; set; } = "";

    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Errored { get; set; }
    public int Skipped { get; set; }
    public long DurationSeconds { get; set; }

    public List<ReportItem> Items { get; set; } = new();
}

/// <summary>
/// E3 (ADR-0008): renders a Markdown run report (Durchführungsbericht) that
/// marries the test documentation (purpose etc., parsed from the local Markdown
/// definitions) with the run result (outcome/duration/error from Dataverse).
/// Pure string transforms; the CLI does the IO (directory walk, Dataverse read,
/// file write). Parsing reuses <see cref="MarkdownDocument"/> so the format is
/// read the same way as the E2 round-trip.
/// </summary>
public static class MarkdownReportGenerator
{
    /// <summary>The doc sections rendered per test in the full report, in order.</summary>
    public static readonly string[] FullSections =
        { "Zweck", "Datenkonstellation", "Vorbedingungen", "Ablauf", "Erwartetes Ergebnis" };

    // ── parsing ──────────────────────────────────────────────────────

    /// <summary>Parses a test definition Markdown into its doc part (id, titel, sections).</summary>
    public static DefinitionDoc ParseDefinition(string markdown)
    {
        var doc = new DefinitionDoc();
        if (string.IsNullOrEmpty(markdown)) return doc;

        var norm = MarkdownDocument.Normalize(markdown);
        if (MarkdownDocument.TrySplitFrontmatter(norm, out var fm, out var body))
        {
            doc.Id = MarkdownDocument.ReadScalar(fm, "id");
            doc.Titel = MarkdownDocument.ReadScalar(fm, "titel") ?? "";
            foreach (var kv in MarkdownDocument.SplitSections(body))
                doc.Sections[kv.Key] = kv.Value;
        }
        return doc;
    }

    /// <summary>Parses a suite README into its title, intro and carrier-model sections.</summary>
    public static SuiteDoc ParseReadme(string markdown)
    {
        var suite = new SuiteDoc();
        if (string.IsNullOrEmpty(markdown)) return suite;

        suite.Titel = MarkdownDocument.ReadFirstHeading(markdown);
        var sections = MarkdownDocument.SplitSections(markdown);
        if (sections.TryGetValue("Worum es geht", out var intro)) suite.Intro = intro;
        if (sections.TryGetValue("Träger-Modell", out var carrier)) suite.Carrier = carrier;
        return suite;
    }

    // ── rendering ────────────────────────────────────────────────────

    /// <summary>Renders the run report as Markdown (LF line endings).</summary>
    public static string Render(ReportModel model, ReportDetail detail)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        var sb = new StringBuilder();
        var title = string.IsNullOrWhiteSpace(model.SuiteTitle)
            ? "Durchführungsbericht"
            : $"Durchführungsbericht: {model.SuiteTitle}";
        sb.Append("# ").Append(title).Append("\n\n");

        RenderHeaderFacts(sb, model);

        // Suite intro: full paragraph in full mode, first paragraph in compact.
        if (!string.IsNullOrWhiteSpace(model.SuiteIntro))
        {
            sb.Append("\n## Worum es geht\n\n");
            sb.Append(detail == ReportDetail.Full ? model.SuiteIntro!.Trim()
                                                  : FirstParagraph(model.SuiteIntro!));
            sb.Append("\n");
        }
        if (detail == ReportDetail.Full && !string.IsNullOrWhiteSpace(model.SuiteCarrier))
        {
            sb.Append("\n## Träger-Modell\n\n").Append(model.SuiteCarrier!.Trim()).Append("\n");
        }

        if (detail == ReportDetail.Compact) RenderCompactTable(sb, model);
        else RenderFullDetail(sb, model);

        return sb.ToString();
    }

    static void RenderHeaderFacts(StringBuilder sb, ReportModel m)
    {
        sb.Append("- **Lauf:** ").Append(m.RunDate);
        if (!string.IsNullOrWhiteSpace(m.Env)) sb.Append(", Env ").Append(m.Env.ToUpperInvariant());
        sb.Append("\n");
        sb.Append("- **Ergebnis:** ").Append(m.Passed).Append("/").Append(m.Total)
          .Append(" ").Append(SuiteStatus(m))
          .Append(" (").Append(m.DurationSeconds).Append("s)");
        if (m.Failed > 0 || m.Errored > 0 || m.Skipped > 0)
        {
            sb.Append(" - ").Append(m.Failed).Append(" Failed, ")
              .Append(m.Errored).Append(" Error, ").Append(m.Skipped).Append(" Skipped");
        }
        sb.Append("\n");
        sb.Append("- **Run-ID:** ").Append(m.RunId.ToString()).Append("\n");
        if (!string.IsNullOrWhiteSpace(m.Filter))
            sb.Append("- **Filter:** ").Append(m.Filter).Append("\n");
    }

    static void RenderCompactTable(StringBuilder sb, ReportModel m)
    {
        sb.Append("\n## Ergebnisse\n\n");
        sb.Append("| ID | Titel | Zweck | Ergebnis | Dauer |\n");
        sb.Append("|---|---|---|---|---|");
        foreach (var it in m.Items)
        {
            it.Sections.TryGetValue("Zweck", out var zweck);
            sb.Append("\n| ").Append(EscapeCell(it.TestId))
              .Append(" | ").Append(EscapeCell(it.Titel))
              .Append(" | ").Append(EscapeCell(FirstSentence(zweck ?? "")))
              .Append(" | ").Append(OutcomeTag(it.Outcome))
              .Append(" | ").Append(Seconds(it.DurationMs)).Append("s |");
        }
        sb.Append("\n");
    }

    static void RenderFullDetail(StringBuilder sb, ReportModel m)
    {
        sb.Append("\n## Ergebnisse im Detail\n");
        foreach (var it in m.Items)
        {
            sb.Append("\n### ").Append(it.TestId)
              .Append(" - ").Append(OutcomeTag(it.Outcome))
              .Append(" (").Append(Seconds(it.DurationMs)).Append("s)\n");
            if (!string.IsNullOrWhiteSpace(it.Titel))
                sb.Append("\n**").Append(it.Titel.Trim()).Append("**\n");

            foreach (var name in FullSections)
            {
                if (it.Sections.TryGetValue(name, out var content) && !string.IsNullOrWhiteSpace(content))
                    sb.Append("\n**").Append(name).Append("**\n\n").Append(content.Trim()).Append("\n");
            }

            if (it.Outcome != TestOutcome.Passed && !string.IsNullOrWhiteSpace(it.ErrorMessage))
                sb.Append("\n**Fehler:** ").Append(it.ErrorMessage!.Trim()).Append("\n");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    static string SuiteStatus(ReportModel m)
    {
        if (m.Total > 0 && m.Passed == m.Total) return "PASS";
        if (m.Errored > 0) return "ERROR";
        if (m.Failed > 0) return "FAIL";
        return "?";
    }

    static string OutcomeTag(TestOutcome o) => o switch
    {
        TestOutcome.Passed => "PASS",
        TestOutcome.Failed => "FAIL",
        TestOutcome.Error => "ERROR",
        TestOutcome.Skipped => "SKIP",
        _ => "?"
    };

    static long Seconds(long ms) => (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>First sentence of a text, flattened to one line and length-capped (compact table cell).</summary>
    public static string FirstSentence(string text, int maxLen = 240)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var flat = Regex.Replace(text.Replace("\n", " "), @"\s+", " ").Trim();
        int idx = flat.IndexOf(". ", StringComparison.Ordinal);
        string s = idx > 0 ? flat.Substring(0, idx + 1) : flat;
        if (s.Length > maxLen) s = s.Substring(0, maxLen).TrimEnd() + "...";
        return s;
    }

    /// <summary>First paragraph (up to the first blank line) of a text, trimmed.</summary>
    public static string FirstParagraph(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var norm = MarkdownDocument.Normalize(text).Trim();
        int idx = norm.IndexOf("\n\n", StringComparison.Ordinal);
        return (idx > 0 ? norm.Substring(0, idx) : norm).Trim();
    }

    /// <summary>Flattens a value for a Markdown table cell (no newlines, escaped pipes).</summary>
    static string EscapeCell(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var flat = Regex.Replace(text.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
        return flat.Replace("|", "\\|");
    }
}
