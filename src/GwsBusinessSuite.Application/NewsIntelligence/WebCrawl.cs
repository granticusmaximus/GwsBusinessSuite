using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Application.NewsIntelligence;

// Media Watch's own web crawler: it reads sites (the outlets Media Watch and World News already
// follow, sites added by hand, and sites those link to), keeps their pages in a full-text index,
// and matches each watched topic's keywords against what it finds, like a small search engine.
public sealed record WebSearchHit(string Url, string Host, string Title, string Snippet, DateTimeOffset? PublishedAt, DateTimeOffset FetchedAt);

public sealed record WebCrawlStatus(bool Enabled, long PagesIndexed, int Sites, int PagesToday, int DailyLimit, int Queued,
    DateTimeOffset? LastCrawlAt, int TopicMatchesToday, int RetentionDays);

public sealed record WebCrawlSite(string Host, string HomeUrl, bool AddedByHand, int PagesIndexed, DateTimeOffset? LastFetchedAt, string? LastError);

public sealed class WebCrawlException(string message) : Exception(message);

public interface IWebCrawlService
{
    Task<IReadOnlyList<WebSearchHit>> SearchAsync(string query, int limit = 50, CancellationToken ct = default);
    Task<WebCrawlStatus> GetStatusAsync(CancellationToken ct = default);
    // Sites added by hand first, then the busiest.
    Task<IReadOnlyList<WebCrawlSite>> ListSitesAsync(int limit = 100, CancellationToken ct = default);
    Task<WebCrawlSite> AddSiteAsync(string url, CancellationToken ct = default);
    Task RemoveSiteAsync(string host, CancellationToken ct = default);
}

// Turns what someone types (or a topic's comma-separated keywords) into an FTS5 query, so no
// input can reach FTS5's own syntax: every word or quoted phrase becomes a quoted string.
public static partial class WebSearchQuery
{
    // "climate policy" "ev" -> all of the words (and phrases) must appear.
    public static string? FromSearch(string text)
    {
        var parts = new List<string>();
        foreach (Match match in Token().Matches(text ?? string.Empty))
        {
            var term = (match.Groups["phrase"].Success ? match.Groups["phrase"].Value : match.Groups["word"].Value).Trim();
            if (term.Any(char.IsLetterOrDigit)) parts.Add(Quote(term));
            if (parts.Count == 12) break;
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    // "Atlanta Braves, MLB trade" -> either phrase.
    public static string? FromKeywords(string keywords)
    {
        var parts = (keywords ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Any(char.IsLetterOrDigit))
            .Take(20)
            .Select(Quote)
            .ToList();
        return parts.Count == 0 ? null : string.Join(" OR ", parts);
    }

    private static string Quote(string term)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in term)
        {
            if (c == '"') builder.Append("\"\"");
            else if (!char.IsControl(c)) builder.Append(c);
        }
        return builder.Append('"').ToString();
    }

    [GeneratedRegex("\"(?<phrase>[^\"]+)\"|(?<word>[^\\s\"]+)")]
    private static partial Regex Token();
}
