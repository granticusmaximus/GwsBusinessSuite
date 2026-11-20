using System.Net;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.NewsIntelligence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartReader;

namespace GwsBusinessSuite.Infrastructure.Services;

// Media Watch's reader (20-11-26): fetches the article once (the World News article client: public
// addresses only, an honest User-Agent, and a site that refuses this server is left alone for a
// day), keeps the readable part (SmartReader, Firefox's Reader View algorithm), and renders it from
// Markdown with raw HTML off, so nothing of the outlet's markup or scripts reaches the page.
public sealed class ArticleReaderService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<ArticleReaderService> logger,
    IOllamaService? ollama = null,
    IOptions<ContentStudioOptions>? studioOptions = null) : IArticleReader
{
    public const int MinReadableChars = 300;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    public async Task<ReaderArticle> ReadAsync(string url, string? fallbackTitle = null, CancellationToken cancellationToken = default)
    {
        var title = string.IsNullOrWhiteSpace(fallbackTitle) ? url : fallbackTitle.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https"))
            return Problem(url, title, "That link isn't a web address the reader can open.");
        if (cache.TryGetValue(CacheKey(source), out ReaderArticle? cached) && cached is not null) return cached;

        string html;
        try
        {
            using var response = await httpClientFactory.CreateClient(WorldNewsArticleReader.HttpClientName).GetAsync(source, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed or HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired)
                return Remember(source, Problem(url, title, $"{source.Host} doesn't let this server read its articles. Open the original to read it there."));
            if (!response.IsSuccessStatusCode)
                return Problem(url, title, $"{source.Host} answered {(int)response.StatusCode}. Open the original to read it there.");
            if (response.Content.Headers.ContentType?.MediaType is { } type && !type.Contains("html", StringComparison.OrdinalIgnoreCase))
                return Problem(url, title, "That link isn't a web page the reader can show.");
            html = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HostPausedException ex)
        {
            return Problem(url, title, ex.Message + " Open the original to read it there.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "The reader couldn't fetch {Url}", url);
            return Problem(url, title, $"{source.Host} didn't answer. Open the original to read it there.");
        }

        return Remember(source, Read(source, html, title));
    }

    // The readable part of the page, as safe HTML (public for tests: no network).
    public static ReaderArticle Read(Uri source, string html, string fallbackTitle)
    {
        Article? article = null;
        try { article = new Reader(source.AbsoluteUri, html).GetArticle(); }
        catch (Exception) { /* not readable: reported below */ }
        var title = string.IsNullOrWhiteSpace(article?.Title) ? fallbackTitle : article!.Title.Trim();
        if (article is not { IsReadable: true } || string.IsNullOrWhiteSpace(article.Content) || (article.TextContent?.Trim().Length ?? 0) < MinReadableChars)
            return Problem(source.AbsoluteUri, title, "This page has no article text the reader can show (it may be behind a paywall or built by scripts). Open the original to read it there.");

        var markdown = WebClipMarkdownConverter.Convert(article.Content, source);
        var body = ArticleMarkdownRenderer.RenderPreviewHtml(markdown, []);
        var words = article.TextContent!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return new ReaderArticle(source.AbsoluteUri, title,
            string.IsNullOrWhiteSpace(article.Byline) ? null : article.Byline.Trim(),
            string.IsNullOrWhiteSpace(article.SiteName) ? source.Host : article.SiteName.Trim(),
            article.PublicationDate is { } published ? new DateTimeOffset(published) : null,
            body, Math.Max(1, (int)Math.Round(words / 230.0)), null, markdown, article.TextContent!.Trim());
    }

    // 5. Why it matters (20-11-26): three takeaways and one line for GWS, from the article's own
    // text. Public articles, so with server AI off it may run on an Admin's Mac (shared work).
    public const int TakeawayTextChars = 6000;
    private static readonly TimeSpan TakeawayTimeout = TimeSpan.FromMinutes(2);

    public async Task<ReaderTakeaways> TakeawaysAsync(string url, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue("article-takeaways:" + url, out ReaderTakeaways? cached) && cached is not null) return cached;
        if (ollama is null || studioOptions is null) return new([], null, "The AI model isn't set up on this server.");
        var article = await ReadAsync(url, null, cancellationToken);
        if (!article.Readable) return new([], null, article.Problem);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TakeawayTimeout);
            using var sharedWork = MacRelayScope.ForSharedWork();
            var text = article.Text.Length > TakeawayTextChars ? article.Text[..TakeawayTextChars] : article.Text;
            var raw = await ollama.GenerateAsync(studioOptions.Value.Model, TakeawaysPrompt,
                $"Title: {article.Title}\n\n{text}", timeout.Token);
            var parsed = ParseTakeaways(raw);
            if (parsed.Points.Count > 0) cache.Set("article-takeaways:" + url, parsed, TimeSpan.FromHours(12));
            return parsed;
        }
        catch (ServerAiUnavailableException ex)
        {
            return new([], null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reader takeaways failed for {Url}", url);
            return new([], null, "The AI model didn't answer just now. Try again in a minute.");
        }
    }

    public const string TakeawaysPrompt =
        "You help a software developer decide what to take from a technical article. The developer builds GWS, " +
        "a .NET 10 Blazor Server business suite with a MAUI Mac app, SQLite and EF Core, local AI through Ollama, " +
        "maps (Cesium, MapKit) and news and intelligence feeds. Reply in exactly this format and nothing else:\n" +
        "- first takeaway\n- second takeaway\n- third takeaway\nFor GWS: one sentence on whether and how it applies to GWS (say plainly if it doesn't).\n" +
        "Each takeaway is one concrete sentence under 25 words, from the article itself.";

    public static ReaderTakeaways ParseTakeaways(string? raw)
    {
        static string Clean(string line) => line.Replace("**", string.Empty).TrimStart('-', '*', '•', ' ').Trim();
        static bool IsForGws(string line) => Clean(line).StartsWith("For GWS", StringComparison.OrdinalIgnoreCase);
        var lines = (raw ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var points = lines.Where(line => !IsForGws(line) && line.Length > 2 && (line[0] is '-' or '*' or '•' || (char.IsDigit(line[0]) && line[1] is '.' or ')')))
            .Select(line => Clean(line).TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ').Trim())
            .Where(line => line.Length > 0).Take(3).ToList();
        var forGws = lines.Where(IsForGws).Select(line => Clean(line)["For GWS".Length..].TrimStart(':', ' ', '-').Trim()).FirstOrDefault();
        return new(points, string.IsNullOrWhiteSpace(forGws) ? null : forGws,
            points.Count == 0 ? "The AI model's answer couldn't be read. Try again." : null);
    }

    private static ReaderArticle Problem(string url, string title, string why) => new(url, title, null, null, null, string.Empty, 0, why);

    private ReaderArticle Remember(Uri source, ReaderArticle article)
    {
        cache.Set(CacheKey(source), article, CacheFor);
        return article;
    }

    private static string CacheKey(Uri source) => "article-reader:" + source.AbsoluteUri;
}
