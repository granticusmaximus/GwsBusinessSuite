using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GwsBusinessSuite.Infrastructure.Services;

// Markdown as a PDF (06-11-26). A document the app builds in Markdown (World News' event document)
// is shown rendered in the app and downloads as a PDF made here from the same Markdown, so the two
// never drift apart. Covers what the app's documents use: headings, paragraphs, bullet and numbered
// lists, quotes, rules, code, links, bold and italic. Raw HTML is ignored, as on screen, and only
// web and mail links stay links. Text falls back from Liberation Sans to Noto for other scripts
// (World News carries Japanese, Korean, Chinese, Arabic, Persian, Urdu and Hindi headlines): the
// container installs both (Dockerfile), and a Mac uses its own fonts.
public static class MarkdownPdf
{
    public static readonly string[] Fonts =
    [
        "Liberation Sans", "Noto Sans", "Noto Sans CJK JP", "Noto Sans CJK KR", "Noto Sans CJK SC", "Noto Sans Arabic",
        "Noto Naskh Arabic", "Noto Sans Devanagari", "Noto Sans Hebrew", "Noto Sans Thai",
        // A Mac's own, for local runs.
        "Helvetica", "Hiragino Sans", "Apple SD Gothic Neo", "PingFang SC", "Geeza Pro", "Kohinoor Devanagari"
    ];

    private static readonly string[] MonoFonts = ["Liberation Mono", "Menlo", "Noto Sans Mono"];

    private const string Ink = "#111827";
    private const string Muted = "#6B7280";
    private const string Rule = "#E5E7EB";
    private const string LinkColor = "#1D4ED8";
    private const string QuoteBar = "#D1D5DB";
    private const string CodeBackground = "#F3F4F6";
    private const float BodySize = 10;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    static MarkdownPdf()
    {
        QuestPdfSetup.Apply();
        // A glyph no installed font has prints blank rather than failing the download.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    // footer: a short line printed at the foot of every page, beside the page number.
    public static byte[] Render(string markdown, string footer)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        return Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(44);
            page.DefaultTextStyle(x => x.FontFamily(Fonts).FontSize(BodySize).FontColor(Ink).LineHeight(1.35f));
            page.Content().Column(column =>
            {
                column.Spacing(6);
                foreach (var block in document) RenderBlock(column, block);
            });
            page.Footer().PaddingTop(8).Row(row =>
            {
                row.RelativeItem().Text(footer).FontSize(8).FontColor(Muted);
                row.ConstantItem(70).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        })).GeneratePdf();
    }

    private static void RenderBlock(ColumnDescriptor column, Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var size = heading.Level switch { 1 => 18f, 2 => 13.5f, 3 => 11f, _ => BodySize };
                column.Item().PaddingTop(heading.Level switch { 1 => 0, 2 => 10, _ => 4 })
                    .Text(text => RenderInlines(text, heading.Inline, new Style(size, Bold: true)));
                if (heading.Level == 2) column.Item().LineHorizontal(0.75f).LineColor(Rule);
                break;
            case ParagraphBlock paragraph:
                column.Item().Text(text => RenderInlines(text, paragraph.Inline, new Style(BodySize)));
                break;
            case QuoteBlock quote:
                column.Item().BorderLeft(2).BorderColor(QuoteBar).PaddingLeft(8).DefaultTextStyle(x => x.FontColor(Muted)).Column(inner =>
                {
                    inner.Spacing(4);
                    foreach (var child in quote) RenderBlock(inner, child);
                });
                break;
            case ListBlock list:
                var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                column.Item().Column(items =>
                {
                    items.Spacing(3);
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var marker = list.IsOrdered ? $"{number++}." : "•";
                        items.Item().Row(row =>
                        {
                            row.ConstantItem(list.IsOrdered ? 18 : 12).Text(marker);
                            row.RelativeItem().Column(inner =>
                            {
                                inner.Spacing(3);
                                foreach (var child in item) RenderBlock(inner, child);
                            });
                        });
                    }
                });
                break;
            case ThematicBreakBlock:
                column.Item().PaddingVertical(4).LineHorizontal(0.75f).LineColor(Rule);
                break;
            case CodeBlock code:
                var lines = code.Lines.Lines.Take(code.Lines.Count).Select(line => line.ToString());
                column.Item().Background(CodeBackground).Padding(6).Text(string.Join('\n', lines)).FontFamily(MonoFonts).FontSize(9);
                break;
            case ContainerBlock container:
                foreach (var child in container) RenderBlock(column, child);
                break;
            case LeafBlock { Inline: not null } leaf:
                column.Item().Text(text => RenderInlines(text, leaf.Inline, new Style(BodySize)));
                break;
        }
    }

    private sealed record Style(float Size, bool Bold = false, bool Italic = false, bool Mono = false, string? Url = null);

    private static void RenderInlines(TextDescriptor text, ContainerInline? inlines, Style style)
    {
        if (inlines is null) return;
        foreach (var inline in inlines) RenderInline(text, inline, style);
    }

    private static void RenderInline(TextDescriptor text, Inline inline, Style style)
    {
        switch (inline)
        {
            case LiteralInline literal:
                Span(text, literal.Content.ToString(), style);
                break;
            case CodeInline code:
                Span(text, code.Content, style with { Mono = true });
                break;
            case LineBreakInline lineBreak:
                Span(text, lineBreak.IsHard ? "\n" : " ", style);
                break;
            case HtmlEntityInline entity:
                Span(text, entity.Transcoded.ToString(), style);
                break;
            case AutolinkInline autolink:
                Span(text, autolink.Url, style with { Url = SafeUrl(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url) });
                break;
            case LinkInline link:
                RenderInlines(text, link, style with { Url = SafeUrl(link.Url) });
                break;
            case EmphasisInline emphasis:
                var inner = emphasis.DelimiterChar is '*' or '_'
                    ? emphasis.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true }
                    : style;
                RenderInlines(text, emphasis, inner);
                break;
            case ContainerInline container:
                RenderInlines(text, container, style);
                break;
        }
    }

    private static void Span(TextDescriptor text, string value, Style style)
    {
        if (value.Length == 0) return;
        var span = style.Url is { } url ? text.Hyperlink(value, url) : text.Span(value);
        span.FontSize(style.Size);
        if (style.Bold) span.Bold();
        if (style.Italic) span.Italic();
        if (style.Mono) span.FontFamily(MonoFonts);
        if (style.Url is not null) span.FontColor(LinkColor).Underline();
    }

    private static string? SafeUrl(string? url) =>
        url is not null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            ? url
            : null;
}
