using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.Application.Wiki;

// Sentinel's Notion-parity "export" story (Phase 4.5) - the rough inverse of
// NotionMarkdownBlockParser, best-effort by nature since several block types (databases,
// buttons, columns, breadcrumbs) have no faithful Markdown equivalent. Toggle nesting is
// preserved via GitHub-flavored Markdown's <details>/<summary> HTML, which every other
// structural block type doesn't need since Markdown already nests list items by indentation.
public static class WikiMarkdownExporter
{
    public static string ExportPage(string title, IReadOnlyList<WikiBlock> blocks)
    {
        var markdown = new StringBuilder();
        markdown.Append("# ").Append(title).Append("\n\n");

        // Indent levels of every currently-open <details> that still needs a closing tag -
        // popped (and the tag closed) as soon as a later block's indent drops back to or below
        // the toggle's own indent, mirroring how the live editor/HTML renderer both decide which
        // blocks are a toggle's children (wiki-block-editor.js's refreshToggleVisibility,
        // WikiBlockHtmlRenderer's tree-building).
        var openToggleIndents = new Stack<int>();

        foreach (var block in blocks)
        {
            while (openToggleIndents.Count > 0 && block.IndentLevel <= openToggleIndents.Peek())
            {
                markdown.Append("\n</details>\n\n");
                openToggleIndents.Pop();
            }

            AppendBlock(markdown, block);

            if (block.Type == WikiBlockTypes.Toggle)
            {
                openToggleIndents.Push(block.IndentLevel);
            }
        }

        while (openToggleIndents.Count > 0)
        {
            markdown.Append("\n</details>\n\n");
            openToggleIndents.Pop();
        }

        return markdown.ToString();
    }

    private static void AppendBlock(StringBuilder markdown, WikiBlock block)
    {
        var indent = new string(' ', block.IndentLevel * 2);
        var text = ToMarkdownText(block.RichText);

        switch (block.Type)
        {
            case WikiBlockTypes.Heading1:
                markdown.Append("# ").Append(text).Append("\n\n");
                break;
            case WikiBlockTypes.Heading2:
                markdown.Append("## ").Append(text).Append("\n\n");
                break;
            case WikiBlockTypes.Heading3:
                markdown.Append("### ").Append(text).Append("\n\n");
                break;
            case WikiBlockTypes.BulletedListItem:
                markdown.Append(indent).Append("- ").Append(text).Append('\n');
                break;
            case WikiBlockTypes.NumberedListItem:
                markdown.Append(indent).Append(block.Props.GetValueOrDefault("number", "1")).Append(". ").Append(text).Append('\n');
                break;
            case WikiBlockTypes.ToDo:
                markdown.Append(indent).Append("- [").Append(block.Props.GetValueOrDefault("checked") == "true" ? 'x' : ' ').Append("] ").Append(text).Append('\n');
                break;
            case WikiBlockTypes.Toggle:
                markdown.Append("<details>\n<summary>").Append(text).Append("</summary>\n\n");
                break;
            case WikiBlockTypes.Quote:
                markdown.Append("> ").Append(text).Append("\n\n");
                break;
            case WikiBlockTypes.Callout:
                markdown.Append("> ").Append(block.Props.GetValueOrDefault("icon", "💡")).Append(' ').Append(text).Append("\n\n");
                break;
            case WikiBlockTypes.Code:
                markdown.Append("```").Append(block.Props.GetValueOrDefault("language", string.Empty)).Append('\n')
                    .Append(block.PlainText).Append("\n```\n\n");
                break;
            case WikiBlockTypes.Divider:
                markdown.Append("---\n\n");
                break;
            case WikiBlockTypes.Image:
                if (WikiBlockHtmlRenderer.GetSafeLink(block.Props.GetValueOrDefault("url")) is { } imageUrl)
                {
                    markdown.Append("![").Append(text.Length == 0 ? "image" : text).Append("](").Append(imageUrl).Append(")\n\n");
                }
                break;
            case WikiBlockTypes.Embed:
                if (WikiBlockHtmlRenderer.GetSafeLink(block.Props.GetValueOrDefault("url")) is { } embedUrl)
                {
                    markdown.Append('[').Append(text.Length == 0 ? embedUrl : text).Append("](").Append(embedUrl).Append(")\n\n");
                }
                break;
            case WikiBlockTypes.Equation:
                markdown.Append("$$\n").Append(block.PlainText).Append("\n$$\n\n");
                break;
            case WikiBlockTypes.Button:
                if (WikiBlockHtmlRenderer.GetSafeLink(block.Props.GetValueOrDefault("url")) is { } buttonUrl)
                {
                    markdown.Append('[').Append(text.Length == 0 ? "Button" : text).Append("](").Append(buttonUrl).Append(")\n\n");
                }
                break;
            case WikiBlockTypes.LinkedDatabase:
            case WikiBlockTypes.InlineDatabase:
                markdown.Append("*[Database: ").Append(block.Props.GetValueOrDefault("databaseTitle", "Untitled")).Append("]*\n\n");
                break;
            case WikiBlockTypes.Table:
                AppendTable(markdown, block);
                break;
            case WikiBlockTypes.Columns:
                AppendColumns(markdown, block);
                break;
            case WikiBlockTypes.Tab:
                AppendTabs(markdown, block);
                break;
            // SyncedBlock content is already hydrated to its shared source's live text by the
            // time this runs (WikiService.GetPageAsync), so it needs no special handling beyond
            // the plain-text default below. Breadcrumb/TableOfContents are app-navigation aids
            // with no standalone meaning once exported, so they're intentionally skipped.
            case WikiBlockTypes.Breadcrumb:
            case WikiBlockTypes.TableOfContents:
                break;
            case WikiBlockTypes.Markdown:
                markdown.Append(block.Props.GetValueOrDefault("content", string.Empty)).Append("\n\n");
                break;
            default:
                if (text.Length > 0)
                {
                    markdown.Append(indent).Append(text).Append("\n\n");
                }
                break;
        }
    }

