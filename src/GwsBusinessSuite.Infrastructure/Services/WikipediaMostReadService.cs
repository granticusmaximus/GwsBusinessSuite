using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record MostReadArticle(string Title, long Views, int Rank, string Url);

// Wikipedia's most-read articles beside a World News story (07-11-26): what people are looking up,
// next to what the outlets report. English Wikipedia's daily top list from Wikimedia's pageviews
// API, key-less. A day's list comes out the next day, so this reads yesterday's (UTC), or the day
// before's when yesterday's isn't out yet.
public sealed class WikipediaMostReadService(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<WikipediaMostReadService> logger)
{
    private const string CacheKey = "worldnews:wikipedia-most-read";
    // The top of the list that counts as "most read" (the API gives 1,000).
    public const int Considered = 300;
    private static readonly string[] Namespaces = ["Special:", "Wikipedia:", "File:", "Portal:", "Help:", "Category:", "Template:", "Talk:", "User:"];

    public async Task<IReadOnlyList<MostReadArticle>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<MostReadArticle>? cached) && cached is not null) return cached;
        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        for (var attempt = 0; attempt < 2; attempt++, day = day.AddDays(-1))
        {
            try
            {
                using var response = await httpClient.GetAsync(Url(day), ct);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                var articles = Parse(await response.Content.ReadAsStringAsync(ct));
                cache.Set(CacheKey, articles, TimeSpan.FromHours(3));
                return articles;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "World News: Wikipedia's most-read list failed");
                break;
            }
        }
        IReadOnlyList<MostReadArticle> none = [];
        cache.Set(CacheKey, none, TimeSpan.FromMinutes(30));
        return none;
    }

    private static string Url(DateOnly day) =>
        string.Create(CultureInfo.InvariantCulture, $"https://wikimedia.org/api/rest_v1/metrics/pageviews/top/en.wikipedia/all-access/{day:yyyy}/{day:MM}/{day:dd}");

    // Articles only: the main page and special and project pages are left out.
    public static IReadOnlyList<MostReadArticle> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0
            || !items[0].TryGetProperty("articles", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var articles = new List<MostReadArticle>();
        foreach (var a in list.EnumerateArray())
        {
            var name = a.TryGetProperty("article", out var article) && article.ValueKind == JsonValueKind.String ? article.GetString()! : string.Empty;
            if (name.Length == 0 || name is "Main_Page" or "-" || Namespaces.Any(ns => name.StartsWith(ns, StringComparison.Ordinal))) continue;
            var views = a.TryGetProperty("views", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            var rank = a.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : articles.Count + 1;
            articles.Add(new MostReadArticle(name.Replace('_', ' '), views, rank, "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(name)));
            if (articles.Count == Considered) break;
        }
        return articles;
    }

    // The articles a story's headlines name, as whole words: the article's title without a
    // bracketed note, or without a leading year ("2026 Israeli legislative election"). One-word
    // titles ("Israel") are left out, since they'd match every story about a place. Most read
    // first, at most `max`.
    public static IReadOnlyList<MostReadArticle> Matching(IEnumerable<string?> headlines, IReadOnlyList<MostReadArticle> articles, int max = 2)
    {
        var text = string.Join(" | ", headlines.Where(h => !string.IsNullOrWhiteSpace(h))).Replace('’', '\'');
        if (text.Length == 0) return [];
        return articles
            .Where(a => Phrases(a.Title).Any(phrase => Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))))
            .OrderBy(a => a.Rank)
            .Take(max)
            .ToList();
    }

    private static IEnumerable<string> Phrases(string title)
    {
        var bare = Regex.Replace(title, @"\s*\([^)]*\)\s*$", string.Empty).Replace('’', '\'').Trim();
        if (bare.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2) yield return bare;
        var noYear = Regex.Replace(bare, @"^\d{4}(?:–\d{2,4})?\s+", string.Empty);
        if (noYear != bare && noYear.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2) yield return noYear;
    }
}
