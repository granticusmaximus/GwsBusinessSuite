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

        // A title wins; otherwise the one page with that alias (WikiPageAliases.Resolve).
        return WikiLinkPattern.Replace(markdown, match =>
        {
            var title = match.Groups[1].Value.Trim();
            var target = WikiPageAliases.Resolve(title, pages);
            return target is null
                ? $"*{title}* _(wiki page not found)_"
                : $"[{title}](wikilink:{target.Id})";
        });
    }

    // Titles and aliases both match. Best first: exact title, exact alias, title starting with the
    // query, alias starting with it, then anything containing it. A suggestion found through an
    // alias carries it, so the menu can say "also known as" and the link can read as typed.
    public static IReadOnlyList<WikiLinkSuggestion> SearchLinkSuggestions(string query, IReadOnlyList<WikiPage> pages, int maxResults = 8)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return pages.Select(p => new WikiLinkSuggestion(p.Id, p.Title, p.Icon)).Take(maxResults).ToList();
        }

        query = query.Trim();
        var ranked = new List<(int Rank, int Order, WikiLinkSuggestion Suggestion)>();
        for (var order = 0; order < pages.Count; order++)
        {
            var page = pages[order];
            var titleRank = Rank(page.Title, query, exact: 0, prefix: 2, contains: 4);
            var bestAlias = WikiPageAliases.Parse(page.Aliases)
                .Select(alias => (Alias: alias, Rank: Rank(alias, query, exact: 1, prefix: 3, contains: 5)))
                .Where(match => match.Rank >= 0)
                .OrderBy(match => match.Rank)
                .FirstOrDefault();
            if (titleRank < 0 && bestAlias.Alias is null) continue;
            var useAlias = bestAlias.Alias is not null && (titleRank < 0 || bestAlias.Rank < titleRank);
            ranked.Add(useAlias
                ? (bestAlias.Rank, order, new WikiLinkSuggestion(page.Id, page.Title, page.Icon, bestAlias.Alias))
                : (titleRank, order, new WikiLinkSuggestion(page.Id, page.Title, page.Icon)));
        }
        return ranked.OrderBy(item => item.Rank).ThenBy(item => item.Order).Select(item => item.Suggestion).Take(maxResults).ToList();
    }

    private static int Rank(string? text, string query, int exact, int prefix, int contains)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase)) return exact;
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return prefix;
        return text.Contains(query, StringComparison.OrdinalIgnoreCase) ? contains : -1;
    }
}
