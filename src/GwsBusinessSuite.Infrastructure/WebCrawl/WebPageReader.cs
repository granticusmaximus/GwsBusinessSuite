using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using SmartReader;

namespace GwsBusinessSuite.Infrastructure.WebCrawl;

public sealed record CrawledPage(string? Title, string? Description, DateTimeOffset? PublishedAt, Uri? Canonical,
    bool NoIndex, bool NoFollow, string Text, IReadOnlyList<Uri> Links);

// Page URLs (newest first when the file dates them) and further sitemaps, from a sitemap, a
// sitemap index, an RSS feed or an Atom feed.
public sealed record LinkList(IReadOnlyList<(Uri Url, DateTimeOffset? Date)> Pages, IReadOnlyList<(Uri Url, DateTimeOffset? Date)> Sitemaps);

// Reading what the crawler fetched: the readable text, title, date and links of a page, and the
// links in a sitemap or feed. URLs are normalised so one page has one address.
public static partial class WebPageReader
{
    public const int MaxTextLength = 10000;
    private const int MaxLinksPerPage = 400;

    private static readonly string[] TrackingParameters = ["fbclid", "gclid", "mc_cid", "mc_eid", "igshid", "ref_src", "cmpid", "ocid"];
    private static readonly string[] SkippedExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".pdf", ".zip", ".gz", ".mp3", ".mp4", ".m4a", ".mov", ".avi", ".webm",
         ".css", ".js", ".json", ".woff", ".woff2", ".ttf", ".exe", ".dmg", ".apk", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"];

    public static CrawledPage ReadHtml(string html, Uri url)
    {
        var document = new HtmlParser().ParseDocument(html);
        string? Meta(params string[] names) => names
            .Select(n => document.QuerySelector($"meta[name='{n}'], meta[property='{n}'], meta[itemprop='{n}']")?.GetAttribute("content"))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        var robots = (Meta("robots") ?? string.Empty).ToLowerInvariant();
        var noIndex = robots.Contains("noindex", StringComparison.Ordinal) || robots.Contains("none", StringComparison.Ordinal);
        var noFollow = robots.Contains("nofollow", StringComparison.Ordinal) || robots.Contains("none", StringComparison.Ordinal);
        var title = Clean(Meta("og:title") ?? document.Title);
        var description = Clean(Meta("description", "og:description"));
        var published = ParseDate(Meta("article:published_time", "og:published_time", "datePublished", "pubdate", "date", "dc.date"))
            ?? ParseDate(document.QuerySelector("time[datetime]")?.GetAttribute("datetime"))
            ?? document.QuerySelectorAll("script[type='application/ld+json']")
                .Select(s => JsonLdDate().Match(s.TextContent)).Where(m => m.Success)
                .Select(m => ParseDate(m.Groups[1].Value)).FirstOrDefault(d => d is not null);
        var canonical = Normalize(document.QuerySelector("link[rel='canonical']")?.GetAttribute("href"), url);

        var links = noFollow ? [] : document.QuerySelectorAll("a[href]")
            .Where(a => !(a.GetAttribute("rel") ?? string.Empty).Contains("nofollow", StringComparison.OrdinalIgnoreCase))
            .Select(a => Normalize(a.GetAttribute("href"), url))
            .Where(u => u is not null).Select(u => u!)
            .DistinctBy(u => u.AbsoluteUri)
            .Take(MaxLinksPerPage)
            .ToList();

        var text = string.Empty;
        try
        {
            var article = new Reader(url.AbsoluteUri, html).GetArticle();
            if (article?.IsReadable == true) text = article.TextContent ?? string.Empty;
        }
        catch (Exception)
        {
            // SmartReader gives up on some markup; the page's own text below is the fallback.
        }
        if (text.Length < 300)
        {
            foreach (var element in document.QuerySelectorAll("script, style, noscript, nav, header, footer, aside, form").ToList()) element.Remove();
            text = document.Body?.TextContent ?? string.Empty;
        }
        text = Whitespace().Replace(text, " ").Trim();
        if (text.Length > MaxTextLength) text = text[..MaxTextLength];
        return new CrawledPage(title, description, published, canonical, noIndex, noFollow, text, links);
    }

    public static LinkList ReadXml(string xml, Uri url)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return new LinkList([], []);
        }
        var root = document.Root;
        if (root is null) return new LinkList([], []);
        static string? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        (Uri Url, DateTimeOffset? Date)? Entry(string? href, string? date) =>
            Normalize(href, url) is { } u ? (u, ParseDate(date)) : null;

        var pages = new List<(Uri, DateTimeOffset?)>();
        var sitemaps = new List<(Uri, DateTimeOffset?)>();
        switch (root.Name.LocalName)
        {
            case "sitemapindex":
                sitemaps.AddRange(root.Elements().Where(e => e.Name.LocalName == "sitemap")
                    .Select(e => Entry(Child(e, "loc"), Child(e, "lastmod"))).Where(e => e is not null).Select(e => e!.Value));
                break;
            case "urlset":
                pages.AddRange(root.Elements().Where(e => e.Name.LocalName == "url").Select(e =>
                {
                    var news = e.Elements().FirstOrDefault(n => n.Name.LocalName == "news");
                    return Entry(Child(e, "loc"), (news is null ? null : Child(news, "publication_date")) ?? Child(e, "lastmod"));
                }).Where(e => e is not null).Select(e => e!.Value));
                break;
            case "rss" or "RDF":
                pages.AddRange(root.Descendants().Where(e => e.Name.LocalName == "item")
                    .Select(e => Entry(Child(e, "link"), Child(e, "pubDate") ?? Child(e, "date"))).Where(e => e is not null).Select(e => e!.Value));
                break;
            case "feed":
                pages.AddRange(root.Elements().Where(e => e.Name.LocalName == "entry").Select(e =>
                {
                    var link = e.Elements().Where(l => l.Name.LocalName == "link")
                        .FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate");
                    return Entry((string?)link?.Attribute("href"), Child(e, "published") ?? Child(e, "updated"));
                }).Where(e => e is not null).Select(e => e!.Value));
                break;
        }
        return new LinkList(
            pages.DistinctBy(p => p.Item1.AbsoluteUri).OrderByDescending(p => p.Item2 ?? DateTimeOffset.MinValue).ToList(),
            sitemaps.DistinctBy(s => s.Item1.AbsoluteUri).OrderByDescending(s => s.Item2 ?? DateTimeOffset.MinValue).ToList());
    }

    // One address per page: absolute http(s), no fragment, no tracking parameters, lower-case
    // host, default port dropped. Links to images, documents and other non-pages are skipped.
    public static Uri? Normalize(string? href, Uri baseUrl)
    {
        href = href?.Trim();
        if (string.IsNullOrEmpty(href) || href.StartsWith('#') || href.Length > 2000) return null;
        if (!Uri.TryCreate(baseUrl, href, out var absolute) || absolute.Scheme is not ("http" or "https")) return null;
        var path = absolute.AbsolutePath;
        if (SkippedExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return null;
        var query = absolute.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p =>
            {
                var name = p.Split('=')[0];
                return !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) && !TrackingParameters.Contains(name, StringComparer.OrdinalIgnoreCase);
            });
        var builder = new UriBuilder(absolute)
        {
            Host = absolute.Host.ToLowerInvariant(),
            Fragment = string.Empty,
            Query = string.Join('&', query)
        };
        if (absolute.IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    public static DateTimeOffset? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    private static string? Clean(string? value)
    {
        value = value is null ? null : Whitespace().Replace(value, " ").Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length > 300 ? value[..300] : value;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("\"datePublished\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex JsonLdDate();
}
