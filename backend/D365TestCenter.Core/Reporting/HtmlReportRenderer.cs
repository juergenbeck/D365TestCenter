using System;
using System.Linq;
using System.Text;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// E4 (ADR-0008): renders a run report (<see cref="ReportModel"/>) as a
/// self-contained HTML document with inline CSS (no external assets, in the
/// spirit of golden rule 1 "Zero Dependencies"). The same HTML is the source
/// for the PDF (CLI renders it via Playwright PdfAsync). Doc sections are
/// converted from Markdown via <see cref="MarkdownToHtml"/>. Pure string logic.
/// Print-friendly (@media print) so the in-browser "Print to PDF" path also works.
/// </summary>
public static class HtmlReportRenderer
{
    /// <summary>Renders the run report as a complete HTML document.</summary>
    public static string Render(ReportModel model, ReportDetail detail)
    {
        if (model == null) throw new ArgumentNullException(nameof(model));

        string title = string.IsNullOrWhiteSpace(model.SuiteTitle)
            ? "Durchführungsbericht"
            : $"Durchführungsbericht: {model.SuiteTitle}";

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"de\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(MarkdownToHtml.Escape(title)).Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");

        sb.Append("<h1>").Append(MarkdownToHtml.ConvertInline(title)).Append("</h1>\n");
        RenderKpi(sb, model);

        if (!string.IsNullOrWhiteSpace(model.SuiteIntro))
        {
            sb.Append("<section>\n<h2>Worum es geht</h2>\n");
            sb.Append(MarkdownToHtml.Convert(
                detail == ReportDetail.Full ? model.SuiteIntro! : MarkdownReportGenerator.FirstParagraph(model.SuiteIntro!)));
            sb.Append("</section>\n");
        }
        if (detail == ReportDetail.Full && !string.IsNullOrWhiteSpace(model.SuiteCarrier))
        {
            sb.Append("<section>\n<h2>Träger-Modell</h2>\n");
            sb.Append(MarkdownToHtml.Convert(model.SuiteCarrier!));
            sb.Append("</section>\n");
        }

        if (detail == ReportDetail.Compact) RenderCompact(sb, model);
        else RenderFull(sb, model);

