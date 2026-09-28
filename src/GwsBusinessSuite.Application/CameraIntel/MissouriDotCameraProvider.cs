using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// MoDOT's statewide traffic camera layer via their own ArcGIS server - genuinely free with no API
// key, confirmed directly and confirmed genuinely statewide (not just the Springfield-area subset
// an earlier research pass in this project found - that was a real but incomplete finding, this
// supersedes it). URL2 is the field actually populated with a live HLS (.m3u8) stream in every
// sampled record; URL1 was consistently null - CameraStreamKind.Hls is used since no separate
// static-image field exists on this layer at all (same situation as Delaware's own M3U8S-only
// layer). STREAM_ERROR ("N"/"Y") flags a currently-broken feed - filtered out here rather than
// surfaced as a dead camera pin.
public sealed class MissouriDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<MissouriDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:modot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "MoDOT";

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
            const string url = "arcgis/rest/services/TravelerInformation/NWSDATA/MapServer/0/query" +
                "?where=1=1&outFields=CAM_ID,DESCRIPTION,URL2,STREAM_ERROR&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null
                    && !string.IsNullOrWhiteSpace(f.Attributes.Url2)
                    && !string.Equals(f.Attributes.StreamError, "Y", StringComparison.OrdinalIgnoreCase))
                .Select(f => new CameraFeed(
                    Id: $"modot-{f.Attributes.CamId}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.Description) ? $"MoDOT Camera {f.Attributes.CamId}" : $"MoDOT: {f.Attributes.Description}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.Url2!,
                    StreamKind: CameraStreamKind.Hls,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.modot.org"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch MoDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] ModotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record ModotAttributes(
        [property: JsonPropertyName("CAM_ID")] long CamId,
        [property: JsonPropertyName("DESCRIPTION")] string? Description,
        [property: JsonPropertyName("URL2")] string? Url2,
        [property: JsonPropertyName("STREAM_ERROR")] string? StreamError);
}
