using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// SentinelGPT's and World News' web search and page fetch. It searches a self-hosted SearXNG
// (OllamaWeb:SearxngUrl, key-less, the default since 13-11-26) and fetches
// pages itself through the "web-fetch" client, whose ConnectCallback refuses private addresses on every
// connection (redirects and DNS rebinding included).
public sealed class OllamaWebSearchService(
    HttpClient http,
    IOptions<OllamaWebOptions> options,
    IHttpClientFactory? clients = null) : IOllamaWebSearchService
{
    public const string FetchClientName = "web-fetch";
    private const int MaxPageBytes = 3_000_000;
    private const string NotConfigured =
        "SentinelGPT internet access is not configured. Set OllamaWeb__SearxngUrl (key-less) or OllamaWeb__ApiKey.";

    private readonly OllamaWebOptions _options = options.Value;

    // Ollama's billed web API, only when OllamaWeb:AllowPaidSearch is true (19-11-26): a key left in
    // .env costs nothing.
    private bool UsesOllama => _options.AllowPaidSearch && !string.IsNullOrWhiteSpace(_options.ApiKey);

    private bool HasSearxng => !string.IsNullOrWhiteSpace(_options.SearxngUrl);

    // SearXNG is used without a key, and in place of Ollama while Ollama is refusing this key.
    private bool UsesSearxng => HasSearxng && (!UsesOllama || OllamaPaused);

    public bool IsConfigured => UsesOllama || HasSearxng;

    // 19-11-26: production's Ollama web search answered 402 (out of credit) to every search, every
    // five minutes, so World News discovery found nothing although SearXNG was set up. A refusal of
    // the key now hands searches and fetches to SearXNG for a while. Keyed by the API key, so a new
    // key is tried straight away.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> OllamaPausedUntil = new();

    private bool OllamaPaused =>
        OllamaPausedUntil.TryGetValue(_options.ApiKey, out var until) && DateTimeOffset.UtcNow < until;

    public static TimeSpan? PauseAfter(System.Net.HttpStatusCode status) => status switch
    {
        System.Net.HttpStatusCode.TooManyRequests => TimeSpan.FromMinutes(15),
        System.Net.HttpStatusCode.PaymentRequired or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden => TimeSpan.FromHours(6),
        _ => null,
    };

    // True when Ollama refused the key and SearXNG can answer instead.
    private bool HandOverToSearxng(HttpResponseMessage response)
    {
        if (!HasSearxng || PauseAfter(response.StatusCode) is not { } pause) return false;
        OllamaPausedUntil[_options.ApiKey] = DateTimeOffset.UtcNow + pause;
        return true;
    }

    public Task<IReadOnlyList<OllamaWebSearchResult>> SearchAsync(
        string query,
        int? maxResults = null,
        CancellationToken ct = default) =>
        SearchCoreAsync(query, maxResults, recent: false, ct);

    public Task<IReadOnlyList<OllamaWebSearchResult>> SearchRecentAsync(
        string query,
        int? maxResults = null,
        CancellationToken ct = default) =>
        SearchCoreAsync(query, maxResults, recent: true, ct);

    private async Task<IReadOnlyList<OllamaWebSearchResult>> SearchCoreAsync(
        string query,
        int? maxResults,
        bool recent,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(NotConfigured);
        }

        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length is < 2 or > 500)
        {
            throw new ArgumentException("A web search query must be between 2 and 500 characters.", nameof(query));
        }

        var resultLimit = Math.Clamp(maxResults ?? _options.MaxResults, 1, 10);
        if (UsesSearxng)
            return await SearchSearxngAsync(normalizedQuery, resultLimit, recent ? "day" : null, ct);

        using var request = CreateRequest(
            HttpMethod.Post,
            "/api/web_search",
            new
            {
                query = recent ? $"{normalizedQuery} within the past 24 hours" : normalizedQuery,
                max_results = resultLimit
            });
        using var response = await http.SendAsync(request, ct);
        if (HandOverToSearxng(response))
            return await SearchSearxngAsync(normalizedQuery, resultLimit, recent ? "day" : null, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<WebSearchResponse>(cancellationToken: ct);
        return result?.Results?
            .Where(item => IsSafePublicHttpsUrl(item.Url))
            .Select(item => new OllamaWebSearchResult(
                Limit(item.Title, 240),
                item.Url,
                Limit(item.Content, 4_000)))
            .ToList()
            ?? [];
    }

    public async Task<OllamaWebSearchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(NotConfigured);
        }

        if (!IsSafePublicHttpsUrl(url))
        {
            throw new ArgumentException("Only public HTTPS URLs can be fetched.", nameof(url));
        }

        if (UsesSearxng) return await FetchDirectAsync(url, ct);

        using var request = CreateRequest(HttpMethod.Post, "/api/web_fetch", new { url });
        using var response = await http.SendAsync(request, ct);
        if (HandOverToSearxng(response)) return await FetchDirectAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<WebFetchResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("The web fetch service returned an empty response.");
        return new OllamaWebSearchResult(
            Limit(result.Title, 240),
            url,
            Limit(result.Content, 12_000));
    }

    private async Task<IReadOnlyList<OllamaWebSearchResult>> SearchSearxngAsync(
        string query,
        int maxResults,
        string? timeRange,
        CancellationToken ct)
    {
        var range = timeRange is null ? string.Empty : $"&time_range={Uri.EscapeDataString(timeRange)}";
        var url = $"{_options.SearxngUrl.TrimEnd('/')}/search?format=json&safesearch=1{range}&q={Uri.EscapeDataString(query)}";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<WebSearchResponse>(cancellationToken: ct);
        return SearxngResults(result?.Results, maxResults);
    }

    // SearXNG's JSON uses the same title/url/content fields; only public HTTPS results are kept.
    public static IReadOnlyList<OllamaWebSearchResult> SearxngResults(IEnumerable<WebSearchItem>? items, int maxResults) =>
        (items ?? [])
            .Where(item => IsSafePublicHttpsUrl(item.Url))
            .DistinctBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(item => new OllamaWebSearchResult(Limit(item.Title, 240), item.Url, Limit(item.Content, 4_000)))
            .ToList();

    private async Task<OllamaWebSearchResult> FetchDirectAsync(string url, CancellationToken ct)
    {
        var client = clients?.CreateClient(FetchClientName) ?? throw new InvalidOperationException(NotConfigured);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is 403 or 405 or 429)
            throw new InvalidOperationException($"{new Uri(url).Host} refused this server ({(int)response.StatusCode}); it isn't asked again.");
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"That page isn't text ({type}).");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxPageBytes];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
        var html = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        var (title, text) = PageText(html);
        return new OllamaWebSearchResult(Limit(title, 240), url, Limit(text, 12_000));
    }

    // The readable text of a page: title, then the body without scripts, styles and site chrome.
    public static (string Title, string Text) PageText(string html)
    {
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);
        var title = document.Title?.Trim() ?? string.Empty;
        var root = (AngleSharp.Dom.IElement?)document.QuerySelector("article") ?? document.QuerySelector("main") ?? document.Body;
        if (root is null) return (title, string.Empty);
        foreach (var junk in root.QuerySelectorAll("script, style, noscript, nav, footer, header, aside, form, svg, iframe").ToList()) junk.Remove();
        foreach (var block in root.QuerySelectorAll("p, div, li, h1, h2, h3, h4, tr, br")) block.Append(document.CreateTextNode("\n"));
        var text = System.Text.RegularExpressions.Regex.Replace(root.TextContent, @"[ \t\u00a0]+", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\n\s*(\n\s*)+", "\n\n").Trim();
        return (title, text);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object payload)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        return request;
    }

    public static bool IsSafePublicHttpsUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || uri.IsLoopback)
        {
            return false;
        }

        var host = uri.IdnHost;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !System.Net.IPAddress.TryParse(host, out var address) || IsPublicAddress(address);
    }

    private static bool IsPublicAddress(System.Net.IPAddress address)
    {
        if (System.Net.IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] != 10
                && !(bytes[0] == 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 0)
                && !(bytes[0] >= 224);
        }

        return !address.IsIPv6LinkLocal
            && !address.IsIPv6Multicast
            && !address.Equals(System.Net.IPAddress.IPv6Any)
            && (bytes[0] & 0xfe) != 0xfc;
    }

    private static string Limit(string? value, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    public sealed record WebSearchResponse(
        [property: JsonPropertyName("results")] WebSearchItem[]? Results);

    public sealed record WebSearchItem(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("content")] string Content);

    private sealed record WebFetchResponse(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("content")] string Content);
}
