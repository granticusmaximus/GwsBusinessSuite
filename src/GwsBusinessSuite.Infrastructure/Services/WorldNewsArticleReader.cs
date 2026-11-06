using System.Collections.Concurrent;
using GwsBusinessSuite.Infrastructure.WebCrawl;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// The opening of the outlets' own articles for SentinelGPT's analysis of an event (06-11-26), so it
// compares what the articles say, not just their headlines. Up to MaxArticles a document, one per
// outlet, languages taking turns. Only links to an outlet's own site are read: Google News's links
// can't be followed without running its page in a browser. Each fetch goes through a client that
// refuses private addresses and leaves alone, for a day, any site that refuses it (RefusedHostPause);
// what was read, or that it couldn't be, is kept so the document opens fast the next time.
public sealed class WorldNewsArticleReader(IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<WorldNewsArticleReader> logger)
{
    public const string HttpClientName = "world-news-articles";
    public const int MaxArticles = 14;
    public const int MinTextChars = 200;
    private const int MaxParallel = 4;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(12);
    // A page that couldn't be read is tried again after this.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);

    public static bool IsReadable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
        && !uri.Host.Equals("news.google.com", StringComparison.OrdinalIgnoreCase);

    // One report per outlet with a link to its own site, newest first, languages taking turns
    // (biggest first) so the articles read aren't all in one language.
    public static IReadOnlyList<WorldArticle> Choose(IReadOnlyList<WorldArticle> articles, int max = MaxArticles)
    {
        var queues = articles.Where(a => IsReadable(a.Url))
            .GroupBy(a => a.Outlet)
            .Select(outlet => outlet.OrderByDescending(a => a.Snippet is not null).ThenByDescending(a => a.SeenAt).First())
            .GroupBy(a => a.Language)
            .OrderByDescending(language => language.Count())
            .Select(language => new Queue<WorldArticle>(language.OrderByDescending(a => a.SeenAt)))
            .ToList();
        var chosen = new List<WorldArticle>();
        while (chosen.Count < max && queues.Any(queue => queue.Count > 0))
        {
            foreach (var queue in queues.Where(queue => queue.Count > 0))
            {
                if (chosen.Count >= max) break;
                chosen.Add(queue.Dequeue());
            }
        }
        return chosen;
    }

    // Article id -> excerpt, for those that could be read.
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(WorldNewsStore store, IReadOnlyList<WorldArticle> articles, CancellationToken cancellationToken)
    {
        var chosen = Choose(articles);
        var known = store.ArticleTexts(chosen.Select(a => a.Id));
        var excerpts = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var now = time.GetUtcNow();
        foreach (var (id, (excerpt, _)) in known)
            if (excerpt is not null) excerpts[id] = excerpt;
        var toRead = chosen.Where(a => !known.TryGetValue(a.Id, out var before) || (before.Excerpt is null && now - before.FetchedAt > RetryAfter)).ToList();
        await Parallel.ForEachAsync(toRead, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = cancellationToken },
            async (article, ct) =>
            {
                var (excerpt, reason) = await FetchAsync(article.Url, ct);
                store.SaveArticleText(article.Id, excerpt, reason, time.GetUtcNow());
                if (excerpt is not null) excerpts[article.Id] = excerpt;
            });
        return excerpts;
    }

    private async Task<(string? Excerpt, string? Reason)> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (null, "not a web link");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FetchTimeout);
        try
        {
            using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(uri, timeout.Token);
            if (!response.IsSuccessStatusCode) return (null, $"answered {(int)response.StatusCode}");
            if (response.Content.Headers.ContentType?.MediaType is { } type && !type.Contains("html", StringComparison.OrdinalIgnoreCase))
                return (null, "not a web page");
            var html = await response.Content.ReadAsStringAsync(timeout.Token);
            var excerpt = Excerpt(WebPageReader.ReadHtml(html, response.RequestMessage?.RequestUri ?? uri));
            return excerpt is null ? (null, "no article text") : (excerpt, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or HostPausedException or InvalidOperationException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogInformation("World News: couldn't read {Host} for an analysis ({Reason})", uri.Host, ex.Message);
            return (null, ex is HostPausedException ? "the site refused this server" : ex is TaskCanceledException ? "too slow" : "couldn't connect");
        }
    }

    // The article's opening: its readable text, whitespace flattened, at most a few paragraphs.
    // Too little text (a paywall, a cookie wall) counts as unread; the description alone is no better
    // than the feed's.
    public static string? Excerpt(CrawledPage page)
    {
        var text = string.Join(' ', page.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length < MinTextChars) return null;
        return text.Length <= EventAnalyzer.MaxExcerptChars * 2 ? text : text[..(EventAnalyzer.MaxExcerptChars * 2)].TrimEnd();
    }
}
