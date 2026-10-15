using System.Net;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.Wiki;

// Publish a page tree as a mini-site (Sentinel item 30): a page share with IncludeSubpages shows
// the page and its sub-pages with sidebar navigation and search, under the share's own token,
// password, expiry and indexing options. What is published:
//   - the shared page and its sub-pages, depth-first in sidebar order (max MaxDepth levels and
//     MaxPages pages);
//   - never a trashed page, and never a sub-page with its own sharing (explicit permissions) - a
//     page somebody restricted on purpose stays hidden, with everything under it;
//   - links to pages outside the published tree lose their link, so they lead nowhere.
public static partial class SentinelPublishedSite
{
    public const int MaxPages = 200;
    public const int MaxDepth = 8;
    public const int MaxSiteTitleLength = 120;
    public const int MaxSearchResults = 20;

    // One page as the tree builder sees it.
    public sealed record Node(Guid Id, Guid? ParentId, int SortOrder, string Title, string? Icon, bool IsTrashed, bool HasOwnSharing);

    public sealed record Page(Guid Id, Guid? ParentId, string Title, string? Icon, int Depth);

    public sealed record Site(string Title, Guid RootId, IReadOnlyList<Page> Pages, IReadOnlyList<string> HiddenTitles)
    {
        public bool Contains(Guid pageId) => Pages.Any(page => page.Id == pageId);
    }

    public sealed record SearchResult(Guid Id, string Title, string Snippet);

    // Null when the root itself is missing or trashed.
    public static Site? Build(Guid rootId, IReadOnlyCollection<Node> nodes, string? siteTitle)
    {
        var byId = nodes.ToDictionary(node => node.Id);
        if (!byId.TryGetValue(rootId, out var root) || root.IsTrashed) return null;
        var children = nodes
            .Where(node => node.ParentId is not null && !node.IsTrashed)
            .GroupBy(node => node.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(node => node.SortOrder)
                .ThenBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
                .ToList());
        var pages = new List<Page> { new(root.Id, null, Title(root), root.Icon, 0) };
        var hidden = new List<string>();
        var visited = new HashSet<Guid> { root.Id };

        void Walk(Guid parentId, int depth)
        {
            if (depth > MaxDepth || !children.TryGetValue(parentId, out var kids)) return;
            foreach (var kid in kids)
            {
                if (!visited.Add(kid.Id)) continue;
                if (kid.HasOwnSharing)
                {
                    hidden.Add(Title(kid));
                    continue;
                }
                if (pages.Count >= MaxPages) return;
                pages.Add(new Page(kid.Id, parentId, Title(kid), kid.Icon, depth));
                Walk(kid.Id, depth + 1);
            }
        }

        Walk(root.Id, 1);
        var title = string.IsNullOrWhiteSpace(siteTitle) ? Title(root) : siteTitle.Trim();
        return new Site(title, root.Id, pages, hidden);
    }

    public static string PageUrl(string token, Guid rootId, Guid pageId) =>
        pageId == rootId ? $"/sentinel/share/{token}" : $"/sentinel/share/{token}/{pageId}";

    // Rendered page HTML links pages as href="wikilink:{id}". Inside the site those become the
    // site's own URLs; anything else is left as plain text so a visitor never reaches a page
    // that wasn't published (it wouldn't open anyway, but a dead link is worse than none).
    public static string RewriteLinks(string html, Site site, string token) =>
        WikiLinkAnchor().Replace(html, match =>
        {
            var inner = match.Groups["inner"].Value;
            if (Guid.TryParse(match.Groups["id"].Value, out var pageId) && site.Contains(pageId))
            {
                var url = WebUtility.HtmlEncode(PageUrl(token, site.RootId, pageId));
                return $"<a{match.Groups["before"].Value}href=\"{url}\"{match.Groups["after"].Value}>{inner}</a>";
            }
            return $"<span class=\"sentinel-public-unlinked\">{inner}</span>";
        });

    // Title, then text matches; texts maps page id -> its plain text.
    public static IReadOnlyList<SearchResult> Search(Site site, IReadOnlyDictionary<Guid, string> texts, string? query)
    {
        var term = (query ?? string.Empty).Trim();
        if (term.Length < 2) return [];
        return site.Pages
            .Select(page =>
            {
                var text = texts.GetValueOrDefault(page.Id) ?? string.Empty;
                var inTitle = page.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
                var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                return (page, inTitle, at, text);
            })
            .Where(item => item.inTitle || item.at >= 0)
            .OrderByDescending(item => item.inTitle)
            .ThenBy(item => item.page.Depth)
            .Take(MaxSearchResults)
            .Select(item => new SearchResult(item.page.Id, item.page.Title, Snippet(item.text, item.at, term.Length)))
            .ToList();
    }

    private static string Snippet(string text, int at, int length)
    {
        if (text.Length == 0) return string.Empty;
        if (at < 0) return text.Length <= 140 ? text : text[..140].TrimEnd() + "…";
        var start = Math.Max(0, at - 60);
        var end = Math.Min(text.Length, at + length + 80);
        return (start > 0 ? "…" : string.Empty) + text[start..end].Trim() + (end < text.Length ? "…" : string.Empty);
    }

    private static string Title(Node node) => string.IsNullOrWhiteSpace(node.Title) ? "Untitled" : node.Title;

    [GeneratedRegex("<a(?<before>[^>]*?\\s)href=\"wikilink:(?<id>[^\"]*)\"(?<after>[^>]*)>(?<inner>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex WikiLinkAnchor();
}

public interface ISentinelPublishedSiteService
{
    // The share's published pages: the whole tree for a site, just the page otherwise. Null for
    // database/automation shares, or when the shared page is gone.
    Task<SentinelPublishedSite.Site?> GetSiteAsync(SentinelShareView share, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelPublishedSite.SearchResult>> SearchAsync(SentinelPublishedSite.Site site, string? query, CancellationToken cancellationToken = default);
}
