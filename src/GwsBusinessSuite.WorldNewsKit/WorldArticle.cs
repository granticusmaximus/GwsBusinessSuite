using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// One report from one outlet. Language and Country are English names ("Turkish", "Turkey").
// GroupHint ties together reports a feed already says are the same story (a Google News item
// and the related coverage listed under it), so they land in one event.
public sealed record WorldArticle(
    string Id,
    string Url,
    string Title,
    string Outlet,
    string Language,
    string Country,
    DateTimeOffset SeenAt,
    string Feed,
    string? GroupHint = null);

public static partial class ArticleNormalizer
{
    private static readonly string[] TrackingPrefixes = ["utm_", "fbclid", "gclid", "ocid", "cmpid", "ref"];

    // One id per story URL, whichever feed it came through: lower-case host without "www.",
    // no fragment, no tracking parameters, no trailing slash.
    public static string CanonicalUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url.Trim();
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var query = string.Join('&', uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => !TrackingPrefixes.Any(prefix => pair.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))));
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : string.Empty;
        return $"{uri.Scheme}://{host}{path}{(query.Length > 0 ? "?" + query : string.Empty)}";
    }

    public static string IdFor(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalUrl(url))))[..32];

    // Feed titles arrive HTML-encoded and GDELT spaces out punctuation ("Flut : Zahl", "sel , o").
    public static string CleanTitle(string title)
    {
        var text = WebUtility.HtmlDecode(title ?? string.Empty);
        text = SpacedPunctuation().Replace(text, "$1");
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+([,:;.!?])(?=\s|$)")]
    private static partial Regex SpacedPunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
