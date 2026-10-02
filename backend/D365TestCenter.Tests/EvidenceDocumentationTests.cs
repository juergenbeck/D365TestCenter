using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using D365TestCenter.Cli;
using D365TestCenter.Cli.UiAutomation;
using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using DocumentFormat.OpenXml.Packaging;
using Newtonsoft.Json;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace D365TestCenter.Tests;

/// <summary>
/// ADR 2026-09-16-1957: evidence documentation (screenshot metadata, docSteps grouping,
/// HTML and DOCX rendering, evidence.json round trip).
/// </summary>
public class EvidenceDocumentationTests
{
    // 1x1 transparent PNG.
    static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public void NewDefinitionFields_Deserialize()
    {
        var json = @"{ ""id"": ""T1"", ""title"": ""t"",
            ""docSteps"": [ { ""number"": 1, ""action"": ""Firma anlegen"", ""expected"": ""Firma gespeichert"" } ],
            ""steps"": [ { ""stepNumber"": 1, ""action"": ""BrowserAction"", ""operation"": ""screenshot"", ""docStep"": 1,
                ""caption"": ""Firma gespeichert"", ""highlightFields"": [""name""], ""clip"": ""viewport"",
                ""hideOverlays"": false, ""hideSelectors"": [""#x""] } ] }";
        var tc = JsonConvert.DeserializeObject<TestCase>(json)!;
        Assert.Single(tc.DocSteps!);
        Assert.Equal("Firma gespeichert", tc.DocSteps![0].Expected);
        var s = tc.Steps[0];
        Assert.Equal(1, s.DocStep);
        Assert.Equal("Firma gespeichert", s.Caption);
        Assert.Equal(new[] { "name" }, s.HighlightFields);
        Assert.Equal("viewport", s.Clip);
        Assert.False(s.HideOverlays);
        Assert.Equal(new[] { "#x" }, s.HideSelectors);
    }

    [Fact]
    public void FrameFallback_DeserializesAndDefaultsToUnset()
    {
        var off = JsonConvert.DeserializeObject<TestStep>(@"{ ""stepNumber"": 1, ""action"": ""BrowserAction"", ""operation"": ""evaluate"", ""frameFallback"": false }")!;
        Assert.False(off.FrameFallback);
        var unset = JsonConvert.DeserializeObject<TestStep>(@"{ ""stepNumber"": 1, ""action"": ""BrowserAction"", ""operation"": ""evaluate"" }")!;
        Assert.Null(unset.FrameFallback);
    }

    [Fact]
    public void OldDefinition_HasNoEvidenceFields_AndRunResultOmitsRunId()
    {
        var tc = JsonConvert.DeserializeObject<TestCase>(@"{ ""id"": ""T1"", ""steps"": [ { ""stepNumber"": 1, ""action"": ""Wait"" } ] }")!;
        Assert.Null(tc.DocSteps);
        Assert.Null(tc.Steps[0].DocStep);
        Assert.Null(tc.Steps[0].HighlightFields);
        Assert.DoesNotContain("testRunId", JsonConvert.SerializeObject(new TestRunResult()));
    }

    static (TestRunResult run, TestCase def) SampleRun()
    {
        var def = new TestCase
        {
            Id = "UI-1",
            Title = "Lead erhält Kontakt",
            Description = "Beschreibung <mit> Sonderzeichen & Co.",
            Tags = new List<string> { "PROJ-1", "ui" },
            DocSteps = new List<DocStepDefinition>
            {
                new() { Number = 1, Action = "Firma anlegen", Expected = "Firma ist gespeichert" },
                new() { Number = 2, Title = "Lead prüfen", Action = "Lead öffnen.\nFelder Kontakt und E-Mail ablesen.", Expected = "Kontakt ist gesetzt" },
                new() { Number = 3, Action = "Manuell prüfen", Expected = "nur manuell" }
            },
            Steps = new List<TestStep>
            {
                new() { StepNumber = 1, Action = "BrowserAction", Operation = "navigate", Description = "Formular öffnen" },
                new() { StepNumber = 2, Action = "BrowserAction", Operation = "evaluate", Description = "Firma speichern", DocStep = 1 },
                new() { StepNumber = 3, Action = "BrowserAction", Operation = "screenshot", Description = "Bild Firma" },
                new() { StepNumber = 4, Action = "BrowserAction", Operation = "evaluate", Description = "Lead prüfen", DocStep = 2 },
                new() { StepNumber = 5, Action = "BrowserAction", Operation = "screenshot", Description = "Bild Lead" }
            }
        };
        var result = new TestCaseResult
        {
            TestId = "UI-1",
            Title = "Lead erhält Kontakt",
            Outcome = TestOutcome.Failed,
            DurationMs = 12345,
            ErrorMessage = "Lead prüfen fehlgeschlagen",
            StepResults = new List<StepResult>
            {
                new() { StepNumber = 1, Action = "BrowserAction", Success = true, DurationMs = 800 },
                new() { StepNumber = 2, Action = "BrowserAction", Success = true, DurationMs = 1500 },
                new() { StepNumber = 3, Action = "BrowserAction", Success = true, DurationMs = 900 },
                new() { StepNumber = 4, Action = "BrowserAction", Success = false, DurationMs = 2000, Message = "expected 'ok', got 'kontakt'" }
            }
        };
        var run = new TestRunResult
        {
            TestRunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StartedAt = new DateTime(2026, 9, 16, 18, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 9, 16, 18, 1, 0, DateTimeKind.Utc),
            TotalCount = 1,
            FailedCount = 1,
            Results = new List<TestCaseResult> { result }
        };
        return (run, def);
    }

    static EvidenceDocument SampleDocument()
    {
        var (run, def) = SampleRun();
        var items = new List<EvidenceItem>
        {
            new()
            {
                TestId = "UI-1", StepNumber = 3, Caption = "Firma nach dem Speichern <b>",
                CapturedAtUtc = new DateTime(2026, 9, 16, 18, 0, 20, DateTimeKind.Utc), Clip = "section",
                Facts = { new EvidenceFact { Field = "name", Label = "Firmenname", Value = "ACME 2026-09-16T10:00:00" },
                          new EvidenceFact { Field = "telephone1", Label = "Telefon", Value = "" } },
                Parts = { new EvidenceImagePart { File = "bilder/ui-1-s03-1.png", Label = "Firma", Width = 1, Height = 1 } },
                HiddenOverlays = { "Copilot-Datensatzzusammenfassung" }
            }
        };
        var notes = new List<EvidenceStepNote> { new() { TestId = "UI-1", StepNumber = 2, Text = "Rückgabe: ok (erwartet: ok)" } };
        return EvidenceDocumentBuilder.Build(run, new[] { def }, items, notes,
            "https://org.crm4.dynamics.com", "test", "UI-*", new DateTime(2026, 9, 16, 18, 2, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Build_JoinsDefinitionResultNotesAndItems()
    {
        var doc = SampleDocument();
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), doc.TestRunId);
        var tc = Assert.Single(doc.TestCases);
        Assert.Equal(5, tc.Steps.Count);
        Assert.Equal(new[] { 0, 1, 1, 2, 2 }, tc.Steps.Select(s => s.DocStep));
        Assert.Equal(EvidenceStepStatus.Passed, tc.Steps[1].Status);
        Assert.Equal("Rückgabe: ok (erwartet: ok)", tc.Steps[1].Message);
        Assert.Equal(EvidenceStepStatus.Failed, tc.Steps[3].Status);
        Assert.Equal("expected 'ok', got 'kontakt'", tc.Steps[3].Message);
        Assert.Equal(EvidenceStepStatus.NotExecuted, tc.Steps[4].Status);
        Assert.Single(tc.Items);
    }

    [Fact]
    public void Group_PreparationInheritanceAndUnautomatedDocStep()
    {
        var sections = EvidenceDocumentBuilder.Group(SampleDocument().TestCases[0]);
        Assert.Equal(new[] { 0, 1, 2, 3 }, sections.Select(s => s.Number));
        Assert.Equal("Vorbereitung", sections[0].Heading);
        Assert.Equal("Schritt 1: Firma anlegen", sections[1].Heading);
        Assert.Equal("Schritt 2: Lead prüfen", sections[2].Heading);
        Assert.False(EvidenceDocumentBuilder.ShowActionSeparately(sections[1]));
        Assert.True(EvidenceDocumentBuilder.ShowActionSeparately(sections[2]));
        Assert.Equal(new[] { 2, 3 }, sections[1].Steps.Select(s => s.StepNumber));
        Assert.Single(sections[1].Items);
        Assert.Equal(EvidenceStepStatus.Passed, sections[1].Status);
        Assert.Equal(EvidenceStepStatus.Failed, sections[2].Status);
        Assert.Empty(sections[3].Steps);
        Assert.Equal(EvidenceStepStatus.NotExecuted, sections[3].Status);
    }

    [Fact]
    public void Group_WithoutDocSteps_IsOneProcedureSection()
    {
        var tc = new EvidenceTestCase
        {
            TestId = "API-1",
            Steps = { new EvidenceStep { StepNumber = 1, Status = EvidenceStepStatus.Passed },
                      new EvidenceStep { StepNumber = 2, Status = EvidenceStepStatus.Passed } }
        };
        var section = Assert.Single(EvidenceDocumentBuilder.Group(tc));
        Assert.Equal("Ablauf", section.Heading);
        Assert.Equal(2, section.Steps.Count);
    }

    [Fact]
    public void Html_ContainsStepsExpectationCaptionValuesAndEmbeddedImage_Escaped()
    {
        var html = EvidenceHtmlRenderer.Render(SampleDocument(), _ => Png);
        Assert.Contains("Schritt 1: Firma anlegen", html);
        Assert.Contains("<strong>Erwartetes Ergebnis:</strong> Firma ist gespeichert", html);
        Assert.Contains("<h3>Schritt 2: Lead prüfen ", html);
        Assert.Contains("<strong>Durchführung:</strong> Lead öffnen.\nFelder Kontakt und E-Mail ablesen.", html);
        Assert.DoesNotContain("<strong>Durchführung:</strong> Firma anlegen", html);
        Assert.Contains("Firma nach dem Speichern &lt;b&gt;", html);
        Assert.DoesNotContain("Speichern <b>", html);
        Assert.Contains("Firmenname", html);
        Assert.Contains("ACME 2026-09-16T10:00:00", html);
        Assert.Contains("<span class=\"empty\">leer</span>", html);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(Png), html);
        Assert.Contains("Beschreibung &lt;mit&gt; Sonderzeichen &amp; Co.", html);
        Assert.Contains("Diesem Schritt sind keine automatisierten Schritte zugeordnet.", html);
        Assert.Contains("Nicht ausgeführt", html);
    }

    [Fact]
    public void Html_MissingImage_IsReportedNotSilentlyDropped()
    {
        var html = EvidenceHtmlRenderer.Render(SampleDocument(), _ => null);
        Assert.Contains("Bilddatei fehlt: bilder/ui-1-s03-1.png", html);
    }

    [Fact]
    public void Docx_OpensAndContainsHeadingsTablesAndOneImagePerPart()
    {
        var dir = Directory.CreateTempSubdirectory("tc-evidence-");
        try
        {
            var path = Path.Combine(dir.FullName, "doku.docx");
            EvidenceDocxRenderer.Render(SampleDocument(), _ => Png, path);
            using var word = WordprocessingDocument.Open(path, false);
            var body = word.MainDocumentPart!.Document.Body!;
            var texts = body.Descendants<W.Paragraph>().Select(p => p.InnerText).ToList();
            Assert.Contains("Schritt 1: Firma anlegen", texts);
            Assert.Contains("Schritt 2: Lead prüfen", texts);
            Assert.Contains(texts, t => t.StartsWith("Durchführung: Lead öffnen."));
            Assert.Contains(texts, t => t.StartsWith("Erwartetes Ergebnis: Firma ist gespeichert"));
            Assert.Contains(texts, t => t.StartsWith("Beleg: Firma nach dem Speichern <b>"));
            Assert.Single(body.Descendants<W.Drawing>());
            Assert.Single(word.MainDocumentPart.ImageParts);
            Assert.Contains(body.Descendants<W.TableCell>(), c => c.InnerText == "Firmenname (name)");
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void EvidenceJson_RoundTrip_RendersByteIdenticalHtml_AndKeepsDateLikeStrings()
    {
        var dir = Directory.CreateTempSubdirectory("tc-evidence-");
        try
        {
            var collector = new EvidenceCollector(dir.FullName);
            File.WriteAllBytes(collector.ToFullPath("bilder/ui-1-s03-1.png"), Png);
            var doc = SampleDocument();
            EvidenceOutput.WriteJson(dir.FullName, doc);
            var (htmlPath, docxPath) = EvidenceOutput.Render(dir.FullName, doc);
            var first = File.ReadAllBytes(htmlPath);

            var reread = EvidenceOutput.ReadJson(dir.FullName);
            Assert.Equal("ACME 2026-09-16T10:00:00", reread.TestCases[0].Items[0].Facts[0].Value);
            EvidenceOutput.Render(dir.FullName, reread);
            Assert.Equal(first, File.ReadAllBytes(htmlPath));
            Assert.True(new FileInfo(docxPath).Length > 0);
            Assert.Contains(Convert.ToBase64String(Png), System.Text.Encoding.UTF8.GetString(first));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Collector_ImagePathsAreUniqueAndSlugged_PngSizeReadsHeader()
    {
        var dir = Directory.CreateTempSubdirectory("tc-evidence-");
        try
        {
            var c = new EvidenceCollector(dir.FullName);
            var p1 = c.NewImagePath("CONTOSO-UI-1481 Nachzug", 7, "Lead nach Nachzug", 1);
            Assert.Equal("bilder/contoso-ui-1481-nachzug-s07-lead-nach-nachzug-1.png", p1);
            File.WriteAllBytes(c.ToFullPath(p1), Png);
            Assert.NotEqual(p1, c.NewImagePath("CONTOSO-UI-1481 Nachzug", 7, "Lead nach Nachzug", 1));
            Assert.Equal((1, 1), EvidenceCollector.PngSize(Png));
            Assert.Equal((0, 0), EvidenceCollector.PngSize(new byte[] { 1, 2, 3 }));
        }
        finally { dir.Delete(true); }
    }
}
