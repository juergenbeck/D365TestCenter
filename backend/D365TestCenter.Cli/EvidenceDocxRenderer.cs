using D365TestCenter.Core;
using D365TestCenter.Core.Reporting;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace D365TestCenter.Cli;

/// <summary>
/// ADR 2026-09-16-1957: renders an <see cref="EvidenceDocument"/> as a Word document (DOCX)
/// with the same structure as <see cref="EvidenceHtmlRenderer"/>: run facts, then per test
/// case and documented step the action, the expected result, the evidence items (caption,
/// actual values, embedded images) and the executed technical steps.
/// Uses DocumentFormat.OpenXml only (no Word installation needed).
/// </summary>
public static class EvidenceDocxRenderer
{
    // A4 portrait with 2 cm margins: 17 cm text width. EMU: 1 cm = 360000.
    const long MaxImageWidthEmu = 17L * 360000;
    const long MaxImageHeightEmu = 21L * 360000;
    const string ColorPass = "2E7D32";
    const string ColorFail = "C62828";
    const string ColorMuted = "666666";

    public static void Render(EvidenceDocument doc, Func<string, byte[]?> loadImage, string outPath)
    {
        if (doc == null) throw new ArgumentNullException(nameof(doc));
        if (loadImage == null) throw new ArgumentNullException(nameof(loadImage));

        using var word = WordprocessingDocument.Create(outPath, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        AddStyles(main);
        var body = new Body();
        main.Document = new Document(body);
        uint drawingId = 1;

        body.Append(Para(EvidenceHtmlRenderer.Title(doc), "Title"));

        var runRows = new List<(string, string)>
        {
            ("Ergebnis", $"{doc.PassedCount} von {doc.TotalCount} Testfällen bestanden" +
                (doc.FailedCount + doc.ErrorCount > 0 ? $", {doc.FailedCount} fehlgeschlagen, {doc.ErrorCount} mit Fehler" : "") +
                (doc.SkippedCount > 0 ? $", {doc.SkippedCount} übersprungen" : ""))
        };
        if (!string.IsNullOrWhiteSpace(doc.Environment)) runRows.Add(("Umgebung", doc.Environment!.ToUpperInvariant()));
        if (!string.IsNullOrWhiteSpace(doc.Organization)) runRows.Add(("Organisation", doc.Organization!));
        runRows.Add(("Beginn", EvidenceDocumentBuilder.FormatLocal(doc.StartedAtUtc)));
        runRows.Add(("Ende", EvidenceDocumentBuilder.FormatLocal(doc.CompletedAtUtc)));
        if (doc.TestRunId.HasValue) runRows.Add(("Testlauf-Kennung", doc.TestRunId.Value.ToString()));
        if (!string.IsNullOrWhiteSpace(doc.Filter)) runRows.Add(("Filter", doc.Filter!));
        body.Append(KeyValueTable(runRows));

        foreach (var tc in doc.TestCases)
        {
            var heading = Para(tc.Title + " (" + EvidenceDocumentBuilder.OutcomeLabel(tc.Outcome) + ")", "Heading1");
            body.Append(heading);
            var tcRows = new List<(string, string)>
            {
                ("Testfall-Kennung", tc.TestId),
                ("Ergebnis", EvidenceDocumentBuilder.OutcomeLabel(tc.Outcome)),
                ("Dauer", EvidenceDocumentBuilder.FormatDuration(tc.DurationMs))
            };
            if (tc.Tags.Count > 0) tcRows.Add(("Tags", string.Join(", ", tc.Tags)));
            tcRows.Add(("Belege", $"{tc.Items.Count} Belege mit {tc.Items.Sum(i => i.Parts.Count)} Bildern, {tc.Steps.Count} technische Schritte"));
            body.Append(KeyValueTable(tcRows));
            if (!string.IsNullOrWhiteSpace(tc.Description)) body.Append(Para(tc.Description!));
            if (tc.Outcome != TestOutcome.Passed && !string.IsNullOrWhiteSpace(tc.ErrorMessage))
                body.Append(LabeledPara("Fehler: ", tc.ErrorMessage!.Trim(), ColorFail));

            foreach (var section in EvidenceDocumentBuilder.Group(tc))
            {
                var h = Para(section.Heading, "Heading2");
                body.Append(h);
                body.Append(LabeledPara("Status: ", EvidenceDocumentBuilder.StatusLabel(section.Status),
                    section.Status == EvidenceStepStatus.Passed ? ColorPass : section.Status == EvidenceStepStatus.Failed ? ColorFail : ColorMuted));
                if (EvidenceDocumentBuilder.ShowActionSeparately(section))
                    body.Append(LabeledPara("Durchführung: ", section.Action!.Trim(), null));
                if (!string.IsNullOrWhiteSpace(section.Expected))
                    body.Append(LabeledPara("Erwartetes Ergebnis: ", section.Expected!.Trim(), null));

                foreach (var item in section.Items)
                {
                    body.Append(LabeledPara("Beleg: ", item.Caption, null, keepNext: true));
                    body.Append(Para($"Schritt {item.StepNumber}, aufgenommen {EvidenceDocumentBuilder.FormatLocal(item.CapturedAtUtc)}", "Caption"));
                    if (item.Facts.Count > 0)
                    {
                        var rows = new List<string[]> { new[] { "Feld", "Angezeigter Wert" } };
                        rows.AddRange(item.Facts.Select(f => new[]
                        {
                            (string.IsNullOrWhiteSpace(f.Label) ? f.Field : f.Label + " (" + f.Field + ")"),
                            string.IsNullOrEmpty(f.Value) ? "leer" : f.Value!
                        }));
                        body.Append(GridTable(rows, new[] { 3600, 6000 }));
                    }
                    foreach (var part in item.Parts)
                    {
                        if (!string.IsNullOrWhiteSpace(part.Label)) body.Append(Para(part.Label!, "Caption", keepNext: true));
                        var bytes = loadImage(part.File);
                        if (bytes == null || bytes.Length == 0)
                        {
                            body.Append(LabeledPara("Bilddatei fehlt: ", part.File, ColorFail));
                            continue;
                        }
                        body.Append(ImageParagraph(main, bytes, part, drawingId++, item.Caption));
                    }
                    foreach (var w in item.Warnings) body.Append(LabeledPara("Hinweis zur Aufnahme: ", w, ColorMuted));
                }

                if (section.Steps.Count > 0)
                {
                    body.Append(Para("Ausgeführte Schritte", "Heading3"));
                    var rows = new List<string[]> { new[] { "Nr.", "Ausgeführter Schritt", "Status", "Dauer", "Rückmeldung" } };
                    rows.AddRange(section.Steps.Select(s => new[]
                    {
                        s.StepNumber.ToString(),
                        s.Description + (string.IsNullOrWhiteSpace(s.Action) ? "" : " [" + (s.Operation != null ? s.Action + " " + s.Operation : s.Action) + "]"),
                        EvidenceDocumentBuilder.StatusLabel(s.Status),
                        s.Status == EvidenceStepStatus.NotExecuted ? "" : EvidenceDocumentBuilder.FormatDuration(s.DurationMs),
                        s.Message ?? ""
                    }));
                    body.Append(GridTable(rows, new[] { 600, 3900, 1300, 1000, 2800 }));
                }
                else
                {
                    body.Append(Para("Diesem Schritt sind keine automatisierten Schritte zugeordnet.", "Caption"));
                }
            }
        }

        body.Append(Para("Erzeugt vom D365 Test Center am " + EvidenceDocumentBuilder.FormatLocal(doc.CreatedAtUtc) + ".", "Caption"));
        body.Append(new SectionProperties(
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1134, Bottom = 1134, Left = 1134U, Right = 1134U, Header = 567U, Footer = 567U }));
        main.Document.Save();
    }

