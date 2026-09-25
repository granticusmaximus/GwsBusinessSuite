using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.ThreatIntel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// AlienVault OTX (otx.alienvault.com) - confirmed real and live directly (an unauthenticated
// request to /api/v1/pulses/subscribed returns a real 401, not a 404), requiring the free,
// self-registered API key every OTX account gets, sent via the X-OTX-API-KEY header.
//
// The exact "pulse" JSON field names below (id/name/description/author_name/created/tags/
// indicators) follow OTX's own long-published, stable public API documentation - this app has
// no live key to verify a real response against during development, so parsing is deliberately
// defensive (every field optional, missing/malformed pulses skipped rather than throwing),
// matching this codebase's existing WindyWebcamProvider precedent for a documented-but-
// unverified schema.
public sealed class OtxThreatIntelService(
    HttpClient httpClient,
    IOptions<ThreatIntelOptions> options,
    ILogger<OtxThreatIntelService> logger) : IOtxThreatIntelService
{
    public async Task<IReadOnlyList<OtxPulse>> GetPulsesAsync(string? searchTerm, CancellationToken cancellationToken = default)
    {
        var apiKey = options.Value.OtxApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        var url = string.IsNullOrWhiteSpace(searchTerm)
            ? "pulses/subscribed?limit=20"
            : $"search/pulses?q={Uri.EscapeDataString(searchTerm)}&limit=20";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-OTX-API-KEY", apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OTX request failed with {StatusCode}", response.StatusCode);
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var root = JsonNode.Parse(json)?.AsObject();
            var results = root?["results"]?.AsArray();
            if (results is null) return [];

            var pulses = new List<OtxPulse>();
            foreach (var node in results)
            {
                var pulse = TryParsePulse(node);
                if (pulse is not null) pulses.Add(pulse);
            }
            return pulses;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Failed to fetch OTX pulses");
            return [];
        }
    }

    private static OtxPulse? TryParsePulse(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        var id = GetString(obj, "id");
        var name = GetString(obj, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

        var tags = (obj["tags"] as JsonArray)?
            .Select(t => t?.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList() ?? [];

        DateTimeOffset? created = null;
        if (GetString(obj, "created") is { } createdRaw && DateTimeOffset.TryParse(createdRaw, out var parsedCreated))
        {
            created = parsedCreated;
        }

        var indicatorCount = GetInt(obj, "indicator_count")
            ?? (obj["indicators"] as JsonArray)?.Count
            ?? 0;

        return new OtxPulse(
            Id: id,
            Name: name,
            Description: GetString(obj, "description") ?? string.Empty,
            AuthorName: GetString(obj, "author_name") ?? GetString(obj["author"] as JsonObject, "username") ?? string.Empty,
            Created: created,
            Tags: tags,
            IndicatorCount: indicatorCount);
    }

    private static string? GetString(JsonObject? obj, string property) =>
        obj?[property] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static int? GetInt(JsonObject? obj, string property) =>
        obj?[property] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out int i)
            ? i
            : null;
}
