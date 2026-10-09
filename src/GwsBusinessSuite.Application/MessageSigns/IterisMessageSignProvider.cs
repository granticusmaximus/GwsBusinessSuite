using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.MessageSigns;

// Message signs from an Iteris ATIS 511 state's public map layer
// ({state}.cdn.iteris-atis.com/geojson/icons/metadata/icons.dms.geojson) - the same CDN the
// IterisAtisCameraProvider reads. Verified live 2026-10-09 for Montana (76 signs, every one with
// text in `report`, lines separated by <br>); South Dakota's equivalent answers 403.
public sealed partial class IterisMessageSignProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<IterisMessageSignProvider> logger,
    string stateCode,
    string sourceName,
    string sourceAttributionUrl,
    BoundingBox coverage) : IMessageSignProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public string SourceName => sourceName;

    public BoundingBox Coverage => coverage;

    public async Task<IReadOnlyList<MessageSign>> GetActiveSignsAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"message-signs:iteris:{stateCode}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<MessageSign>? cached) && cached is not null) return cached;

        try
        {
            var collection = await httpClient.GetFromJsonAsync<SignCollection>(
                $"https://{stateCode}.cdn.iteris-atis.com/geojson/icons/metadata/icons.dms.geojson", cancellationToken);
            var signs = new List<MessageSign>();
            foreach (var feature in collection?.Features ?? [])
            {
                if (feature.Geometry?.Coordinates is not { Count: >= 2 } coordinates || feature.Properties is not { } sign) continue;
                var lines = SignText.CleanLines(LineBreak().Split(sign.Report ?? string.Empty));
                if (lines.Count == 0) continue;

                signs.Add(new MessageSign(
                    Id: $"iteris-{stateCode}-{sign.Id}",
                    Name: string.IsNullOrWhiteSpace(sign.Name) ? $"{sourceName} sign" : sign.Name.Trim(),
                    Latitude: coordinates[1],
                    Longitude: coordinates[0],
                    Road: string.IsNullOrWhiteSpace(sign.Route) ? null : sign.Route.Trim(),
                    Direction: string.IsNullOrWhiteSpace(sign.Direction) ? null : sign.Direction.Trim(),
                    Pages: [lines],
                    SourceName: sourceName,
                    SourceAttributionUrl: sourceAttributionUrl));
            }
            if (signs.Count > 0) cache.Set(cacheKey, (IReadOnlyList<MessageSign>)signs, CacheDuration);
            return signs;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch {Source} message signs.", sourceName);
            return [];
        }
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    private sealed record SignCollection([property: JsonPropertyName("features")] List<SignFeature>? Features);

    private sealed record SignFeature(
        [property: JsonPropertyName("geometry")] SignGeometry? Geometry,
        [property: JsonPropertyName("properties")] SignProperties? Properties);

    private sealed record SignGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record SignProperties(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("report")] string? Report,
        [property: JsonPropertyName("route")] string? Route,
        [property: JsonPropertyName("direction")] string? Direction);
}
