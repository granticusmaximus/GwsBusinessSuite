using System.Net;
using System.Text;
using Markdig;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Renders a WikiBlock list to read-only HTML for consumers outside the interactive editor.
// The editor's own contenteditable DOM is owned and rendered client-side by
// wiki-block-editor.js, so authored pages do not need a second preview surface.
//
// Deliberately flat: each block renders independently with a margin-left proportional to
// IndentLevel rather than stitching runs of list-item/toggle blocks into real nested
// <ul>/<ol>/<details> trees. That would need a stateful container-stack algorithm keyed off
// indent-level transitions: real but non-trivial to get right, and this app's Wiki has no
// public-facing view yet (admin-only, preview-only) where the semantic-HTML difference
// matters. Indent-based visual nesting reads the same to an author previewing their page.
public static class WikiBlockHtmlRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string RenderRichText(IReadOnlyList<WikiRichTextSpan> spans)
    {
        var builder = new StringBuilder();
        foreach (var span in spans)
        {
            var text = WebUtility.HtmlEncode(span.Text).Replace("\n", "<br />");
            if (span.Code) text = $"<code>{text}</code>";
            if (span.Bold) text = $"<b>{text}</b>";
            if (span.Italic) text = $"<i>{text}</i>";
            if (span.Strikethrough) text = $"<s>{text}</s>";
            if (!string.IsNullOrWhiteSpace(span.Link))
            {
                var isMention = span.Link.StartsWith("usermention:", StringComparison.OrdinalIgnoreCase)
                    || span.Link.StartsWith("datemention:", StringComparison.OrdinalIgnoreCase);
                text = isMention
                    ? $"<a class=\"wiki-mention\" href=\"{WebUtility.HtmlEncode(span.Link)}\">{text}</a>"
                    : $"<a href=\"{WebUtility.HtmlEncode(span.Link)}\" target=\"_blank\" rel=\"noopener noreferrer\">{text}</a>";
            }
            builder.Append(text);
        }
        return builder.ToString();
    }

    public static string RenderBlock(WikiBlock block, IReadOnlyList<WikiPage>? pagesForWikiLinks = null)
    {
        var content = RenderRichText(block.RichText);
        var indentStyle = block.IndentLevel > 0 ? $" style=\"margin-left:{block.IndentLevel * 1.5}rem\"" : string.Empty;

        return block.Type switch
        {
            WikiBlockTypes.Paragraph => $"<p{indentStyle}>{content}</p>",
            WikiBlockTypes.Heading1 => $"<h1{indentStyle}>{content}</h1>",
            WikiBlockTypes.Heading2 => $"<h2{indentStyle}>{content}</h2>",
            WikiBlockTypes.Heading3 => $"<h3{indentStyle}>{content}</h3>",
            WikiBlockTypes.BulletedListItem => $"<div class=\"wiki-list-item\"{indentStyle}>&bull; {content}</div>",
            WikiBlockTypes.NumberedListItem => $"<div class=\"wiki-list-item\"{indentStyle}>{content}</div>",
            WikiBlockTypes.ToDo => $"<div class=\"wiki-todo\"{indentStyle}><input type=\"checkbox\" disabled {(block.Props.GetValueOrDefault("checked") == "true" ? "checked" : string.Empty)} /> <span>{content}</span></div>",
            WikiBlockTypes.Toggle => $"<details{indentStyle}><summary>{content}</summary></details>",
            WikiBlockTypes.Quote => $"<blockquote{indentStyle}>{content}</blockquote>",
            WikiBlockTypes.Callout => $"<div class=\"wiki-callout\"{indentStyle}>{block.Props.GetValueOrDefault("icon", "💡")} {content}</div>",
            WikiBlockTypes.Code => $"<pre{indentStyle}><code>{WebUtility.HtmlEncode(block.PlainText)}</code></pre>",
            WikiBlockTypes.Divider => "<hr />",
            WikiBlockTypes.Image => string.IsNullOrWhiteSpace(block.Props.GetValueOrDefault("url"))
                ? string.Empty
                : $"<img src=\"{WebUtility.HtmlEncode(block.Props["url"])}\" alt=\"{WebUtility.HtmlEncode(block.PlainText)}\" loading=\"lazy\" style=\"max-width:100%\" />",
            WikiBlockTypes.Embed => string.IsNullOrWhiteSpace(block.Props.GetValueOrDefault("url"))
                ? string.Empty
                : $"<a href=\"{WebUtility.HtmlEncode(block.Props["url"])}\" target=\"_blank\" rel=\"noopener noreferrer\">{WebUtility.HtmlEncode(block.Props["url"])}</a>",
            WikiBlockTypes.LinkedDatabase => RenderLinkedDatabase(block, indentStyle),
            // Legacy content from the pre-block-editor wiki still uses [[Page Title]] syntax,
            // so it's routed through the same resolver the old single-Markdown-string editor
            // used - new blocks link via RichTextSpan.Link instead and never hit this path.
            WikiBlockTypes.Markdown => Markdown.ToHtml(
                WikiMarkdownHelper.ResolveWikiLinks(block.Props.GetValueOrDefault("content", string.Empty), pagesForWikiLinks ?? []),
                MarkdownPipeline),
            _ => $"<p{indentStyle}>{content}</p>"
        };
    }

    public static string RenderPage(IReadOnlyList<WikiBlock> blocks, IReadOnlyList<WikiPage>? pagesForWikiLinks = null) =>
        string.Concat(blocks.Select(block => RenderBlock(block, pagesForWikiLinks)));

    // A short single-line preview of a block's content, used by the sidebar tree and by the
    // structural revision diff (WikiService.BuildStructuralDiff) - never HTML, just text.
    public static string PlainTextPreview(WikiBlock block, int maxLength = 80)
    {
        var text = block.Type switch
        {
            WikiBlockTypes.Markdown => block.Props.GetValueOrDefault("content", string.Empty),
            WikiBlockTypes.Divider => "---",
            WikiBlockTypes.Image => block.Props.GetValueOrDefault("url", "[image]"),
            WikiBlockTypes.Embed => block.Props.GetValueOrDefault("url", "[embed]"),
            WikiBlockTypes.LinkedDatabase => block.Props.GetValueOrDefault("databaseTitle", "[linked database]"),
            _ => block.PlainText
        };
        text = text.Replace('\n', ' ').Trim();
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }

    private static string RenderLinkedDatabase(WikiBlock block, string indentStyle)
    {
        var databaseId = block.Props.GetValueOrDefault("databaseId", string.Empty);
        var title = block.Props.GetValueOrDefault("databaseTitle", "Linked database");
        return $"<div class=\"wiki-linked-database\" data-database-id=\"{WebUtility.HtmlEncode(databaseId)}\"{indentStyle}>"
            + $"<span aria-hidden=\"true\">▦</span><span>{WebUtility.HtmlEncode(title)}</span></div>";
    }
}
