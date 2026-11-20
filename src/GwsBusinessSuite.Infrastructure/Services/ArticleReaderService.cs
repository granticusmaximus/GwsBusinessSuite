using System.Net;
using GwsBusinessSuite.Application.Articles;
using GwsBusinessSuite.Application.NewsIntelligence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SmartReader;

namespace GwsBusinessSuite.Infrastructure.Services;

// Media Watch's reader (20-11-26): fetches the article once (the World News article client: public
// addresses only, an honest User-Agent, and a site that refuses this server is left alone for a
// day), keeps the readable part (SmartReader, Firefox's Reader View algorithm), and renders it from
// Markdown with raw HTML off, so nothing of the outlet's markup or scripts reaches the page.
public sealed class ArticleReaderService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<ArticleReaderService> logger) : IArticleReader
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
            body, Math.Max(1, (int)Math.Round(words / 230.0)), null);
    }

    private static ReaderArticle Problem(string url, string title, string why) => new(url, title, null, null, null, string.Empty, 0, why);

    private ReaderArticle Remember(Uri source, ReaderArticle article)
    {
        cache.Set(CacheKey(source), article, CacheFor);
        return article;
    }

    private static string CacheKey(Uri source) => "article-reader:" + source.AbsoluteUri;
}
