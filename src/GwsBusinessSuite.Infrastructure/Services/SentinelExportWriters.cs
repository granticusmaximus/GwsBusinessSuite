using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using QColors = QuestPDF.Helpers.Colors;
using QDocument = QuestPDF.Fluent.Document;
using Color = DocumentFormat.OpenXml.Wordprocessing.Color;
using Document = DocumentFormat.OpenXml.Wordprocessing.Document;
using PageSize = DocumentFormat.OpenXml.Wordprocessing.PageSize;

namespace GwsBusinessSuite.Infrastructure.Services;

// The two file formats for SentinelPageExportService, both drawn from SentinelExportFlattener's
// elements so a PDF and a .docx of the same page say the same thing.
public static class SentinelExportWriters
{
    // The production container only has the Liberation fonts (see ResumePdfService).
    private const string BodyFont = "Liberation Sans";
    private const string MonoFont = "Liberation Mono";
    private const string Ink = "#1F2933";
    private const string Muted = "#6B7280";
    private const string Rule = "#D1D5DB";
    private const string CodeBackground = "#F3F4F6";
    private const string CalloutBackground = "#F1F5F9";

    static SentinelExportWriters()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // A glyph the fonts lack (an emoji, say) prints as a blank rather than failing the export.
        QuestPDF.Settings.CheckIfAllTextGlyphsAreAvailable = false;
    }

    // ---- PDF ----

    public static byte[] RenderPdf(SentinelExportDocument document) =>
        QDocument.Create(container => container.Page(page =>
        {
            page.Size(QuestPDF.Helpers.PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(x => x.FontFamily(BodyFont).FontSize(10.5f).FontColor(Ink).LineHeight(1.35f));
            page.Content().Column(column =>
            {
                column.Spacing(6);
                for (var index = 0; index < document.Pages.Count; index++)
                {
                    var exportPage = document.Pages[index];
                    if (index > 0) column.Item().PageBreak();
                    if (exportPage.Depth > 0)
                        column.Item().Text(new string('›', Math.Min(exportPage.Depth, 4)) + " sub-page").FontSize(8).FontColor(Muted);
                    column.Item().PaddingBottom(4).Text(exportPage.Title).FontSize(exportPage.Depth == 0 ? 22 : 18).Bold();
                    foreach (var element in SentinelExportFlattener.Flatten(exportPage.Blocks))
                        PdfElement(column.Item(), element, document.Images);
                }
            });
            page.Footer().Row(row =>
            {
                row.RelativeItem().Text($"{document.Title} · exported {document.GeneratedAt.ToLocalGwsDateTime()}").FontSize(8).FontColor(Muted);
                row.ConstantItem(80).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        })).GeneratePdf();

    private static void PdfElement(IContainer item, ExportElement element, IReadOnlyDictionary<string, SentinelExportImage> images)
    {
        var indent = element.Indent * 14;
        switch (element)
        {
            case ExportHeading heading:
                item.PaddingTop(heading.Level == 1 ? 8 : 4).Text(text => PdfSpans(text, heading.Text, heading.Level switch { 1 => 17f, 2 => 14f, _ => 12f }, bold: true));
                break;
            case ExportListItem listItem:
                item.PaddingLeft(indent).Row(row =>
                {
                    row.ConstantItem(18).Text(PdfMarker(listItem.Marker));
                    row.RelativeItem().Text(text => PdfSpans(text, listItem.Text));
                });
                break;
            case ExportParagraph { Style: "code" } code:
                // Keeps the label with the start of the block rather than alone at a page's foot.
                item.EnsureSpace(70).PaddingLeft(indent).Column(block =>
                {
                    if (code.Label is { } label) block.Item().Text(label).FontSize(8).FontColor(Muted);
                    block.Item().Background(CodeBackground).Padding(8).Text(SentinelExportFlattener.Text(code.Text)).FontFamily(MonoFont).FontSize(9);
                });
                break;
            case ExportParagraph { Style: "quote" } quote:
                item.PaddingLeft(indent).BorderLeft(2).BorderColor(Rule).PaddingLeft(10)
                    .Text(text => PdfSpans(text, quote.Text, italic: true));
                break;
            case ExportParagraph { Style: "callout" } callout:
                item.PaddingLeft(indent).Background(CalloutBackground).Padding(10).Text(text => PdfSpans(text, callout.Text));
                break;
            case ExportParagraph paragraph:
                item.PaddingLeft(indent).Text(text => PdfSpans(text, paragraph.Text));
                break;
            case ExportDivider:
                item.PaddingVertical(4).LineHorizontal(1).LineColor(Rule);
                break;
            case ExportImageRef image when images.TryGetValue(image.Url, out var picture):
                // Never enlarged past its own size (a pixel is 0.75 pt), nor taller than ~half a page.
                item.EnsureSpace(120).Column(block =>
                {
                    block.Item().AlignCenter().MaxWidth(Math.Max(24, picture.Width * 0.75f)).MaxHeight(420).Image(picture.Bytes).FitArea();
                    if (image.Caption.Length > 0) block.Item().AlignCenter().Text(image.Caption).FontSize(8.5f).FontColor(Muted);
                });
                break;
            case ExportImageRef image:
                PdfNote(item, $"Image: {(image.Caption.Length > 0 ? image.Caption : image.Url)}", SentinelExportFlattener.ExternalLink(image.Url));
                break;
            case ExportTable table:
                var columns = Math.Max(1, table.Rows.Max(r => r.Count));
                item.Table(grid =>
                {
                    grid.ColumnsDefinition(c => { for (var i = 0; i < columns; i++) c.RelativeColumn(); });
                    for (var r = 0; r < table.Rows.Count; r++)
                    {
                        var isHeader = table.HasHeader && r == 0;
                        for (var c = 0; c < columns; c++)
                        {
                            var cell = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                            grid.Cell().Border(0.5f).BorderColor(Rule).Background(isHeader ? CodeBackground : QColors.White).Padding(4)
                                .Text(text => PdfSpans(text, cell, 9.5f, bold: isHeader));
                        }
                    }
                });
                break;
            case ExportNote note:
                PdfNote(item, note.Text, note.Url);
                break;
        }
    }

    private static void PdfNote(IContainer item, string label, string? url) =>
        item.Text(text =>
        {
            if (url is not null) text.Hyperlink(label, url).Italic().FontColor("#2563EB");
            else text.Span(label).Italic().FontColor(Muted);
        });

    // The body font has no ballot-box glyphs, so to-dos print as [ ] / [x] in the PDF.
    private static string PdfMarker(string marker) => marker switch { "☐" => "[ ]", "☑" => "[x]", "▸" => "›", _ => marker };

    private static void PdfSpans(TextDescriptor text, IReadOnlyList<WikiRichTextSpan> spans, float size = 10.5f, bool bold = false, bool italic = false)
    {
        text.DefaultTextStyle(x => x.FontSize(size));
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            var link = SentinelExportFlattener.ExternalLink(span.Link);
            var descriptor = link is null ? text.Span(span.Text) : text.Hyperlink(span.Text, link).FontColor("#2563EB").Underline();
            if (bold || span.Bold) descriptor.Bold();
            if (italic || span.Italic) descriptor.Italic();
            if (span.Underline) descriptor.Underline();
            if (span.Strikethrough) descriptor.Strikethrough();
            if (span.Code) descriptor.FontFamily(MonoFont).BackgroundColor(CodeBackground);
        }
    }

    // ---- Word (.docx) ----

    private const long EmuPerPixel = 9525;
    private const long MaxImageWidthEmu = 6L * 914400;

    public static byte[] RenderDocx(SentinelExportDocument document)
    {
        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            word.PackageProperties.Title = document.Title;
            word.PackageProperties.Creator = document.GeneratedBy;
            word.PackageProperties.Created = document.GeneratedAt.UtcDateTime;
            var main = word.AddMainDocumentPart();
            AddStyles(main);
            var body = new Body();
            var imageIds = new Dictionary<string, string>(StringComparer.Ordinal);
            uint drawingId = 1;
            for (var index = 0; index < document.Pages.Count; index++)
            {
                var exportPage = document.Pages[index];
                if (index > 0) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(StyledParagraph(exportPage.Depth == 0 ? "Title" : "Heading1", [new Run(new Text(exportPage.Title) { Space = SpaceProcessingModeValues.Preserve })]));
                foreach (var element in SentinelExportFlattener.Flatten(exportPage.Blocks))
                    body.Append(DocxElement(main, element, document.Images, imageIds, ref drawingId));
            }
            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708, Gutter = 0 }));
            main.Document = new Document(body);
            main.Document.Save();
        }
        return stream.ToArray();
    }

    private static IEnumerable<OpenXmlElement> DocxElement(MainDocumentPart main, ExportElement element,
        IReadOnlyDictionary<string, SentinelExportImage> images, Dictionary<string, string> imageIds, ref uint drawingId)
    {
        var indentTwips = (element.Indent * 360).ToString(System.Globalization.CultureInfo.InvariantCulture);
        switch (element)
        {
            case ExportHeading heading:
                return [StyledParagraph($"Heading{Math.Clamp(heading.Level, 1, 3)}", DocxRuns(main, heading.Text))];
            case ExportListItem listItem:
            {
                var paragraph = new Paragraph(new ParagraphProperties(
                    new Indentation { Left = ((element.Indent + 1) * 360).ToString(System.Globalization.CultureInfo.InvariantCulture), Hanging = "360" }));
                paragraph.Append(new Run(new Text(listItem.Marker), new TabChar()));
                paragraph.Append(DocxRuns(main, listItem.Text));
                return [paragraph];
            }
            case ExportParagraph { Style: "code" } code:
            {
                var result = new List<OpenXmlElement>();
                if (code.Label is { } label)
                    result.Add(new Paragraph(new Run(new RunProperties(new Color { Val = "6B7280" }, new FontSize { Val = "16" }), new Text(label))));
                var paragraph = new Paragraph(new ParagraphProperties(
                    new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F4F6" },
                    new Indentation { Left = indentTwips }));
                paragraph.Append(TextRuns(SentinelExportFlattener.Text(code.Text), () => new RunProperties(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" }, new FontSize { Val = "18" })));
                result.Add(paragraph);
                return result;
            }
            case ExportParagraph { Style: "quote" } quote:
                return [StyledParagraph("Quote", DocxRuns(main, quote.Text))];
            case ExportParagraph { Style: "callout" } callout:
            {
                var paragraph = new Paragraph(new ParagraphProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F1F5F9" }, new Indentation { Left = indentTwips }));
                if (callout.Label is { Length: > 0 } icon) paragraph.Append(new Run(new Text(icon + " ") { Space = SpaceProcessingModeValues.Preserve }));
                paragraph.Append(DocxRuns(main, callout.Text));
                return [paragraph];
            }
            case ExportParagraph text:
            {
                var paragraph = new Paragraph(new ParagraphProperties(new Indentation { Left = indentTwips }));
                paragraph.Append(DocxRuns(main, text.Text));
                return [paragraph];
            }
            case ExportDivider:
                return [new Paragraph(new ParagraphProperties(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "D1D5DB" })))];
            case ExportImageRef image when images.TryGetValue(image.Url, out var picture):
            {
                if (!imageIds.TryGetValue(image.Url, out var relationshipId))
                {
                    var part = main.AddImagePart(picture.Format == "png" ? ImagePartType.Png : ImagePartType.Jpeg);
                    using (var data = new MemoryStream(picture.Bytes)) part.FeedData(data);
                    relationshipId = main.GetIdOfPart(part);
                    imageIds[image.Url] = relationshipId;
                }
                var result = new List<OpenXmlElement> { new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                    new Run(Drawing(relationshipId, picture, drawingId++, image.Caption))) };
                if (image.Caption.Length > 0)
                    result.Add(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                        new Run(new RunProperties(new Color { Val = "6B7280" }, new FontSize { Val = "16" }), new Text(image.Caption))));
                return result;
            }
            case ExportImageRef image:
                return [NoteParagraph(main, $"Image: {(image.Caption.Length > 0 ? image.Caption : image.Url)}", SentinelExportFlattener.ExternalLink(image.Url))];
            case ExportTable table:
                return [DocxTable(main, table), new Paragraph()];
            case ExportNote note:
                return [NoteParagraph(main, note.Text, note.Url)];
            default:
                return [];
        }
    }

    private static Table DocxTable(MainDocumentPart main, ExportTable table)
    {
        var columns = Math.Max(1, table.Rows.Max(r => r.Count));
        var grid = new Table(new TableProperties(
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            // Schema order matters here (Word reports the file as damaged otherwise): top, left,
            // bottom, right, then the inside borders.
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" })));
        var gridColumns = new TableGrid();
        for (var c = 0; c < columns; c++) gridColumns.Append(new GridColumn { Width = (9638 / columns).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        grid.Append(gridColumns);
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var isHeader = table.HasHeader && r == 0;
            var row = new TableRow();
            if (isHeader) row.Append(new TableRowProperties(new TableHeader()));
            for (var c = 0; c < columns; c++)
            {
                var spans = c < table.Rows[r].Count ? table.Rows[r][c] : [];
                var cell = new TableCell();
                if (isHeader) cell.Append(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F4F6" }));
                var paragraph = new Paragraph();
                paragraph.Append(DocxRuns(main, isHeader ? spans.Select(s => s with { Bold = true }).ToList() : spans));
                cell.Append(paragraph);
                row.Append(cell);
            }
            grid.Append(row);
        }
        return grid;
    }

    private static Paragraph NoteParagraph(MainDocumentPart main, string label, string? url)
    {
        var paragraph = new Paragraph();
        paragraph.Append(DocxRuns(main, [new WikiRichTextSpan(label, Italic: true, Link: url)]));
        return paragraph;
    }

    private static Paragraph StyledParagraph(string style, IEnumerable<OpenXmlElement> content)
    {
        var paragraph = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        paragraph.Append(content);
        return paragraph;
    }

    private static List<OpenXmlElement> DocxRuns(MainDocumentPart main, IReadOnlyList<WikiRichTextSpan> spans)
    {
        var result = new List<OpenXmlElement>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0) continue;
            var link = SentinelExportFlattener.ExternalLink(span.Link);
            RunProperties Properties()
            {
                var properties = new RunProperties();
                if (span.Bold) properties.Append(new Bold());
                if (span.Italic) properties.Append(new Italic());
                if (span.Strikethrough) properties.Append(new Strike());
                if (span.Code)
                {
                    properties.Append(new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
                    properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F3F4F6" });
                }
                if (link is not null) properties.Append(new Color { Val = "2563EB" });
                if (span.Underline || link is not null) properties.Append(new Underline { Val = UnderlineValues.Single });
                return properties;
            }
            var runs = TextRuns(span.Text, Properties);
            if (link is null)
            {
                result.AddRange(runs);
                continue;
            }
            var relationship = main.AddHyperlinkRelationship(new Uri(link, UriKind.Absolute), true);
            var hyperlink = new Hyperlink { Id = relationship.Id };
            hyperlink.Append(runs);
            result.Add(hyperlink);
        }
        return result;
    }

    // One run per line, with breaks between, so newlines in the text survive.
    private static List<Run> TextRuns(string text, Func<RunProperties> properties)
    {
        var runs = new List<Run>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var run = new Run(properties());
            if (i > 0) run.Append(new Break());
            run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            runs.Add(run);
        }
        return runs;
    }

    private static Drawing Drawing(string relationshipId, SentinelExportImage picture, uint id, string description)
    {
        var width = picture.Width * EmuPerPixel;
        var height = picture.Height * EmuPerPixel;
        if (width > MaxImageWidthEmu)
        {
            height = (long)(height * (MaxImageWidthEmu / (double)width));
            width = MaxImageWidthEmu;
        }
        var name = $"Picture {id}";
        return new Drawing(new DW.Inline(
            new DW.Extent { Cx = width, Cy = height },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = name, Description = description },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = id, Name = name }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = width, Cy = height }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
    }

    // Title, Heading 1-3 and Quote, so Word's navigation pane and styles work on the export.
    private static void AddStyles(MainDocumentPart main)
    {
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = "22" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            HeadingStyle("Title", "Title", 52, null),
            HeadingStyle("Heading1", "heading 1", 34, 0),
            HeadingStyle("Heading2", "heading 2", 28, 1),
            HeadingStyle("Heading3", "heading 3", 24, 2),
            new Style(
                new StyleName { Val = "Quote" },
                new BasedOn { Val = "Normal" },
                new StyleParagraphProperties(
                    new ParagraphBorders(new LeftBorder { Val = BorderValues.Single, Size = 12, Space = 8, Color = "D1D5DB" }),
                    new Indentation { Left = "360" }),
                new StyleRunProperties(new Italic(), new Color { Val = "4B5563" }))
            { Type = StyleValues.Paragraph, StyleId = "Quote" });
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = styles;
        part.Styles.Save();
    }

    private static Style HeadingStyle(string id, string name, int halfPoints, int? outlineLevel)
    {
        var paragraphProperties = new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = "240", After = "120" });
        if (outlineLevel is { } level) paragraphProperties.Append(new OutlineLevel { Val = level });
        return new Style(
            new StyleName { Val = name },
            new BasedOn { Val = "Normal" },
            new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            paragraphProperties,
            new StyleRunProperties(new Bold(), new FontSize { Val = halfPoints.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        { Type = StyleValues.Paragraph, StyleId = id };
    }
}
