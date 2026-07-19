using System.Text.RegularExpressions;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Pure helpers backing the Wiki editor's "richer editing" features (wiki-links, wiki-link
// autocomplete search) - kept free of Blazor/JS so they're unit-testable directly. The
// interactive parts (typing-triggered autocomplete, click-to-navigate) live in Wiki.razor +
// wwwroot/js/wiki-block-editor.js and wikiLinks.js, calling back into these.
public static class WikiMarkdownHelper
{
    private static readonly Regex WikiLinkPattern = new(@"\[\[([^\[\]]+)\]\]", RegexOptions.Compiled);

    // Rewrites [[Page Title]] into a real Markdown link the renderer turns into
    // <a href="wikilink:{id}">...</a> - wikiLinks.js intercepts clicks on that href scheme
    // and calls back into Wiki.razor's NavigateToWikiPageId rather than actually
    // navigating, since this app has no per-page URL route to link to. A title with no
    // matching page renders as plain emphasized text instead of a dead link.
    //
    // Only legacy "markdown"-type blocks (pre-existing content carried over by the
    // Markdown-to-block backfill) still use this [[..]] syntax - blocks authored in the new
    // block editor link via a RichTextSpan.Link (still using the "wikilink:{id}" scheme so
    // wikiLinks.js's click handling stays unchanged, just inserted directly rather than
    // resolved from bracket syntax at render time).
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
}
