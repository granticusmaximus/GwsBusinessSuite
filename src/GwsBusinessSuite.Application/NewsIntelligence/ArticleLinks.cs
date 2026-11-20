using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.NewsIntelligence;

// One article, one link (20-11-26): the outlet's own canonical address, so a Google News redirect
// and the outlet's link count as the same article for read state and duplicates.
public static partial class ArticleLinks
{
    // Hosts whose canonical is the aggregator itself, never the article.
    private static readonly string[] AggregatorHosts = ["news.google.com", "consent.google.com", "news.yahoo.com", "msn.com"];

    public static string? Canonical(string pageUrl, string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var head = html.Length > 200_000 ? html[..200_000] : html;
        var candidate = CanonicalLink().Match(head) is { Success: true } link ? link.Groups["href"].Value
            : OgUrl().Match(head) is { Success: true } og ? og.Groups["href"].Value
            : null;
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        candidate = System.Net.WebUtility.HtmlDecode(candidate.Trim());
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return null;
        if (!Uri.TryCreate(page, candidate, out var resolved)) return null;
        if (resolved.Scheme is not ("http" or "https")) return null;
        var host = resolved.Host.ToLowerInvariant();
        if (AggregatorHosts.Any(a => host == a || host.EndsWith("." + a, StringComparison.Ordinal))) return null;
        // A canonical pointing at a site's home page is a template mistake, not the article.
        if (resolved.AbsolutePath is "/" or "" && page.AbsolutePath.Length > 1) return null;
        return resolved.GetLeftPart(UriPartial.Query);
    }

    private static readonly string[] Redirectors = ["feedproxy.google.com", "feeds.feedburner.com", "news.google.com", "rss.app", "flipboard.com"];

    // The link to keep: the canonical when it's the same site (http→https, AMP, tracking removed)
    // or the original was only a redirector; a syndicated copy keeps its own link.
    public static string Preferred(string originalUrl, string? canonical)
    {
        var original = StripTracking(originalUrl);
        if (canonical is null || !Uri.TryCreate(original, UriKind.Absolute, out var from) || !Uri.TryCreate(canonical, UriKind.Absolute, out var to))
            return original;
        var fromHost = from.Host.ToLowerInvariant();
        var redirector = Redirectors.Any(r => fromHost == r || fromHost.EndsWith("." + r, StringComparison.Ordinal));
        return redirector || SiteOf(fromHost) == SiteOf(to.Host.ToLowerInvariant()) ? StripTracking(canonical) : original;
    }

    // utm_* and the usual click ids make one article look like many.
    public static string StripTracking(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query)) return url;
        var kept = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var name = p.Split('=')[0].ToLowerInvariant();
                return !name.StartsWith("utm_", StringComparison.Ordinal) && name is not ("fbclid" or "gclid" or "mc_cid" or "mc_eid" or "cmpid" or "ocid");
            })
            .ToList();
        var builder = new UriBuilder(uri) { Query = string.Join('&', kept) };
        return builder.Uri.GetLeftPart(UriPartial.Query).TrimEnd('?');
    }

    // The last two labels (bbc.co.uk keeps three), enough to tell amp.cnn.com and cnn.com from another site.
    private static string SiteOf(string host)
    {
        var labels = host.Split('.');
        var take = labels.Length >= 3 && labels[^2].Length <= 3 && labels[^1].Length == 2 ? 3 : 2;
        return string.Join('.', labels.TakeLast(Math.Min(take, labels.Length)));
    }

    [GeneratedRegex("""<link\b(?=[^>]*\brel\s*=\s*["']?canonical["'\s>])[^>]*\bhref\s*=\s*["'](?<href>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalLink();

    [GeneratedRegex("""<meta\b(?=[^>]*\bproperty\s*=\s*["']og:url["'])[^>]*\bcontent\s*=\s*["'](?<href>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex OgUrl();
}
