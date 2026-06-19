using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace D365TestCenter.Core.Reporting;

/// <summary>One test definition in the inventory: front-matter facts plus its run history.</summary>
public sealed class InventoryEntry
{
    public string Id { get; set; } = "";
    public string Titel { get; set; } = "";
    public string Domaene { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> SuiteTags { get; set; } = new();
    public string Ticket { get; set; } = "";          // ticket + weitere_tickets, comma-joined
    public string LaufStatus { get; set; } = "";       // d365tc_lauf_status
    // Additive front-matter facts (parity with Markant Build-Inventar.ps1). Empty when the
    // definition carries no such field - generic, optional metadata (ADR-0002).
    public string Stufe { get; set; } = "";            // stufe (tier/level)
    public string Verantwortlich { get; set; } = "";   // verantwortlich (owner)
    public string GeschaetztMin { get; set; } = "";    // geschaetzt_min (estimated minutes)
    public string Quelle { get; set; } = "";           // quelle (source)
    public string Datei { get; set; } = "";            // relative path to the definition file
    public List<HistoryEntry> History { get; set; } = new();
}

/// <summary>The inventory over a set of test definitions.</summary>
public sealed class InventoryModel
{
    public List<InventoryEntry> Entries { get; } = new();
}

/// <summary>
/// E6 (ADR-0008): builds a management inventory over the Markdown test definitions -
/// a static overview (status/domain roll-ups + a table per domain, adopting the Markant
/// Build-Inventar.ps1 vocabulary) enriched with the run view (last run + stability trend)
/// from the front-matter <c>ergebnis_historie</c> (B4 suite trend). Pure: front-matter only,
/// no Dataverse. The trend columns are additive - empty where a definition has no history
/// (the Bridge fg-testtool defs carry none; only the DSGVO/generic defs do). The CLI owns
/// the directory walk and file write.
/// </summary>
public static class InventoryBuilder
{
    static readonly Regex StatusRegex = new Regex(@"\b(PASS|FAIL|ERROR|SKIP)\b", RegexOptions.Compiled);

