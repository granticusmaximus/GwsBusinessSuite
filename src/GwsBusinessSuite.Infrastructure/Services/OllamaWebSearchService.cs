using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class OllamaWebSearchService(
    HttpClient http,
    IOptions<OllamaWebOptions> options) : IOllamaWebSearchService
{
    private readonly OllamaWebOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<IReadOnlyList<OllamaWebSearchResult>> SearchAsync(
        string query,
        int? maxResults = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "SentinelGPT internet access is not configured. Set OllamaWeb:ApiKey in user secrets or the OllamaWeb__ApiKey environment variable.");
        }

        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length is < 2 or > 500)
        {
            throw new ArgumentException("A web search query must be between 2 and 500 characters.", nameof(query));
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            "/api/web_search",
            new
            {
                query = normalizedQuery,
                max_results = Math.Clamp(maxResults ?? _options.MaxResults, 1, 10)
            });
        using var response = await http.SendAsync(request, ct);
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
            throw new InvalidOperationException(
                "SentinelGPT internet access is not configured. Set OllamaWeb:ApiKey in user secrets or the OllamaWeb__ApiKey environment variable.");
        }

        if (!IsSafePublicHttpsUrl(url))
        {
            throw new ArgumentException("Only public HTTPS URLs can be fetched.", nameof(url));
        }

        using var request = CreateRequest(HttpMethod.Post, "/api/web_fetch", new { url });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<WebFetchResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("The web fetch service returned an empty response.");
        return new OllamaWebSearchResult(
            Limit(result.Title, 240),
            url,
            Limit(result.Content, 12_000));
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

    private static bool IsSafePublicHttpsUrl(string? value)
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

    private sealed record WebSearchResponse(
        [property: JsonPropertyName("results")] WebSearchItem[]? Results);

    private sealed record WebSearchItem(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("content")] string Content);

    private sealed record WebFetchResponse(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("content")] string Content);
}