    static Paragraph Para(string text, string? style = null, bool keepNext = false)
    {
        var props = new ParagraphProperties();
        if (style != null) props.Append(new ParagraphStyleId { Val = style });
        if (keepNext) props.Append(new KeepNext());
        return new Paragraph(props, TextRun(text, bold: false, color: null));
    }

    static Paragraph LabeledPara(string label, string text, string? color, bool keepNext = false)
    {
        var props = new ParagraphProperties();
        if (keepNext) props.Append(new KeepNext());
        return new Paragraph(props, TextRun(label, bold: true, color: null), TextRun(text, bold: false, color: color));
    }

    static Run TextRun(string text, bool bold, string? color)
    {
        var rp = new RunProperties();
        if (bold) rp.Append(new Bold());
        if (color != null) rp.Append(new Color { Val = color });
        var run = new Run(rp);
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.Append(new Break());
            run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }

    static Table KeyValueTable(List<(string Key, string Value)> rows)
    {
        var data = rows.Select(r => new[] { r.Key, r.Value }).ToList();
        return GridTable(data, new[] { 2600, 7000 }, headerRow: false, boldFirstColumn: true);
    }

    static Table GridTable(List<string[]> rows, int[] widthsTwips, bool headerRow = true, bool boldFirstColumn = false)
    {
        var table = new Table();
        var border = new Func<BorderType, BorderType>(b => { b.Val = BorderValues.Single; b.Size = 4; b.Color = "C8C8C8"; return b; });
        table.Append(new TableProperties(
            new TableWidth { Width = widthsTwips.Sum().ToString(), Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableBorders(
                border(new TopBorder()), border(new BottomBorder()), border(new LeftBorder()),
                border(new RightBorder()), border(new InsideHorizontalBorder()), border(new InsideVerticalBorder())),
            new TableCellMarginDefault(
                new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 80, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = 80, Type = TableWidthValues.Dxa })));
        table.Append(new TableGrid(widthsTwips.Select(w => new GridColumn { Width = w.ToString() })));

        for (int r = 0; r < rows.Count; r++)
        {
            bool isHeader = headerRow && r == 0;
            var tr = new TableRow();
            if (isHeader) tr.Append(new TableRowProperties(new TableHeader()));
            for (int c = 0; c < widthsTwips.Length; c++)
            {
                var text = c < rows[r].Length ? rows[r][c] ?? "" : "";
                var cellProps = new TableCellProperties(new TableCellWidth { Width = widthsTwips[c].ToString(), Type = TableWidthUnitValues.Dxa });
                if (isHeader) cellProps.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" });
                var p = new Paragraph(
                    new ParagraphProperties(new SpacingBetweenLines { Before = "0", After = "0" }),
                    TextRun(text, bold: isHeader || (boldFirstColumn && c == 0), color: null));
                tr.Append(new TableCell(cellProps, p));
            }
            table.Append(tr);
        }
        return table;
    }