    /// <summary>
    /// Parses one definition's Markdown into an <see cref="InventoryEntry"/>, or null if it
    /// has no front-matter <c>id</c> (e.g. a README or a mapping doc - not a test).
    /// </summary>
    public static InventoryEntry? BuildEntry(string markdown, string source)
    {
        var norm = MarkdownDocument.Normalize(markdown);
        if (!MarkdownDocument.TrySplitFrontmatter(norm, out var fm, out _)) return null;
        var id = MarkdownDocument.ReadScalar(fm, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var ticket = new List<string>();
        var t = MarkdownDocument.ReadScalar(fm, "ticket");
        if (!string.IsNullOrWhiteSpace(t)) ticket.Add(t!.Trim());
        foreach (var w in MarkdownDocument.ReadArray(fm, "weitere_tickets"))
            if (!string.IsNullOrWhiteSpace(w) && !ticket.Contains(w)) ticket.Add(w);

        return new InventoryEntry
        {
            Id = id!,
            Titel = MarkdownDocument.ReadScalar(fm, "titel") ?? "",
            Domaene = MarkdownDocument.ReadScalar(fm, "domaene") ?? DomainFromSource(source),
            Status = MarkdownDocument.ReadScalar(fm, "status") ?? "",
            SuiteTags = MarkdownDocument.ReadArray(fm, "suite_tags"),
            Ticket = string.Join(", ", ticket),
            LaufStatus = MarkdownDocument.ReadScalar(fm, "d365tc_lauf_status") ?? "",
            Stufe = MarkdownDocument.ReadScalar(fm, "stufe") ?? "",
            Verantwortlich = MarkdownDocument.ReadScalar(fm, "verantwortlich") ?? "",
            GeschaetztMin = MarkdownDocument.ReadScalar(fm, "geschaetzt_min") ?? "",
            Quelle = MarkdownDocument.ReadScalar(fm, "quelle") ?? "",
            Datei = source ?? "",
            History = MarkdownResultSync.ParseHistory(fm)
        };
    }

    /// <summary>Builds the inventory model from a set of definitions (source label + Markdown).</summary>
    public static InventoryModel Build(IEnumerable<(string Source, string Markdown)> defs)
    {
        var model = new InventoryModel();
        foreach (var (source, markdown) in defs)
        {
            var e = BuildEntry(markdown, source);
            if (e != null) model.Entries.Add(e);
        }
        return model;
    }

    /// <summary>Renders the inventory as Markdown (LF line endings).</summary>
    public static string Render(InventoryModel model, string title = "Inventar Integrationstests")
    {
        if (model == null) throw new ArgumentNullException(nameof(model));
        var sb = new StringBuilder();
        sb.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? "Inventar" : title).Append("\n\n");
        sb.Append("> Autogeneriert (inventory). Frontmatter ist SSOT - nicht von Hand editieren.\n\n");
        sb.Append("Gesamt: ").Append(model.Entries.Count).Append(" Test-Definitionen.\n");

        RenderRollup(sb, "Status-Verteilung", "Status", model.Entries.Select(e => Blank(e.Status)));
        RenderRollup(sb, "Domänen-Verteilung", "Domäne", model.Entries.Select(e => Blank(e.Domaene)));

        // Lauf-Status only when at least one definition carries one (additive).
        var laufStatuses = model.Entries.Where(e => !string.IsNullOrWhiteSpace(e.LaufStatus)).ToList();
        if (laufStatuses.Count > 0)
            RenderRollup(sb, "Lauf-Status-Verteilung", "Lauf-Status", laufStatuses.Select(e => e.LaufStatus));

        foreach (var domain in model.Entries.GroupBy(e => Blank(e.Domaene)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("\n## ").Append(domain.Key).Append("\n\n");
            sb.Append("| ID | Titel | Stufe | Status | Suite-Tags | Ticket | Verantw. | Min | Quelle | Letzter Lauf | Trend | Datei |\n");
            sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|");
            foreach (var e in domain.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                sb.Append("\n| ").Append(Cell(e.Id))
                  .Append(" | ").Append(Cell(e.Titel))
                  .Append(" | ").Append(Cell(e.Stufe))
                  .Append(" | ").Append(Cell(e.Status))
                  .Append(" | ").Append(Cell(string.Join(", ", e.SuiteTags)))
                  .Append(" | ").Append(Cell(e.Ticket))
                  .Append(" | ").Append(Cell(e.Verantwortlich))
                  .Append(" | ").Append(Cell(e.GeschaetztMin))
                  .Append(" | ").Append(Cell(e.Quelle))
                  .Append(" | ").Append(Cell(LastRun(e.History)))
                  .Append(" | ").Append(Cell(Trend(e.History)))
                  .Append(" | ").Append(FileLink(e.Datei)).Append(" |");
            }
            sb.Append("\n");
        }
        return sb.ToString();
    }

    // ── trend helpers ────────────────────────────────────────────────

    /// <summary>
    /// The newest history entry (by date) formatted as "Datum (ENV) Ergebnis", or "-" if
    /// none. The Ergebnis is length-capped so a free-text history note does not blow up the
    /// table cell (the common case "p/t STATUS (Ns)" is well under the cap).
    /// </summary>
    public static string LastRun(IReadOnlyList<HistoryEntry> history)
    {
        if (history == null || history.Count == 0) return "-";
        var last = history.OrderBy(h => h.Datum, StringComparer.Ordinal).Last();
        var env = string.IsNullOrWhiteSpace(last.Env) ? "" : $" ({last.Env.ToUpperInvariant()})";
        var ergebnis = last.Ergebnis ?? "";
        if (ergebnis.Length > 60) ergebnis = ergebnis.Substring(0, 60).TrimEnd() + "...";
        return $"{last.Datum}{env} {ergebnis}".Trim();
    }

    /// <summary>
    /// Stability summary over all history entries: "Nx PASS" when every run passed,
    /// "N Läufe, Xx nicht-PASS" when some did not, "-" when there is no history.
    /// </summary>
    public static string Trend(IReadOnlyList<HistoryEntry> history)
    {
        if (history == null || history.Count == 0) return "-";
        int total = history.Count;
        int nonPass = history.Count(h => !IsPass(h.Ergebnis));
        if (nonPass == 0) return $"{total}x PASS";
        return $"{total} Läufe, {nonPass}x nicht-PASS";
    }

    static bool IsPass(string ergebnis)
    {
        var m = StatusRegex.Match(ergebnis ?? "");
        return m.Success && m.Value == "PASS";
    }

    // ── render helpers ───────────────────────────────────────────────

    static void RenderRollup(StringBuilder sb, string heading, string col, IEnumerable<string> values)
    {
        sb.Append("\n## ").Append(heading).Append("\n\n");
        sb.Append("| ").Append(col).Append(" | Anzahl |\n");
        sb.Append("|---|---|");
        foreach (var g in values.GroupBy(v => v).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.Append("\n| ").Append(Cell(g.Key)).Append(" | ").Append(g.Count()).Append(" |");
        sb.Append("\n");
    }

    static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "(ohne)" : s.Trim();

    static string DomainFromSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var parts = source.Replace('\\', '/').Split('/');
        return parts.Length > 1 ? parts[0] : "";
    }

    static string Cell(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var flat = Regex.Replace(text.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
        return flat.Replace("|", "\\|");
    }

    /// <summary>
    /// Renders the definition's relative path as a Markdown link (parity with
    /// Build-Inventar.ps1). The link resolves when the inventory is written into the
    /// definitions root (the common case); empty when no path is known.
    /// </summary>
    static string FileLink(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        var path = source.Replace('\\', '/').Trim();
        var target = path.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29");
        return $"[{Cell(path)}]({target})";
    }
}
