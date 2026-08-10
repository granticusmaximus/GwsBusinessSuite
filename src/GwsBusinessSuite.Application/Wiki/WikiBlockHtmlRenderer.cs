using System.Net;
using System.Text;
using System.Text.Json;
using Markdig;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Renders a WikiBlock list to read-only HTML for consumers outside the interactive editor.
// The editor's own contenteditable DOM is owned and rendered client-side by
// wiki-block-editor.js, so authored pages do not need a second preview surface.
//
// RenderBlock remains useful for independent previews, while RenderPage interprets the flat,
// ordered IndentLevel representation as a hierarchy. Public shares therefore receive semantic
// list markup and real toggle ownership instead of a collection of visually indented siblings.
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
            if (WikiRichTextColors.TryNormalize(span.TextColor, out var textColor))
            {
                text = $"<span class=\"wiki-rich-text-color-{textColor}\">{text}</span>";
            }
            if (WikiRichTextColors.TryNormalize(span.BackgroundColor, out var backgroundColor))
            {
                text = $"<span class=\"wiki-rich-text-bg-{backgroundColor}\">{text}</span>";
            }
            if (!string.IsNullOrWhiteSpace(span.Link))
            {
                var safeLink = GetSafeLink(span.Link);
                if (safeLink is not null)
                {
                    var isMention = safeLink.StartsWith("usermention:", StringComparison.OrdinalIgnoreCase)
                        || safeLink.StartsWith("datemention:", StringComparison.OrdinalIgnoreCase)
                        || safeLink.StartsWith("rowmention:", StringComparison.OrdinalIgnoreCase);
                    var isInternal = isMention || safeLink.StartsWith("wikilink:", StringComparison.OrdinalIgnoreCase);
                    text = isMention
                        ? $"<a class=\"wiki-mention\" href=\"{WebUtility.HtmlEncode(safeLink)}\">{text}</a>"
                        : isInternal
                            ? $"<a href=\"{WebUtility.HtmlEncode(safeLink)}\">{text}</a>"
                            : $"<a href=\"{WebUtility.HtmlEncode(safeLink)}\" target=\"_blank\" rel=\"noopener noreferrer\">{text}</a>";
                }
            }
            builder.Append(text);
        }
        return builder.ToString();
    }

    // Internal rather than private so WikiMarkdownExporter (same assembly) can apply the same
    // executable-scheme filtering to links it writes out.
    internal static string? GetSafeLink(string? value)
    {
        var link = value?.Trim();
        if (string.IsNullOrEmpty(link))
        {
            return null;
        }

        if (link.StartsWith("wikilink:", StringComparison.OrdinalIgnoreCase)
            || link.StartsWith("usermention:", StringComparison.OrdinalIgnoreCase)
            || link.StartsWith("datemention:", StringComparison.OrdinalIgnoreCase)
            || link.StartsWith("rowmention:", StringComparison.OrdinalIgnoreCase))
        {
            return link;
        }

        if (Uri.TryCreate(link, UriKind.Absolute, out var absolute))
        {
            return absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals("tel", StringComparison.OrdinalIgnoreCase)
                    ? link
                    : null;
        }

        // A colon in a value that failed absolute-URI parsing is treated as a malformed
        // scheme, not as a relative application link.
        return link.Contains(':', StringComparison.Ordinal) ? null : link;
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
            WikiBlockTypes.Callout => $"<div class=\"wiki-callout\"{indentStyle}>{WebUtility.HtmlEncode(block.Props.GetValueOrDefault("icon", "💡"))} {content}</div>",
            WikiBlockTypes.Code => RenderCode(block, indentStyle),
            WikiBlockTypes.Divider => "<hr />",
            WikiBlockTypes.Image => GetSafeLink(block.Props.GetValueOrDefault("url")) is not { } imageUrl
                ? string.Empty
                : $"<img src=\"{WebUtility.HtmlEncode(imageUrl)}\" alt=\"{WebUtility.HtmlEncode(block.PlainText)}\" loading=\"lazy\" style=\"max-width:100%\" />",
            WikiBlockTypes.Embed => RenderEmbed(block),
            WikiBlockTypes.LinkedDatabase => RenderLinkedDatabase(block, indentStyle),
            WikiBlockTypes.InlineDatabase => RenderLinkedDatabase(block, indentStyle, isInline: true),
            WikiBlockTypes.Table => RenderTable(block, indentStyle),
            WikiBlockTypes.Equation => RenderEquation(block, indentStyle),
            WikiBlockTypes.Breadcrumb => $"<nav class=\"wiki-breadcrumb\"{indentStyle} aria-label=\"Breadcrumb\">{content}</nav>",
            WikiBlockTypes.TableOfContents => $"<nav class=\"wiki-table-of-contents\"{indentStyle}>Table of contents</nav>",
            WikiBlockTypes.Button => $"<a class=\"wiki-button\" href=\"{WebUtility.HtmlEncode(GetSafeLink(block.Props.GetValueOrDefault("url")) ?? "#")}\">{content}</a>",
            WikiBlockTypes.SyncedBlock => $"<div class=\"wiki-synced-block\"{indentStyle}>{content}</div>",
            WikiBlockTypes.Columns => RenderColumns(block, indentStyle),
            WikiBlockTypes.Tab => RenderTabs(block, indentStyle),
            // Legacy content from the pre-block-editor wiki still uses [[Page Title]] syntax,
            // so it's routed through the same resolver the old single-Markdown-string editor
            // used - new blocks link via RichTextSpan.Link instead and never hit this path.
            WikiBlockTypes.Markdown => Markdown.ToHtml(
                WikiMarkdownHelper.ResolveWikiLinks(block.Props.GetValueOrDefault("content", string.Empty), pagesForWikiLinks ?? []),
                MarkdownPipeline),
            _ => $"<p{indentStyle}>{content}</p>"
        };
    }

    public static string RenderPage(IReadOnlyList<WikiBlock> blocks, IReadOnlyList<WikiPage>? pagesForWikiLinks = null)
    {
        var headings = blocks
            .Where(block => block.Type is WikiBlockTypes.Heading1 or WikiBlockTypes.Heading2 or WikiBlockTypes.Heading3)
            .Select((block, index) => (Block: block, Anchor: $"sentinel-heading-{index + 1}"))
            .ToList();
        var headingAnchors = headings.ToDictionary(item => item.Block.Id, item => item.Anchor);

        var builder = new StringBuilder();
        RenderNodes(
            builder,
            BuildRenderTree(blocks),
            headings,
            headingAnchors,
            pagesForWikiLinks,
            indentOffset: 0);
        return builder.ToString();
    }

    private static IReadOnlyList<RenderNode> BuildRenderTree(IReadOnlyList<WikiBlock> blocks)
    {
        var roots = new List<RenderNode>();
        var ancestorStack = new Stack<RenderNode>();

        foreach (var block in blocks)
        {
            var indent = GetIndent(block);
            while (ancestorStack.Count > 0 && GetIndent(ancestorStack.Peek().Block) >= indent)
            {
                ancestorStack.Pop();
            }

            var node = new RenderNode(block);
            if (ancestorStack.TryPeek(out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }

            ancestorStack.Push(node);
        }

        return roots;
    }

    private static void RenderNodes(
        StringBuilder builder,
        IReadOnlyList<RenderNode> nodes,
        IReadOnlyList<(WikiBlock Block, string Anchor)> headings,
        IReadOnlyDictionary<Guid, string> headingAnchors,
        IReadOnlyList<WikiPage>? pagesForWikiLinks,
        int indentOffset)
    {
        for (var index = 0; index < nodes.Count;)
        {
            var node = nodes[index];
            if (WikiBlockTypes.IsListItem(node.Block.Type))
            {
                index = RenderList(
                    builder,
                    nodes,
                    index,
                    headings,
                    headingAnchors,
                    pagesForWikiLinks,
                    indentOffset);
                continue;
            }

            if (node.Block.Type == WikiBlockTypes.Toggle)
            {
                RenderToggle(
                    builder,
                    node,
                    headings,
                    headingAnchors,
                    pagesForWikiLinks,
                    indentOffset);
            }
            else
            {
                builder.Append(RenderPageBlock(node.Block, headings, headingAnchors, pagesForWikiLinks, indentOffset));
                if (node.Children.Count > 0)
                {
                    // Only list items and toggles are structural containers. Descendants of any
                    // other block remain visual siblings and retain their authored indentation.
                    RenderNodes(
                        builder,
                        node.Children,
                        headings,
                        headingAnchors,
                        pagesForWikiLinks,
                        indentOffset);
                }
            }

            index++;
        }
    }

    private static int RenderList(
        StringBuilder builder,
        IReadOnlyList<RenderNode> nodes,
        int startIndex,
        IReadOnlyList<(WikiBlock Block, string Anchor)> headings,
        IReadOnlyDictionary<Guid, string> headingAnchors,
        IReadOnlyList<WikiPage>? pagesForWikiLinks,
        int indentOffset)
    {
        var listType = nodes[startIndex].Block.Type;
        var tagName = listType == WikiBlockTypes.NumberedListItem ? "ol" : "ul";
        var cssClass = listType == WikiBlockTypes.NumberedListItem
            ? "wiki-list wiki-numbered-list"
            : "wiki-list wiki-bulleted-list";
        var effectiveIndent = Math.Max(0, GetIndent(nodes[startIndex].Block) - indentOffset);
        var indentStyle = effectiveIndent > 0 ? $" style=\"margin-left:{effectiveIndent * 1.5}rem\"" : string.Empty;

        builder.Append('<').Append(tagName).Append(" class=\"").Append(cssClass).Append('"').Append(indentStyle).Append('>');
        var index = startIndex;
        while (index < nodes.Count && nodes[index].Block.Type == listType)
        {
            var node = nodes[index];
            builder.Append("<li class=\"wiki-list-item\">").Append(RenderRichText(node.Block.RichText));
            if (node.Children.Count > 0)
            {
                RenderNodes(
                    builder,
                    node.Children,
                    headings,
                    headingAnchors,
                    pagesForWikiLinks,
                    GetIndent(node.Block) + 1);
            }
            builder.Append("</li>");
            index++;
        }
        builder.Append("</").Append(tagName).Append('>');
        return index;
    }

    private static void RenderToggle(
        StringBuilder builder,
        RenderNode node,
        IReadOnlyList<(WikiBlock Block, string Anchor)> headings,
        IReadOnlyDictionary<Guid, string> headingAnchors,
        IReadOnlyList<WikiPage>? pagesForWikiLinks,
        int indentOffset)
    {
        var effectiveIndent = Math.Max(0, GetIndent(node.Block) - indentOffset);
        var indentStyle = effectiveIndent > 0 ? $" style=\"margin-left:{effectiveIndent * 1.5}rem\"" : string.Empty;

        builder.Append("<details class=\"wiki-toggle\"").Append(indentStyle).Append("><summary>")
            .Append(RenderRichText(node.Block.RichText))
            .Append("</summary>");
        if (node.Children.Count > 0)
        {
            builder.Append("<div class=\"wiki-toggle-content\" style=\"margin-left:1.5rem\">");
            RenderNodes(
                builder,
                node.Children,
                headings,
                headingAnchors,
                pagesForWikiLinks,
                GetIndent(node.Block) + 1);
            builder.Append("</div>");
        }
        builder.Append("</details>");
    }

    private static string RenderPageBlock(
        WikiBlock block,
        IReadOnlyList<(WikiBlock Block, string Anchor)> headings,
        IReadOnlyDictionary<Guid, string> headingAnchors,
        IReadOnlyList<WikiPage>? pagesForWikiLinks,
        int indentOffset)
    {
        if (block.Type == WikiBlockTypes.TableOfContents)
        {
            var links = string.Concat(headings.Select(item =>
                $"<a class=\"level-{item.Block.Type[^1]}\" href=\"#{item.Anchor}\">{RenderRichText(item.Block.RichText)}</a>"));
            return $"<nav class=\"wiki-table-of-contents\">{links}</nav>";
        }

        var effectiveIndent = Math.Max(0, GetIndent(block) - indentOffset);
        var rendered = RenderBlock(block with { IndentLevel = effectiveIndent }, pagesForWikiLinks);
        if (!headingAnchors.TryGetValue(block.Id, out var anchor))
        {
            return rendered;
        }

        var openingTagEnd = rendered.IndexOf('>');
        return openingTagEnd < 0 ? rendered : rendered.Insert(openingTagEnd, $" id=\"{anchor}\"");
    }

    private static int GetIndent(WikiBlock block) => Math.Max(0, block.IndentLevel);

    private sealed class RenderNode(WikiBlock block)
    {
        public WikiBlock Block { get; } = block;
        public List<RenderNode> Children { get; } = [];
    }

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
            WikiBlockTypes.InlineDatabase => block.Props.GetValueOrDefault("databaseTitle", "[inline database]"),
            WikiBlockTypes.Tab => PlainTextTabs(block),
            _ => block.PlainText
        };
        text = text.Replace('\n', ' ').Trim();
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }

    // The "wiki-code-hydrate"/"wiki-katex-target" markers are what wikiRichContentHydrate.js
    // looks for post-render to call highlight.js/KaTeX in read-only views (SentinelPublicShare)
    // - the raw HTML-encoded text is still the fallback if that script or its CDN libraries
    // never load, matching the live block editor's own approach (see attachRichPreview in
    // wiki-block-editor.js) rather than requiring a server-side LaTeX/highlighter dependency.
    private static string RenderCode(WikiBlock block, string indentStyle)
    {
        var language = block.Props.GetValueOrDefault("language", string.Empty);
        var languageClass = string.IsNullOrWhiteSpace(language) ? string.Empty : $" language-{WebUtility.HtmlEncode(language)}";
        return $"<pre class=\"wiki-code\" data-language=\"{WebUtility.HtmlEncode(language)}\"{indentStyle}><code class=\"wiki-code-hydrate{languageClass}\">{WebUtility.HtmlEncode(block.PlainText)}</code></pre>";
    }

    private static string RenderEquation(WikiBlock block, string indentStyle) =>
        $"<div class=\"wiki-equation wiki-katex-target\" data-latex=\"{WebUtility.HtmlEncode(block.PlainText)}\"{indentStyle}>{WebUtility.HtmlEncode(block.PlainText)}</div>";

    private static string RenderEmbed(WikiBlock block)
    {
        var url = GetSafeLink(block.Props.GetValueOrDefault("url"));
        if (url is null)
        {
            return string.Empty;
        }

        // Set only by NotionMapping.MapBlock for imported video/audio/file/pdf blocks -
        // Sentinel has no dedicated block type for these, but an inline player reads far
        // better than a bare link for the two kinds a <video>/<audio> tag can actually play.
        var mediaKind = block.Props.GetValueOrDefault("mediaKind");
        if (mediaKind is "video")
        {
            return $"<video class=\"wiki-embed-media\" src=\"{WebUtility.HtmlEncode(url)}\" controls preload=\"metadata\"></video>";
        }
        if (mediaKind is "audio")
        {
            return $"<audio class=\"wiki-embed-media\" src=\"{WebUtility.HtmlEncode(url)}\" controls preload=\"metadata\"></audio>";
        }

        if (WikiEmbedResolver.TryResolve(url, out var embedUrl, out var providerLabel))
        {
            // allow-same-origin is safe here despite the usual allow-scripts pairing caution:
            // the src is always one of WikiEmbedResolver's hardcoded provider hostnames, never
            // a same-origin (to this app) or editor-chosen origin, so there is nothing for the
            // embedded frame to same-origin its way into beyond its own trusted player.
            return $"<div class=\"wiki-embed-frame\" data-provider=\"{WebUtility.HtmlEncode(providerLabel)}\">"
                + $"<iframe src=\"{WebUtility.HtmlEncode(embedUrl)}\" loading=\"lazy\" allowfullscreen "
                + "sandbox=\"allow-scripts allow-same-origin allow-popups allow-presentation\" "
                + "referrerpolicy=\"strict-origin-when-cross-origin\"></iframe></div>";
        }

        return $"<a href=\"{WebUtility.HtmlEncode(url)}\" target=\"_blank\" rel=\"noopener noreferrer\">{WebUtility.HtmlEncode(url)}</a>";
    }

    private static string RenderLinkedDatabase(WikiBlock block, string indentStyle, bool isInline = false)
    {
        var databaseId = block.Props.GetValueOrDefault("databaseId", string.Empty);
        var title = block.Props.GetValueOrDefault("databaseTitle", "Linked database");
        var databaseViewId = block.Props.GetValueOrDefault("databaseViewId", string.Empty);
        var databaseViewName = block.Props.GetValueOrDefault("databaseViewName", string.Empty);
        var cssClass = isInline ? "wiki-linked-database wiki-inline-database" : "wiki-linked-database";
        var viewAttribute = string.IsNullOrWhiteSpace(databaseViewId)
            ? string.Empty
            : $" data-database-view-id=\"{WebUtility.HtmlEncode(databaseViewId)}\"";
        var viewLabel = !isInline && !string.IsNullOrWhiteSpace(databaseViewName)
            ? $"<span class=\"wiki-linked-database-view\"> · {WebUtility.HtmlEncode(databaseViewName)}</span>"
            : string.Empty;
        return $"<div class=\"{cssClass}\" data-database-id=\"{WebUtility.HtmlEncode(databaseId)}\"{viewAttribute}{indentStyle}>"
            + $"<span aria-hidden=\"true\">▦</span><span>{WebUtility.HtmlEncode(title)}{viewLabel}</span></div>";
    }

    private static string RenderTable(WikiBlock block, string indentStyle)
    {
        if (block.Props.TryGetValue("tableJson", out var tableJson))
        {
            try
            {
                var richRows = JsonSerializer.Deserialize<List<List<List<WikiRichTextSpan>>>>(
                    tableJson,
                    WikiBlockJson.Options);
                if (richRows is { Count: > 0 })
                {
                    var hasHeader = block.Props.GetValueOrDefault("hasColumnHeader", "true") == "true";
                    var richHead = hasHeader
                        ? "<thead><tr>" + string.Concat(richRows[0].Select(cell => $"<th>{RenderRichText(cell)}</th>")) + "</tr></thead>"
                        : string.Empty;
                    var bodyRows = hasHeader ? richRows.Skip(1) : richRows;
                    var richBody = "<tbody>" + string.Concat(bodyRows.Select(row =>
                        "<tr>" + string.Concat(row.Select(cell => $"<td>{RenderRichText(cell)}</td>")) + "</tr>")) + "</tbody>";
                    return $"<table class=\"wiki-native-table\"{indentStyle}>{richHead}{richBody}</table>";
                }
            }
            catch (JsonException)
            {
                // Older table blocks use the pipe-delimited text fallback below.
            }
        }

        var rows = block.PlainText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries).Where(cell => cell.Length > 0).ToList())
            .Where(row => row.Count > 0)
            .ToList();
        if (rows.Count == 0) return $"<table class=\"wiki-native-table\"{indentStyle}></table>";

        var head = "<thead><tr>" + string.Concat(rows[0].Select(cell => $"<th>{WebUtility.HtmlEncode(cell)}</th>")) + "</tr></thead>";
        var body = "<tbody>" + string.Concat(rows.Skip(1).Select(row =>
            "<tr>" + string.Concat(row.Select(cell => $"<td>{WebUtility.HtmlEncode(cell)}</td>")) + "</tr>")) + "</tbody>";
        return $"<table class=\"wiki-native-table\"{indentStyle}>{head}{body}</table>";
    }

    private static string RenderColumns(WikiBlock block, string indentStyle)
    {
        if (block.Props.TryGetValue("columnRichTextJson", out var columnRichTextJson))
        {
            try
            {
                var richColumns = JsonSerializer.Deserialize<List<List<WikiRichTextSpan>>>(
                    columnRichTextJson,
                    WikiBlockJson.Options);
                if (richColumns is { Count: > 0 })
                {
                    return $"<div class=\"wiki-columns\"{indentStyle}>"
                        + string.Concat(richColumns.Select(column => $"<div>{RenderRichText(column)}</div>"))
                        + "</div>";
                }
            }
            catch (JsonException)
            {
                // Older columns use the plain "|||" fallback below.
            }
        }

        var columns = block.PlainText.Split("|||", StringSplitOptions.TrimEntries);
        return $"<div class=\"wiki-columns\"{indentStyle}>"
            + string.Concat(columns.Select(column => $"<div>{WebUtility.HtmlEncode(column).Replace("\n", "<br />")}</div>"))
            + "</div>";
    }

    private static string RenderTabs(WikiBlock block, string indentStyle)
    {
        var tabs = ParseTabs(block);
        var groupName = $"wiki-tabs-{block.Id:N}";
        var renderedTabs = string.Concat(tabs.Select((tab, index) =>
        {
            var inputId = $"{groupName}-{index + 1}";
            var title = string.IsNullOrWhiteSpace(tab.Title) ? $"Tab {index + 1}" : tab.Title.Trim();
            var selected = index == 0 ? " checked" : string.Empty;
            return $"<input class=\"wiki-tab-toggle\" type=\"radio\" name=\"{groupName}\" id=\"{inputId}\"{selected} />"
                + $"<label class=\"wiki-tab-label\" id=\"{inputId}-label\" for=\"{inputId}\">{WebUtility.HtmlEncode(title)}</label>"
                + $"<section class=\"wiki-tab-panel\" role=\"region\" aria-labelledby=\"{inputId}-label\">{RenderRichText(tab.RichText)}</section>";
        }));

        // The native radio controls make the read-only renderer interactive without inline
        // script. CSS shows only the panel paired with the selected radio button.
        return $"<div class=\"wiki-tabs-container\"{indentStyle}>"
            + $"<div class=\"wiki-tabs\" style=\"--wiki-tab-count:{tabs.Count}\">{renderedTabs}</div></div>";
    }

    // Internal rather than private so WikiMarkdownExporter (same assembly) can reuse the exact
    // same tabsJson/plain-text-fallback parsing instead of duplicating it.
    internal static IReadOnlyList<WikiTab> ParseTabs(WikiBlock block)
    {
        if (block.Props.TryGetValue("tabsJson", out var tabsJson))
        {
            try
            {
                var tabs = JsonSerializer.Deserialize<List<WikiTab>>(tabsJson, WikiBlockJson.Options);
                if (tabs is { Count: > 0 })
                {
                    return tabs.Select((tab, index) => new WikiTab(
                            string.IsNullOrWhiteSpace(tab.Title) ? $"Tab {index + 1}" : tab.Title,
                            tab.RichText ?? []))
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // Fall through to the plain-text representation written for compatibility.
            }
        }

        var contents = block.PlainText.Split("|||", StringSplitOptions.TrimEntries);
        if (contents.Length == 0 || (contents.Length == 1 && contents[0].Length == 0))
        {
            contents = [string.Empty, string.Empty];
        }

        return contents.Select((content, index) =>
                new WikiTab($"Tab {index + 1}", [new WikiRichTextSpan(content)]))
            .ToList();
    }

    private static string PlainTextTabs(WikiBlock block) => string.Join(" | ", ParseTabs(block)
        .Select(tab => $"{tab.Title}: {string.Concat(tab.RichText.Select(span => span.Text))}"));
}

public static class WikiRichTextColors
{
    public static IReadOnlySet<string> Supported { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gray", "brown", "orange", "yellow", "green", "blue", "purple", "pink", "red"
    };

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return Supported.Contains(normalized);
    }
}
