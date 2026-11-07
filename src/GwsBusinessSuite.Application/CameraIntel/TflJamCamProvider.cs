using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Transport for London's JamCams through TfL's open Unified API (key optional; open-data terms ask
// for "Powered by TfL Open Data" attribution, which SourceAttributionUrl links). Verified live
// 07-11-26: 889 London cameras, each a still JPEG (and a short MP4 clip) on TfL's own S3 bucket,
// refreshed every few minutes. "available" is false for cameras TfL has switched off.
public sealed class TflJamCamProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<TflJamCamProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:tfl:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    public string SourceName => "TfL";

    // Greater London, with a margin.
    public BoundingBox? Coverage { get; } = new(51.75, 51.25, 0.4, -0.6);

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            if (allCameras.Count > 0) cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var places = await httpClient.GetFromJsonAsync<List<TflPlace>>("Place/Type/JamCam", cancellationToken);
            if (places is null) return [];

            return places
                .Select(place => (place, image: Property(place, "imageUrl"), available: Property(place, "available")))
                .Where(x => x.place.Latitude is >= 51 and <= 52 && x.place.Longitude is >= -1 and <= 1
                            && !string.Equals(x.available, "false", StringComparison.OrdinalIgnoreCase)
                            && Uri.TryCreate(x.image, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                .GroupBy(x => x.image)
                .Select(g => g.First())
                .Select(x => new CameraFeed(
                    Id: $"tfl-{x.place.Id.Replace("JamCams_", string.Empty, StringComparison.Ordinal)}",
                    Name: $"London: {x.place.CommonName}" + (Property(x.place, "view") is { Length: > 0 } view ? $" ({view})" : string.Empty),
                    Latitude: x.place.Latitude!.Value,
                    Longitude: x.place.Longitude!.Value,
                    StreamUrl: x.image!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://tfl.gov.uk/info-for/open-data-users/"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch TfL JamCams.");
            return [];
        }
    }

    private static string? Property(TflPlace place, string key) =>
        place.AdditionalProperties?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private sealed record TflPlace(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("commonName")] string? CommonName,
        [property: JsonPropertyName("lat")] double? Latitude,
        [property: JsonPropertyName("lon")] double? Longitude,
        [property: JsonPropertyName("additionalProperties")] List<TflProperty>? AdditionalProperties);

    private sealed record TflProperty(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("value")] string? Value);
}
