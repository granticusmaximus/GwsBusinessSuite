using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Export to PDF and Word (Sentinel item 20). The service gathers the page (and, if asked, the
// sub-pages the user can view) and the in-app images they use; SentinelExportFlattener turns the
// blocks into a short list of printable elements, which both the PDF and the .docx writer draw.

public enum SentinelExportFormat { Pdf, Docx }

public sealed record SentinelExportImage(byte[] Bytes, string Format, int Width, int Height);

public sealed record SentinelExportPage(Guid Id, string Title, string? Icon, int Depth, IReadOnlyList<WikiBlock> Blocks);

public sealed record SentinelExportDocument(
    string Title, DateTimeOffset GeneratedAt, string GeneratedBy,
    IReadOnlyList<SentinelExportPage> Pages, IReadOnlyDictionary<string, SentinelExportImage> Images);

public sealed record SentinelExportFile(byte[] Content, string ContentType, string FileName);

public interface ISentinelPageExportService
{
    // Null when the page doesn't exist, is in the trash, or the user can't view it.
    Task<SentinelExportFile?> ExportAsync(Guid pageId, string username, SentinelExportFormat format, bool includeSubPages, CancellationToken cancellationToken = default);
}

public abstract record ExportElement(int Indent);
public sealed record ExportHeading(int Level, IReadOnlyList<WikiRichTextSpan> Text) : ExportElement(0);
// Style: "normal", "quote", "callout" or "code".
public sealed record ExportParagraph(IReadOnlyList<WikiRichTextSpan> Text, string Style = "normal", string? Label = null, int Level = 0) : ExportElement(Level);
// Marker: "•", "1.", "☐" or "☑".
public sealed record ExportListItem(string Marker, IReadOnlyList<WikiRichTextSpan> Text, int Level) : ExportElement(Level);
public sealed record ExportDivider() : ExportElement(0);
public sealed record ExportImageRef(string Url, string Caption) : ExportElement(0);
public sealed record ExportTable(IReadOnlyList<IReadOnlyList<IReadOnlyList<WikiRichTextSpan>>> Rows, bool HasHeader) : ExportElement(0);
// Something that can't be printed as itself (a video, a database, an external image): a short line.
public sealed record ExportNote(string Text, string? Url = null) : ExportElement(0);

public static partial class SentinelExportFlattener
{
    public const int MaxNesting = 4;

    public static IReadOnlyList<ExportElement> Flatten(IReadOnlyList<WikiBlock> blocks) => Flatten(blocks, 0);

    private static List<ExportElement> Flatten(IReadOnlyList<WikiBlock> blocks, int nesting)
    {
        var elements = new List<ExportElement>();
        // Numbered lists count per indent level and restart after anything else.
        var numbers = new int[8];
        foreach (var block in blocks)
        {
            var level = Math.Clamp(block.IndentLevel, 0, 6);
            if (block.Type == WikiBlockTypes.NumberedListItem)
            {
                numbers[level]++;
                for (var deeper = level + 1; deeper < numbers.Length; deeper++) numbers[deeper] = 0;
            }
            else if (!WikiBlockTypes.IsListItem(block.Type) && block.Type != WikiBlockTypes.ToDo)
            {
                Array.Clear(numbers);
            }
            elements.AddRange(ToElements(block, level, numbers[level], nesting));
        }
        return elements;
    }

    private static IEnumerable<ExportElement> ToElements(WikiBlock block, int level, int number, int nesting)
    {
        var text = block.RichText;
        switch (block.Type)
        {
            case WikiBlockTypes.Heading1: yield return new ExportHeading(1, text); break;
            case WikiBlockTypes.Heading2: yield return new ExportHeading(2, text); break;
            case WikiBlockTypes.Heading3: yield return new ExportHeading(3, text); break;
            case WikiBlockTypes.BulletedListItem: yield return new ExportListItem("•", text, level); break;
            case WikiBlockTypes.NumberedListItem: yield return new ExportListItem($"{number}.", text, level); break;
            case WikiBlockTypes.ToDo:
                yield return new ExportListItem(block.Props.GetValueOrDefault("checked") == "true" ? "☑" : "☐", text, level);
                break;
            case WikiBlockTypes.Toggle: yield return new ExportListItem("▸", text, level); break;
            case WikiBlockTypes.Quote: yield return new ExportParagraph(text, "quote", Level: level); break;
            case WikiBlockTypes.Callout:
                yield return new ExportParagraph(text, "callout", block.Props.GetValueOrDefault("icon"), level);
                break;
            case WikiBlockTypes.Code:
                yield return new ExportParagraph(Plain(block.PlainText), "code", NullIfEmpty(block.Props.GetValueOrDefault("language")), level);
                break;
            case WikiBlockTypes.Equation: yield return new ExportParagraph(Plain(block.PlainText), "code", "Equation (LaTeX)", level); break;
            case WikiBlockTypes.Diagram: yield return new ExportParagraph(Plain(block.PlainText), "code", "Diagram (Mermaid)", level); break;
            case WikiBlockTypes.Canvas:
                // A whiteboard exports as its words, one bullet per item, under a note.
                yield return new ExportNote("Whiteboard - open the page in Sentinel to see the board.");
                foreach (var line in block.PlainText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    yield return new ExportListItem("•", Plain(line), level);
                break;
            case WikiBlockTypes.Divider: yield return new ExportDivider(); break;
            case WikiBlockTypes.Image:
                var imageUrl = block.Props.GetValueOrDefault("url") ?? string.Empty;
                if (imageUrl.Length > 0) yield return new ExportImageRef(imageUrl, block.PlainText.Trim());
                break;
            case WikiBlockTypes.Table:
                var (rows, header) = TableRows(block);
                if (rows.Count > 0) yield return new ExportTable(rows, header);
                break;
            case WikiBlockTypes.Video: yield return Note("Video", block); break;
            case WikiBlockTypes.Business: yield return new ExportNote($"{SentinelLiveBlockKinds.Describe(block)} - open the page in Sentinel for the current data"); break;
            case WikiBlockTypes.Audio: yield return Note("Audio", block); break;
            case WikiBlockTypes.File: yield return Note("File", block); break;
            case WikiBlockTypes.Pdf: yield return Note("PDF", block); break;
            case WikiBlockTypes.Embed: yield return Note("Link", block); break;
            case WikiBlockTypes.PageLink:
                yield return new ExportNote("→ " + (block.Props.GetValueOrDefault("pageTitle") ?? block.PlainText));
                break;
            case WikiBlockTypes.LinkedDatabase:
            case WikiBlockTypes.InlineDatabase:
                yield return new ExportNote("Database: " + (block.Props.GetValueOrDefault("databaseTitle") ?? "Untitled") + " (open Sentinel to see its rows)");
                break;
            case WikiBlockTypes.Button:
                yield return new ExportParagraph(text, Level: level);
                break;
            case WikiBlockTypes.Columns:
            case WikiBlockTypes.Tab:
                // Stored as "first ||| second": each column or tab becomes its own paragraph.
                foreach (var part in block.PlainText.Split("|||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    yield return new ExportParagraph(Plain(part), Level: level);
                break;
            case WikiBlockTypes.Markdown:
                // Older pages carried over from the Markdown wiki: parse into blocks and print those.
                if (nesting < MaxNesting)
                {
                    foreach (var element in Flatten(NotionMarkdownBlockParser.Parse(block.Props.GetValueOrDefault("content", block.PlainText)), nesting + 1))
                        yield return element;
                }
                break;
            case WikiBlockTypes.Breadcrumb:
            case WikiBlockTypes.TableOfContents:
            case WikiBlockTypes.SyncedBlock:
                break;
            default:
                if (block.PlainText.Trim().Length > 0) yield return new ExportParagraph(text, Level: level);
                break;
        }
    }

    // A rich text cell grid from a table block: tableJson when present, else the older
    // pipe-separated text.
    public static (IReadOnlyList<IReadOnlyList<IReadOnlyList<WikiRichTextSpan>>> Rows, bool HasHeader) TableRows(WikiBlock block)
    {
        var hasHeader = block.Props.GetValueOrDefault("hasColumnHeader", "true") == "true";
        if (block.Props.TryGetValue("tableJson", out var json))
        {
            try
            {
                var rich = JsonSerializer.Deserialize<List<List<List<WikiRichTextSpan>>>>(json, WikiBlockJson.Options);
                if (rich is { Count: > 0 }) return (rich.Select(r => (IReadOnlyList<IReadOnlyList<WikiRichTextSpan>>)r.Select(c => (IReadOnlyList<WikiRichTextSpan>)c).ToList()).ToList(), hasHeader);
            }
            catch (JsonException)
            {
                // Fall back to the text form.
            }
        }
        var rows = block.PlainText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries).Where(cell => cell.Length > 0).ToList())
            .Where(row => row.Count > 0 && !row.All(cell => DividerCell().IsMatch(cell)))
            .Select(row => (IReadOnlyList<IReadOnlyList<WikiRichTextSpan>>)row.Select(cell => Plain(cell)).ToList())
            .ToList();
        return (rows, true);
    }

    // A link that should be clickable in the file: web and mail links only. In-app links
    // (wikilink:, mentions, /admin/...) print as their text.
    public static string? ExternalLink(string? link) =>
        link is not null && (link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                             || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                             || link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            ? link
            : null;

    public static string Text(IReadOnlyList<WikiRichTextSpan> spans) => string.Concat(spans.Select(s => s.Text));

    private static ExportNote Note(string kind, WikiBlock block)
    {
        var url = ExternalLink(block.Props.GetValueOrDefault("url"));
        var caption = block.PlainText.Trim();
        return new ExportNote($"{kind}: {(caption.Length > 0 ? caption : url ?? "attachment in Sentinel")}", url);
    }

    private static IReadOnlyList<WikiRichTextSpan> Plain(string text) => [new WikiRichTextSpan(text)];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^:?-{3,}:?$")]
    private static partial Regex DividerCell();
}