    private static void AppendTable(StringBuilder markdown, WikiBlock block)
    {
        if (block.Props.TryGetValue("tableJson", out var tableJson))
        {
            try
            {
                var rows = JsonSerializer.Deserialize<List<List<List<WikiRichTextSpan>>>>(tableJson, WikiBlockJson.Options);
                if (rows is { Count: > 0 })
                {
                    var columnCount = rows.Max(row => row.Count);
                    markdown.Append('|').AppendJoin('|', rows[0].Select(cell => $" {ToMarkdownText(cell)} ")).Append("|\n");
                    markdown.Append('|').AppendJoin('|', Enumerable.Repeat(" --- ", columnCount)).Append("|\n");
                    foreach (var row in rows.Skip(1))
                    {
                        markdown.Append('|').AppendJoin('|', row.Select(cell => $" {ToMarkdownText(cell)} ")).Append("|\n");
                    }
                    markdown.Append('\n');
                    return;
                }
            }
            catch (JsonException)
            {
                // Fall through to the plain-text fallback below.
            }
        }

        if (block.PlainText.Length > 0)
        {
            markdown.Append(block.PlainText).Append("\n\n");
        }
    }

    private static void AppendColumns(StringBuilder markdown, WikiBlock block)
    {
        // Markdown has no native side-by-side layout, so columns are flattened into sequential
        // sections - a lossy but honest best effort, same tradeoff RenderColumns makes going the
        // other direction for plain-text search/AI grounding.
        IReadOnlyList<string> columns;
        if (block.Props.TryGetValue("columnRichTextJson", out var columnRichTextJson))
        {
            try
            {
                var richColumns = JsonSerializer.Deserialize<List<List<WikiRichTextSpan>>>(columnRichTextJson, WikiBlockJson.Options);
                columns = richColumns is { Count: > 0 } ? richColumns.Select(ToMarkdownText).ToList() : [];
            }
            catch (JsonException)
            {
                columns = [];
            }
        }
        else
        {
            columns = [];
        }

        if (columns.Count == 0)
        {
            columns = block.PlainText.Split("|||", StringSplitOptions.TrimEntries);
        }

        foreach (var column in columns.Where(column => column.Length > 0))
        {
            markdown.Append(column).Append("\n\n");
        }
    }

    private static void AppendTabs(StringBuilder markdown, WikiBlock block)
    {
        foreach (var tab in WikiBlockHtmlRenderer.ParseTabs(block))
        {
            markdown.Append("**").Append(tab.Title).Append("**\n\n").Append(ToMarkdownText(tab.RichText)).Append("\n\n");
        }
    }

    private static string ToMarkdownText(IReadOnlyList<WikiRichTextSpan> spans) =>
        string.Concat(spans.Select(span =>
        {
            var text = span.Text;
            if (span.Code) text = $"`{text}`";
            if (span.Bold) text = $"**{text}**";
            if (span.Italic) text = $"_{text}_";
            if (span.Strikethrough) text = $"~~{text}~~";
            // No CommonMark syntax for underline - raw inline HTML passthrough is the standard
            // Markdown convention for it (Notion's own Markdown export does the same).
            if (span.Underline) text = $"<u>{text}</u>";
            if (WikiBlockHtmlRenderer.GetSafeLink(span.Link) is { } link) text = $"[{text}]({link})";
            return text;
        }));
}
