using System;
using System.Linq;
using System.Text;

namespace D365TestCenter.Core.Reporting;

/// <summary>
/// ADR 2026-09-16-1957: renders an <see cref="EvidenceDocument"/> as one self-contained
/// HTML file (inline CSS, images embedded as data URIs, print-friendly). Per test case:
/// header facts, then per documented step the action, the expected result, the evidence
/// items (caption, actual values, image parts) and the executed technical steps.
/// Pure string logic; the image bytes come from <paramref name="loadImage"/>.
/// </summary>
public static class EvidenceHtmlRenderer
{
    public static string Render(EvidenceDocument doc, Func<string, byte[]?> loadImage)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        if (loadImage == null) throw new ArgumentNullException(nameof(loadImage));

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html lang=\"de\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(E(Title(doc))).Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");

        sb.Append("<h1>").Append(E(Title(doc))).Append("</h1>\n");
        RenderRunFacts(sb, doc);

        if (doc.TestCases.Count > 1)
        {
            sb.Append("<nav class=\"toc\"><h2>Testfälle</h2>\n<ol>\n");
            for (int i = 0; i < doc.TestCases.Count; i++)
            {
                var tc = doc.TestCases[i];
                sb.Append("<li><a href=\"#tc").Append(i + 1).Append("\">").Append(E(tc.Title)).Append("</a> ")
                  .Append(Badge(tc.Outcome)).Append("</li>\n");
            }
            sb.Append("</ol></nav>\n");
        }

        for (int i = 0; i < doc.TestCases.Count; i++)
            RenderTestCase(sb, doc.TestCases[i], i + 1, loadImage);

        sb.Append("<footer>Erzeugt vom D365 Test Center am ").Append(E(EvidenceDocumentBuilder.FormatLocal(doc.CreatedAtUtc)))
          .Append(".</footer>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Document title, shared with the DOCX renderer.</summary>
    public static string Title(EvidenceDocument doc) =>
        doc.TestCases.Count == 1 ? "Testdokumentation: " + doc.TestCases[0].Title : "Testdokumentation";

    static void RenderRunFacts(StringBuilder sb, EvidenceDocument doc)
    {
        sb.Append("<table class=\"facts\">\n<tbody>\n");
        Row(sb, "Ergebnis", $"{doc.PassedCount} von {doc.TotalCount} Testfällen bestanden" +
            (doc.FailedCount + doc.ErrorCount > 0 ? $", {doc.FailedCount} fehlgeschlagen, {doc.ErrorCount} mit Fehler" : "") +
            (doc.SkippedCount > 0 ? $", {doc.SkippedCount} übersprungen" : ""));
        if (!string.IsNullOrWhiteSpace(doc.Environment)) Row(sb, "Umgebung", doc.Environment!.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(doc.Organization)) Row(sb, "Organisation", doc.Organization!);
        Row(sb, "Beginn", EvidenceDocumentBuilder.FormatLocal(doc.StartedAtUtc));
        Row(sb, "Ende", EvidenceDocumentBuilder.FormatLocal(doc.CompletedAtUtc));
        if (doc.TestRunId.HasValue) Row(sb, "Testlauf-Kennung", doc.TestRunId.Value.ToString());
        if (!string.IsNullOrWhiteSpace(doc.Filter)) Row(sb, "Filter", doc.Filter!);
        sb.Append("</tbody>\n</table>\n");
    }

    static void RenderTestCase(StringBuilder sb, EvidenceTestCase tc, int index, Func<string, byte[]?> loadImage)
    {
        sb.Append("<article class=\"testcase ").Append(OutcomeClass(tc.Outcome)).Append("\" id=\"tc").Append(index).Append("\">\n");
        sb.Append("<h2>").Append(E(tc.Title)).Append(" ").Append(Badge(tc.Outcome)).Append("</h2>\n");
        sb.Append("<table class=\"facts\">\n<tbody>\n");
        Row(sb, "Testfall-Kennung", tc.TestId);
        Row(sb, "Dauer", EvidenceDocumentBuilder.FormatDuration(tc.DurationMs));
        if (tc.Tags.Count > 0) Row(sb, "Tags", string.Join(", ", tc.Tags));
        int images = tc.Items.Sum(i => i.Parts.Count);
        Row(sb, "Belege", $"{tc.Items.Count} Belege mit {images} Bildern, {tc.Steps.Count} technische Schritte");
        sb.Append("</tbody>\n</table>\n");
        if (!string.IsNullOrWhiteSpace(tc.Description))
            sb.Append("<p class=\"description\">").Append(E(tc.Description!)).Append("</p>\n");
        if (tc.Outcome != TestOutcome.Passed && !string.IsNullOrWhiteSpace(tc.ErrorMessage))
            sb.Append("<div class=\"error-msg\"><strong>Fehler:</strong> ").Append(E(tc.ErrorMessage!.Trim())).Append("</div>\n");

        foreach (var section in EvidenceDocumentBuilder.Group(tc))
        {
            sb.Append("<section class=\"docstep ").Append(StatusClass(section.Status)).Append("\">\n");
            sb.Append("<h3>").Append(E(section.Heading)).Append(" <span class=\"status ").Append(StatusClass(section.Status)).Append("\">")
              .Append(E(EvidenceDocumentBuilder.StatusLabel(section.Status))).Append("</span></h3>\n");
            if (EvidenceDocumentBuilder.ShowActionSeparately(section))
                sb.Append("<p class=\"action\"><strong>Durchführung:</strong> ").Append(E(section.Action!.Trim())).Append("</p>\n");
            if (!string.IsNullOrWhiteSpace(section.Expected))
                sb.Append("<p class=\"expected\"><strong>Erwartetes Ergebnis:</strong> ").Append(E(section.Expected!.Trim())).Append("</p>\n");

            foreach (var item in section.Items) RenderItem(sb, item, loadImage);

            if (section.Steps.Count > 0)
            {
                sb.Append("<table class=\"steps\">\n<thead><tr><th>Nr.</th><th>Ausgeführter Schritt</th><th>Status</th><th>Dauer</th><th>Rückmeldung</th></tr></thead>\n<tbody>\n");
                foreach (var s in section.Steps)
                {
                    sb.Append("<tr class=\"").Append(StatusClass(s.Status)).Append("\"><td>").Append(s.StepNumber).Append("</td><td>")
                      .Append(E(s.Description)).Append("<div class=\"op\">").Append(E(s.Operation != null ? s.Action + " " + s.Operation : s.Action)).Append("</div></td><td>")
                      .Append(E(EvidenceDocumentBuilder.StatusLabel(s.Status))).Append("</td><td>")
                      .Append(s.Status == EvidenceStepStatus.NotExecuted ? "" : E(EvidenceDocumentBuilder.FormatDuration(s.DurationMs))).Append("</td><td>")
                      .Append(E(s.Message ?? "")).Append("</td></tr>\n");
                }
                sb.Append("</tbody>\n</table>\n");
            }
            else
            {
                sb.Append("<p class=\"hint\">Diesem Schritt sind keine automatisierten Schritte zugeordnet.</p>\n");
            }
            sb.Append("</section>\n");
        }
        sb.Append("</article>\n");
    }

    static void RenderItem(StringBuilder sb, EvidenceItem item, Func<string, byte[]?> loadImage)
    {
        sb.Append("<figure class=\"evidence\">\n");
        sb.Append("<figcaption><strong>Beleg:</strong> ").Append(E(item.Caption))
          .Append(" <span class=\"when\">(Schritt ").Append(item.StepNumber).Append(", aufgenommen ")
          .Append(E(EvidenceDocumentBuilder.FormatLocal(item.CapturedAtUtc))).Append(")</span></figcaption>\n");
        if (item.Facts.Count > 0)
        {
            sb.Append("<table class=\"values\">\n<thead><tr><th>Feld</th><th>Angezeigter Wert</th></tr></thead>\n<tbody>\n");
            foreach (var f in item.Facts)
            {
                sb.Append("<tr><td>").Append(E(string.IsNullOrWhiteSpace(f.Label) ? f.Field : f.Label!))
                  .Append("<div class=\"op\">").Append(E(f.Field)).Append("</div></td><td>")
                  .Append(string.IsNullOrEmpty(f.Value) ? "<span class=\"empty\">leer</span>" : E(f.Value!)).Append("</td></tr>\n");
            }
            sb.Append("</tbody>\n</table>\n");
        }
        foreach (var part in item.Parts)
        {
            var bytes = loadImage(part.File);
            sb.Append("<div class=\"part\">");
            if (!string.IsNullOrWhiteSpace(part.Label))
                sb.Append("<div class=\"partlabel\">").Append(E(part.Label!)).Append("</div>");
            if (bytes == null || bytes.Length == 0)
                sb.Append("<div class=\"error-msg\">Bilddatei fehlt: ").Append(E(part.File)).Append("</div>");
            else
                sb.Append("<img alt=\"").Append(E(item.Caption)).Append("\" src=\"data:image/png;base64,")
                  .Append(Convert.ToBase64String(bytes)).Append("\">");
            sb.Append("</div>\n");
        }
        foreach (var w in item.Warnings)
            sb.Append("<div class=\"warning\">Hinweis zur Aufnahme: ").Append(E(w)).Append("</div>\n");
        sb.Append("</figure>\n");
    }

    static void Row(StringBuilder sb, string key, string value) =>
        sb.Append("<tr><th>").Append(E(key)).Append("</th><td>").Append(E(value)).Append("</td></tr>\n");

    static string Badge(TestOutcome o) =>
        "<span class=\"badge " + OutcomeClass(o) + "\">" + E(EvidenceDocumentBuilder.OutcomeLabel(o)) + "</span>";

    static string OutcomeClass(TestOutcome o) => o switch
    {
        TestOutcome.Passed => "pass",
        TestOutcome.Failed => "fail",
        TestOutcome.Error => "error",
        _ => "skip"
    };

    static string StatusClass(EvidenceStepStatus s) => s switch
    {
        EvidenceStepStatus.Passed => "pass",
        EvidenceStepStatus.Failed => "fail",
        EvidenceStepStatus.Skipped => "skip",
        _ => "open"
    };

    static string E(string s) => MarkdownToHtml.Escape(s ?? "");

    const string Css = @"
* { box-sizing: border-box; }
body { font-family: -apple-system, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif; line-height: 1.5;
       color: #1a1a1a; background: #fff; max-width: 1100px; margin: 2rem auto; padding: 0 1.5rem; }
h1 { font-size: 1.6rem; border-bottom: 2px solid #ddd; padding-bottom: .4rem; }
h2 { font-size: 1.3rem; margin: 2.5rem 0 .6rem; }
h3 { font-size: 1.08rem; margin: 0 0 .5rem; }
table { border-collapse: collapse; width: 100%; margin: .6rem 0 1rem; font-size: .9rem; }
th, td { border: 1px solid #d0d0d0; padding: .35rem .55rem; text-align: left; vertical-align: top; }
th { background: #f4f4f4; font-weight: 600; }
table.facts th { width: 11rem; }
table.steps { font-size: .82rem; }
table.steps td:first-child { width: 3rem; }
table.values { width: auto; min-width: 50%; }
.op { color: #777; font-size: .78rem; }
.badge { display: inline-block; padding: .1rem .5rem; border-radius: 4px; font-weight: 600; color: #fff; font-size: .8rem; vertical-align: middle; }
.badge.pass { background: #2e7d32; } .badge.fail { background: #c62828; } .badge.error { background: #e65100; } .badge.skip { background: #757575; }
.status { font-size: .78rem; font-weight: 600; padding: .05rem .45rem; border-radius: 4px; vertical-align: middle; }
.status.pass { color: #1b5e20; background: #e8f5e9; } .status.fail { color: #b71c1c; background: #ffebee; }
.status.skip, .status.open { color: #555; background: #eee; }
tr.fail td { background: #fff5f5; }
.testcase { border-top: 3px solid #ddd; margin-top: 2.5rem; }
.testcase.pass { border-color: #2e7d32; } .testcase.fail { border-color: #c62828; } .testcase.error { border-color: #e65100; }
.description { color: #333; white-space: pre-line; }
.docstep { border: 1px solid #e0e0e0; border-left: 4px solid #bbb; border-radius: 4px; padding: .9rem 1rem; margin: 1.2rem 0; }
.docstep.pass { border-left-color: #2e7d32; } .docstep.fail { border-left-color: #c62828; }
.action { white-space: pre-line; }
.expected { white-space: pre-line; background: #f7f9fc; border: 1px solid #dde5f0; border-radius: 4px; padding: .45rem .65rem; }
figure.evidence { margin: 1rem 0; padding: .7rem; background: #fafafa; border: 1px solid #e6e6e6; border-radius: 4px; }
figcaption { margin-bottom: .4rem; }
.when { color: #777; font-size: .8rem; }
.part { margin: .6rem 0; }
.partlabel { font-size: .8rem; color: #555; margin-bottom: .2rem; }
.part img { max-width: 100%; height: auto; border: 1px solid #ccc; display: block; }
.empty { color: #999; font-style: italic; }
.hint { color: #777; font-style: italic; }
.warning { color: #8a6d00; background: #fff8e1; border: 1px solid #ffe082; padding: .3rem .5rem; border-radius: 4px; font-size: .82rem; }
.error-msg { background: #fff3e0; border: 1px solid #ffcc80; padding: .5rem .7rem; border-radius: 4px; margin: .6rem 0; }
.toc ol { margin: .3rem 0 0 1.2rem; padding: 0; }
footer { margin-top: 3rem; border-top: 1px solid #ddd; padding-top: .6rem; color: #888; font-size: .8rem; }
@media print {
  body { max-width: none; margin: 0; font-size: 10pt; }
  h2, h3 { page-break-after: avoid; }
  figure.evidence, tr { page-break-inside: avoid; }
}";
}