        sb.Append("<footer>Erzeugt vom D365 Test Center.</footer>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    static void RenderKpi(StringBuilder sb, ReportModel m)
    {
        var (status, cls) = SuiteStatus(m);
        sb.Append("<div class=\"kpi\">\n");
        sb.Append("<span class=\"badge ").Append(cls).Append("\">")
          .Append(m.Passed).Append("/").Append(m.Total).Append(" ").Append(status).Append("</span>\n");
        sb.Append("<span class=\"fact\">Lauf: ").Append(MarkdownToHtml.Escape(m.RunDate));
        if (!string.IsNullOrWhiteSpace(m.Env)) sb.Append(", Env ").Append(MarkdownToHtml.Escape(m.Env.ToUpperInvariant()));
        sb.Append("</span>\n");
        sb.Append("<span class=\"fact\">Dauer: ").Append(m.DurationSeconds).Append("s</span>\n");
        if (m.Failed > 0 || m.Errored > 0 || m.Skipped > 0)
            sb.Append("<span class=\"fact\">")
              .Append(m.Failed).Append(" Failed, ").Append(m.Errored).Append(" Error, ")
              .Append(m.Skipped).Append(" Skipped</span>\n");
        sb.Append("<div class=\"runid\">Run-ID: ").Append(MarkdownToHtml.Escape(m.RunId.ToString()));
        if (!string.IsNullOrWhiteSpace(m.Filter))
            sb.Append(" &middot; Filter: ").Append(MarkdownToHtml.Escape(m.Filter));
        sb.Append("</div>\n</div>\n");
    }

    static void RenderCompact(StringBuilder sb, ReportModel m)
    {
        sb.Append("<h2>Ergebnisse</h2>\n");
        sb.Append("<table>\n<thead><tr><th>ID</th><th>Titel</th><th>Zweck</th>")
          .Append("<th>Ergebnis</th><th>Dauer</th></tr></thead>\n<tbody>\n");
        foreach (var it in m.Items)
        {
            it.Sections.TryGetValue("Zweck", out var zweck);
            var (tag, cls) = OutcomeInfo(it.Outcome);
            sb.Append("<tr><td>").Append(MarkdownToHtml.Escape(it.TestId)).Append("</td>")
              .Append("<td>").Append(MarkdownToHtml.ConvertInline(it.Titel)).Append("</td>")
              .Append("<td>").Append(MarkdownToHtml.ConvertInline(
                  MarkdownReportGenerator.FirstSentence(zweck ?? ""))).Append("</td>")
              .Append("<td><span class=\"badge ").Append(cls).Append("\">").Append(tag).Append("</span></td>")
              .Append("<td>").Append(Seconds(it.DurationMs)).Append("s</td></tr>\n");
        }
        sb.Append("</tbody>\n</table>\n");
    }

    static void RenderFull(StringBuilder sb, ReportModel m)
    {
        sb.Append("<h2>Ergebnisse im Detail</h2>\n");
        foreach (var it in m.Items)
        {
            var (tag, cls) = OutcomeInfo(it.Outcome);
            sb.Append("<div class=\"testblock ").Append(cls).Append("\">\n");
            sb.Append("<h3>").Append(MarkdownToHtml.Escape(it.TestId))
              .Append(" <span class=\"badge ").Append(cls).Append("\">").Append(tag).Append("</span> ")
              .Append("<span class=\"dur\">(").Append(Seconds(it.DurationMs)).Append("s)</span></h3>\n");
            if (!string.IsNullOrWhiteSpace(it.Titel))
                sb.Append("<p class=\"titel\"><strong>").Append(MarkdownToHtml.ConvertInline(it.Titel))
                  .Append("</strong></p>\n");

            foreach (var name in MarkdownReportGenerator.FullSections)
            {
                if (it.Sections.TryGetValue(name, out var content) && !string.IsNullOrWhiteSpace(content))
                {
                    sb.Append("<h4>").Append(MarkdownToHtml.Escape(name)).Append("</h4>\n");
                    sb.Append(MarkdownToHtml.Convert(content));
                }
            }

            if (it.Outcome != TestOutcome.Passed && !string.IsNullOrWhiteSpace(it.ErrorMessage))
                sb.Append("<div class=\"error-msg\"><strong>Fehler:</strong> ")
                  .Append(MarkdownToHtml.Escape(it.ErrorMessage!.Trim())).Append("</div>\n");

            sb.Append("</div>\n");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────

    static (string status, string cls) SuiteStatus(ReportModel m)
    {
        if (m.Total > 0 && m.Passed == m.Total) return ("PASS", "pass");
        if (m.Errored > 0) return ("ERROR", "error");
        if (m.Failed > 0) return ("FAIL", "fail");
        return ("?", "skip");
    }

    static (string tag, string cls) OutcomeInfo(TestOutcome o) => o switch
    {
        TestOutcome.Passed => ("PASS", "pass"),
        TestOutcome.Failed => ("FAIL", "fail"),
        TestOutcome.Error => ("ERROR", "error"),
        TestOutcome.Skipped => ("SKIP", "skip"),
        _ => ("?", "skip")
    };

    static long Seconds(long ms) => (long)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);

    const string Css = @"
* { box-sizing: border-box; }
body { font-family: -apple-system, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;
       line-height: 1.5; color: #1a1a1a; max-width: 900px; margin: 2rem auto; padding: 0 1.5rem; }
h1 { font-size: 1.6rem; border-bottom: 2px solid #ddd; padding-bottom: .4rem; }
h2 { font-size: 1.25rem; margin-top: 2rem; }
h3 { font-size: 1.05rem; margin-top: 1.6rem; margin-bottom: .2rem; }
h4 { font-size: .95rem; margin: 1rem 0 .2rem; color: #333; }
p { margin: .5rem 0; }
table { border-collapse: collapse; width: 100%; margin: 1rem 0; font-size: .92rem; }
th, td { border: 1px solid #ccc; padding: .45rem .6rem; text-align: left; vertical-align: top; }
th { background: #f4f4f4; }
code { background: #f0f0f0; padding: .1em .3em; border-radius: 3px;
       font-family: ""SFMono-Regular"", Consolas, ""Liberation Mono"", monospace; font-size: .88em; }
.kpi { margin: 1rem 0 1.5rem; }
.badge { display: inline-block; padding: .15rem .55rem; border-radius: 4px; font-weight: 600;
         color: #fff; font-size: .85rem; }
.badge.pass { background: #2e7d32; }
.badge.fail { background: #c62828; }
.badge.error { background: #e65100; }
.badge.skip { background: #757575; }
.fact { margin-left: .8rem; color: #444; font-size: .9rem; }
.runid { color: #666; font-size: .82rem; margin-top: .5rem; }
.testblock { border-left: 4px solid #ddd; padding: .1rem 0 .1rem 1rem; margin: 1.4rem 0; }
.testblock.pass { border-color: #2e7d32; }
.testblock.fail { border-color: #c62828; }
.testblock.error { border-color: #e65100; }
.titel { color: #333; }
.dur { color: #888; font-weight: normal; font-size: .85rem; }
.error-msg { background: #fff3e0; border: 1px solid #ffcc80; padding: .6rem .8rem;
             border-radius: 4px; margin: .6rem 0; }
footer { margin-top: 3rem; border-top: 1px solid #ddd; padding-top: .6rem; color: #888; font-size: .8rem; }
@media print {
  body { max-width: none; margin: 0; font-size: 11pt; }
  h2, h3, h4 { page-break-after: avoid; }
  .testblock, table { page-break-inside: avoid; }
}";
}
