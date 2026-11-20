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
    IOptions<ContentStudioOptions>? studioOptions = null,
    IWebCrawlService? crawl = null) : IArticleReader
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
                return await OrKeptAsync(source, Problem(url, title, $"{source.Host} answered {(int)response.StatusCode}. Open the original to read it there."), cancellationToken);
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
            return await OrKeptAsync(source, Problem(url, title, $"{source.Host} didn't answer. Open the original to read it there."), cancellationToken);
        }

        return Remember(source, await OrKeptAsync(source, Read(source, html, title), cancellationToken));
    }

    // When the page can't be shown (built by scripts, down, an error), the text Media Watch's
    // crawler already kept for that address is shown instead (20-11-26). A site that refuses the
    // server never reaches here: that answer is respected as it is.
    private async Task<ReaderArticle> OrKeptAsync(Uri source, ReaderArticle article, CancellationToken ct)
    {
        if (article.Readable || crawl is null) return article;
        try
        {
            var kept = await crawl.GetKeptPageAsync(source.AbsoluteUri, ct);
            return kept is null ? article : FromKept(source, kept, article.Title);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "No kept text for {Url}", source);
            return article;
        }
    }

    // Plain kept text as paragraphs (public for tests). Markdown with raw HTML off, as Read.
    public static ReaderArticle FromKept(Uri source, KeptPage kept, string fallbackTitle)
    {
        var paragraphs = kept.Text.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .Select(p => System.Text.RegularExpressions.Regex.Replace(p, @"([\\`*_\[\]#<>|])", @"\$1"));
        var markdown = string.Join("\n\n", paragraphs);
        var words = kept.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var note = "<p class=\"gws-reader-kept\">Shown from the copy Media Watch's crawler kept, because the page itself couldn't be shown here.</p>";
        return new ReaderArticle(source.AbsoluteUri, string.IsNullOrWhiteSpace(kept.Title) ? fallbackTitle : kept.Title.Trim(), null, source.Host,
            kept.PublishedAt, note + ArticleMarkdownRenderer.RenderPreviewHtml(markdown, []), Math.Max(1, (int)Math.Round(words / 230.0)), null, markdown, kept.Text.Trim());
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

    // Read a report translated (20-11-26): the readable article's Markdown, a few thousand
    // characters at a time, into English by the model, then laid out as any reader article (raw
    // HTML off). Public reports, so it's shared work when server AI is off.
    public const int TranslateChunkChars = 5_000;
    public const int MaxTranslateChars = 20_000;
    private static readonly TimeSpan TranslateTimeout = TimeSpan.FromMinutes(4);

    public const string TranslatePrompt =
        "Translate the user's Markdown news article into natural English. Keep the Markdown structure (headings, " +
        "paragraphs, lists, links, image lines) exactly, translate the text only, and add nothing: no notes, no intro. " +
        "If part is already English, keep it as it is.";

    public async Task<ReaderArticle> TranslateAsync(string url, string? fallbackTitle = null, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue("article-translated:" + url, out ReaderArticle? cached) && cached is not null) return cached;
        var article = await ReadAsync(url, fallbackTitle, cancellationToken);
        if (!article.Readable) return article;
        if (ollama is null || studioOptions is null) return article with { Html = string.Empty, Problem = "The AI model isn't set up on this server." };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TranslateTimeout);
            using var sharedWork = MacRelayScope.ForSharedWork();
            var source = article.Markdown.Length > MaxTranslateChars ? article.Markdown[..MaxTranslateChars] : article.Markdown;
            var parts = new List<string>();
            foreach (var chunk in Chunks(source, TranslateChunkChars))
                parts.Add(StripThinking(await ollama.GenerateAsync(studioOptions.Value.Model, TranslatePrompt, chunk, timeout.Token)).Trim());
            var title = StripThinking(await ollama.GenerateAsync(studioOptions.Value.Model,
                "Translate this news headline into English. Reply with the headline only.", article.Title, timeout.Token)).Trim().Trim('"');
            var markdown = string.Join("\n\n", parts);
            if (article.Markdown.Length > MaxTranslateChars) markdown += "\n\n*The rest of the article wasn't translated; open the original to read it all.*";
            var translated = article with
            {
                Title = string.IsNullOrWhiteSpace(title) || title.Length > 300 ? article.Title : title,
                Html = ArticleMarkdownRenderer.RenderPreviewHtml(markdown, []),
                Markdown = markdown,
                Text = markdown,
            };
            cache.Set("article-translated:" + url, translated, TimeSpan.FromHours(12));
            return translated;
        }
        catch (ServerAiUnavailableException ex)
        {
            return article with { Html = string.Empty, Problem = ex.Message };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reader translation failed for {Url}", url);
            return article with { Html = string.Empty, Problem = "The AI model didn't finish the translation just now. Try again in a minute." };
        }
    }

    // Ask the article (20-11-26): the model answers from the article's text only and quotes the
    // passage; a quote that isn't really in the article is dropped rather than shown.
    public const int AskTextChars = 12_000;

    public const string AskPrompt =
        "Answer the reader's question using only the article below. Reply in exactly this format:\n" +
        "ANSWER: one to three plain sentences. If the article doesn't say, write \"The article doesn't say.\"\n" +
        "QUOTE: the one sentence from the article that best supports the answer, copied exactly, or NONE.";

    public async Task<ReaderAnswer> AskAsync(string url, string question, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question)) return new(string.Empty, null, "Type a question first.");
        if (ollama is null || studioOptions is null) return new(string.Empty, null, "The AI model isn't set up on this server.");
        var article = await ReadAsync(url, null, cancellationToken);
        if (!article.Readable) return new(string.Empty, null, article.Problem);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TakeawayTimeout);
            using var sharedWork = MacRelayScope.ForSharedWork();
            var text = article.Text.Length > AskTextChars ? article.Text[..AskTextChars] : article.Text;
            var q = question.Trim();
            var raw = await ollama.GenerateAsync(studioOptions.Value.Model, AskPrompt,
                $"Question: {(q.Length > 300 ? q[..300] : q)}\n\nArticle: {article.Title}\n\n{text}", timeout.Token);
            return ParseAnswer(raw, article.Text);
        }
        catch (ServerAiUnavailableException ex)
        {
            return new(string.Empty, null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Reader question failed for {Url}", url);
            return new(string.Empty, null, "The AI model didn't answer just now. Try again in a minute.");
        }
    }

    public static ReaderAnswer ParseAnswer(string? raw, string articleText)
    {
        var text = StripThinking(raw ?? string.Empty).Replace("**", string.Empty);
        var answerAt = text.IndexOf("ANSWER:", StringComparison.OrdinalIgnoreCase);
        var quoteAt = text.IndexOf("QUOTE:", StringComparison.OrdinalIgnoreCase);
        var answer = (answerAt < 0 ? (quoteAt < 0 ? text : text[..quoteAt])
            : quoteAt > answerAt ? text[(answerAt + 7)..quoteAt] : text[(answerAt + 7)..]).Trim();
        var quote = quoteAt < 0 ? null : text[(quoteAt + 6)..].Trim().Trim('"', '“', '”').Trim();
        if (quote is not null && (quote.Equals("NONE", StringComparison.OrdinalIgnoreCase) || quote.Length < 12
            || !Squash(articleText).Contains(Squash(quote), StringComparison.OrdinalIgnoreCase)))
            quote = null;
        return answer.Length == 0 ? new(string.Empty, null, "The AI model's answer couldn't be read. Try again.") : new(answer, quote, null);
    }

    private static string Squash(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    // Paragraph-sized pieces of at most max characters (a longer paragraph is cut on its own).
    public static IReadOnlyList<string> Chunks(string markdown, int max)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var paragraph in markdown.Split("\n\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > max)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            var rest = paragraph;
            while (rest.Length > max)
            {
                chunks.Add(rest[..max]);
                rest = rest[max..];
            }
            if (current.Length > 0) current.Append("\n\n");
            current.Append(rest);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private static string StripThinking(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, "<think>.*?</think>", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

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
