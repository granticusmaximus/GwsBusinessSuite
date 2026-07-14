using System.Text;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Domain.Entities;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GwsBusinessSuite.Application.Wiki;

// Pure helpers backing the Wiki editor's "richer editing" features (wiki-links,
// image-embed picker, TOC) - kept free of Blazor/JS so they're unit-testable directly.
// The interactive parts (typing-triggered autocomplete, click-to-navigate) live in
// Wiki.razor + wwwroot/js/markdownEditor.js and wikiLinks.js, calling back into these.
public static class WikiMarkdownHelper
{
    private static readonly Regex WikiLinkPattern = new(@"\[\[([^\[\]]+)\]\]", RegexOptions.Compiled);

    // Rewrites [[Page Title]] into a real Markdown link the renderer turns into
    // <a href="wikilink:{id}">...</a> - wikiLinks.js intercepts clicks on that href scheme
    // and calls back into Wiki.razor's NavigateToWikiPageId rather than actually
    // navigating, since this app has no per-page URL route to link to. A title with no
    // matching page renders as plain emphasized text instead of a dead link.
    public static string ResolveWikiLinks(string markdown, IReadOnlyList<WikiPage> pages)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown ?? string.Empty;
        }

        return WikiLinkPattern.Replace(markdown, match =>
        {
            var title = match.Groups[1].Value.Trim();
            var target = pages.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
            return target is null
                ? $"*{title}* _(wiki page not found)_"
                : $"[{title}](wikilink:{target.Id})";
        });
    }

    // Walks the same Markdig AST the page preview renders from, so anchor ids always match
    // what UseAutoIdentifiers() actually assigned - no reimplementation of its slugging
    // rules that could silently drift out of sync.
    public static string BuildTableOfContentsMarkdown(string markdown, MarkdownPipeline pipeline)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var document = Markdown.Parse(markdown, pipeline);
        var headings = document.Descendants<HeadingBlock>()
            .Where(h => h.Level is >= 2 and <= 4)
            .Select(h => (h.Level, Text: GetHeadingText(h), Id: h.GetAttributes()?.Id))
            .Where(h => !string.IsNullOrWhiteSpace(h.Text) && !string.IsNullOrWhiteSpace(h.Id))
            .ToList();

        if (headings.Count == 0)
        {
            return string.Empty;
        }

        var minLevel = headings.Min(h => h.Level);
        var sb = new StringBuilder();
        foreach (var (level, text, id) in headings)
        {
            var indent = new string(' ', (level - minLevel) * 2);
            sb.AppendLine($"{indent}- [{text}](#{id})");
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }

    public static IReadOnlyList<string> SearchPageTitles(string query, IReadOnlyList<WikiPage> pages, int maxResults = 8)
    {
        var candidates = string.IsNullOrWhiteSpace(query)
            ? pages
            : pages.Where(p => p.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        return candidates
            .Select(p => p.Title)
            .Take(maxResults)
            .ToList();
    }

    private static string GetHeadingText(HeadingBlock heading)
    {
        if (heading.Inline is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var literal in heading.Inline.Descendants<LiteralInline>())
        {
            sb.Append(literal.Content.ToString());
        }

        return sb.ToString();
    }
}