    static Paragraph ImageParagraph(MainDocumentPart main, byte[] png, EvidenceImagePart part, uint id, string altText)
    {
        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) imagePart.FeedData(ms);
        var relId = main.GetIdOfPart(imagePart);

        var (w, h) = part.Width > 0 && part.Height > 0 ? (part.Width, part.Height) : UiAutomation.EvidenceCollector.PngSize(png);
        if (w <= 0 || h <= 0) { w = 1600; h = 900; }
        // 96 dpi: 1 px = 9525 EMU. Scale down to the text area, never up.
        long cx = w * 9525L, cy = h * 9525L;
        double scale = Math.Min(1.0, Math.Min((double)MaxImageWidthEmu / cx, (double)MaxImageHeightEmu / cy));
        cx = (long)(cx * scale);
        cy = (long)(cy * scale);

        var name = "Beleg" + id;
        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = name, Description = altText },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = name + ".png" },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(
                        new A.Blip { Embed = relId },
                        new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                        new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "BFBFBF" })) { Width = 6350 })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };

        return new Paragraph(
            new ParagraphProperties(new SpacingBetweenLines { Before = "60", After = "160" }),
            new Run(new Drawing(inline)));
    }

    static void AddStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = "Segoe UI", HighAnsi = "Segoe UI", ComplexScript = "Segoe UI", EastAsia = "Segoe UI" },
                    new FontSize { Val = "19" }, new FontSizeComplexScript { Val = "19" },
                    new Languages { Val = "de-DE" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "100", Line = "264", LineRule = LineSpacingRuleValues.Auto }))));
        styles.Append(new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
        styles.Append(HeadingStyle("Title", "Title", 36, "1F3864", 0, before: "0", after: "200"));
        styles.Append(HeadingStyle("Heading1", "heading 1", 30, "1F3864", 0, before: "480", after: "120"));
        styles.Append(HeadingStyle("Heading2", "heading 2", 24, "2F5496", 1, before: "320", after: "80"));
        styles.Append(HeadingStyle("Heading3", "heading 3", 20, "404040", 2, before: "160", after: "60"));
        styles.Append(new Style(
            new StyleName { Val = "Caption" },
            new BasedOn { Val = "Normal" },
            new StyleRunProperties(new Color { Val = ColorMuted }, new FontSize { Val = "16" }, new FontSizeComplexScript { Val = "16" }))
        { Type = StyleValues.Paragraph, StyleId = "Caption" });
        stylesPart.Styles = styles;
        stylesPart.Styles.Save();
    }

    static Style HeadingStyle(string id, string name, int halfPoints, string color, int outline, string before, string after) =>
        new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = before, After = after }, new OutlineLevel { Val = outline }),
            new StyleRunProperties(new Bold(), new Color { Val = color },
                new FontSize { Val = halfPoints.ToString() }, new FontSizeComplexScript { Val = halfPoints.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id };
}
