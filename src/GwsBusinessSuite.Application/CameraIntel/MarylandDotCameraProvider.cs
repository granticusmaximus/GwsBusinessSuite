using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// MDOT SHA CHART traffic cameras via Maryland iMap - genuinely free with no API key, confirmed
// directly (a real camera's url resolved live). An earlier research pass in this project flagged
// Maryland as a dead end; re-verifying with a fresh search found this real ArcGIS layer, a
// reminder that "confirmed dead end" findings can go stale and are worth re-checking. The `url`
// field already contains the complete, ready-to-use chart.maryland.gov video link - no URL
// construction needed on this end.
public sealed class MarylandDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<MarylandDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:mdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "MDOT SHA";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allCameras, CacheDuration);
        }

        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string url = "imap/rest/services/Transportation/MD_TrafficCameras/FeatureServer/0/query" +
                "?where=1=1&outFields=OBJECTID,location,county,url&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.Url))
                .Select(f => new CameraFeed(
                    Id: $"mdot-{f.Attributes.ObjectId}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.Location) ? $"MDOT SHA Camera {f.Attributes.ObjectId}" : $"MDOT SHA: {f.Attributes.Location}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.Url!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://chart.maryland.gov"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch MDOT SHA traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] MdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record MdotAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("location")] string? Location,
        [property: JsonPropertyName("url")] string? Url);
}
