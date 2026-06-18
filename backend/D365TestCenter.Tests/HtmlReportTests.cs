using System;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using Xunit;

namespace D365TestCenter.Tests;

/// <summary>E4 (ADR-0008): the Markdown-to-HTML converter.</summary>
public class MarkdownToHtmlTests
{
    [Fact]
    public void Paragraph_BoldAndCode()
    {
        var html = MarkdownToHtml.Convert("Das ist **wichtig** und `markant_adresse` hier.");
        Assert.Contains("<p>", html);
        Assert.Contains("<strong>wichtig</strong>", html);
        Assert.Contains("<code>markant_adresse</code>", html);
    }

    [Fact]
    public void Code_WithUnderscores_NotItalic()
    {
        var html = MarkdownToHtml.Convert("`markant_isderivingaddress1fromaccount` aktiv");
        Assert.Contains("<code>markant_isderivingaddress1fromaccount</code>", html);
        Assert.DoesNotContain("<em>", html);
    }

    [Fact]
    public void PlainNumber_NotMistakenForCodePlaceholder()
    {
        // Regression: a naive code-span placeholder ("rank 50" -> codes[50]) would crash.
        var html = MarkdownToHtml.Convert("Step `Plugin` auf rank 50 und rank 55 aktiv.");
        Assert.Contains("rank 50", html);
        Assert.Contains("rank 55", html);
        Assert.Contains("<code>Plugin</code>", html);
    }

    [Fact]
    public void BulletList_WithIndentedContinuation()
    {
        var md = "- erster Punkt\n- zweiter Punkt\n  mit Fortsetzung\n";
        var html = MarkdownToHtml.Convert(md);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>erster Punkt</li>", html);
        Assert.Contains("zweiter Punkt mit Fortsetzung", html);
    }

    [Fact]
    public void OrderedList()
    {
        var html = MarkdownToHtml.Convert("1. Setup\n2. Trigger\n");
        Assert.Contains("<ol>", html);
        Assert.Contains("<li>Setup</li>", html);
        Assert.Contains("<li>Trigger</li>", html);
    }

    [Fact]
    public void Table()
    {
        var md = "| Modus | Träger |\n|---|---|\n| skript | Smoke |\n";
        var html = MarkdownToHtml.Convert(md);
        Assert.Contains("<table>", html);
        Assert.Contains("<th>Modus</th>", html);
        Assert.Contains("<td>skript</td>", html);
    }

    [Fact]
    public void EscapesHtml()
    {
        var html = MarkdownToHtml.Convert("a < b & c > d");
        Assert.Contains("a &lt; b &amp; c &gt; d", html);
    }
}

/// <summary>E4 (ADR-0008): the self-contained HTML run report.</summary>
public class HtmlReportRendererTests
{
    static ReportModel Sample()
    {
        var m = new ReportModel
        {
            SuiteTitle = "DYN-10000 Test",
            SuiteIntro = "Worum es geht.\n\nZweiter Absatz.",
            SuiteCarrier = "| M | T |\n|---|---|\n| skript | x |",
            RunDate = "2026-06-18",
            Env = "dev",
            RunId = Guid.Parse("787a059e-8c6a-f111-a826-7c1e528427dd"),
            Filter = "DYN10000-*",
            Total = 1,
            Passed = 1,
            DurationSeconds = 16
        };
        var it = new ReportItem
        {
            TestId = "DYN10000-TC8",
            Titel = "Adresse beim Anlegen",
            Outcome = TestOutcome.Passed,
            DurationMs = 15861
        };
        it.Sections["Zweck"] = "Kontakt erbt Adresse. Zweiter Satz hier.";
        it.Sections["Ablauf"] = "1. Setup\n2. Trigger";
        m.Items.Add(it);
        return m;
    }

    [Fact]
    public void Render_SelfContainedDocument()
    {
        var html = HtmlReportRenderer.Render(Sample(), ReportDetail.Full);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("<title>Durchführungsbericht: DYN-10000 Test</title>", html);
        Assert.DoesNotContain("http://", html);   // no external assets
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Render_Compact_TableAndBadge()
    {
        var html = HtmlReportRenderer.Render(Sample(), ReportDetail.Compact);
        Assert.Contains("<table>", html);
        Assert.Contains("DYN10000-TC8", html);
        Assert.Contains("class=\"badge pass\"", html);
        Assert.Contains("Kontakt erbt Adresse.", html);
        Assert.DoesNotContain("Zweiter Satz hier", html);          // first sentence only
        Assert.DoesNotContain("<div class=\"testblock", html);     // no per-test blocks
    }

    [Fact]
    public void Render_Full_TestBlocksAndSections()
    {
        var html = HtmlReportRenderer.Render(Sample(), ReportDetail.Full);
        Assert.Contains("class=\"testblock pass\"", html);
        Assert.Contains("<h4>Zweck</h4>", html);
        Assert.Contains("Zweiter Satz hier", html);                // full content
        Assert.Contains("<h4>Ablauf</h4>", html);
        Assert.Contains("<ol>", html);                             // Ablauf as ordered list
        Assert.Contains("Träger-Modell", html);                    // carrier shown in full
    }

    [Fact]
    public void Render_Full_ErrorMessageOnFailure()
    {
        var m = Sample();
        m.Passed = 0;
        m.Failed = 1;
        m.Items[0].Outcome = TestOutcome.Failed;
        m.Items[0].ErrorMessage = "Assert fehlgeschlagen";
        var html = HtmlReportRenderer.Render(m, ReportDetail.Full);
        Assert.Contains("class=\"badge fail\"", html);
        Assert.Contains("error-msg", html);
        Assert.Contains("Assert fehlgeschlagen", html);
        Assert.Contains("0/1 FAIL", html);
    }
}
