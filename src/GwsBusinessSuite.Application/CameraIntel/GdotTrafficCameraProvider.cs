using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Georgia DOT's public 511 camera feed, published as a plain ArcGIS FeatureServer layer -
// genuinely free with no API key or registration of any kind, verified directly during
// implementation (both the field names and a live snapshot fetch, not assumed from the schema
// alone - the layer's own `VideoUrl` field looked promising but its HLS URLs return
// 401 Unauthorized when fetched directly; `Url` is the one that actually works, resolving to a
// live, unauthenticated, CORS-open PNG snapshot).
public sealed class GdotTrafficCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<GdotTrafficCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:gdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "GDOT";

    public async Task<IReadOnlyList<CameraFeed>> GetCamerasAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<CameraFeed>? allCameras) || allCameras is null)
        {
            allCameras = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allCameras, CacheDuration);
        }

        // Like WSDOT, GDOT's feed has no bbox query parameter of its own (~3,800 cameras
        // statewide) - cheap to cache the whole list and filter client-side per view.
        return allCameras.Where(camera => bbox.Contains(camera.Latitude, camera.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<CameraFeed>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string url = "arcgis/rest/services/GDOT_511_Cameras/FeatureServer/0/query" +
                "?where=Status='Enabled'&outFields=Id,Latitude,Longitude,Url,Roadway&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null)
            {
                return [];
            }

            return features
                .Select(feature => feature.Attributes)
                .Where(attrs => attrs is { Latitude: not 0, Longitude: not 0 } && !string.IsNullOrWhiteSpace(attrs.Url))
                .Select(attrs => new CameraFeed(
                    Id: $"gdot-{attrs.Id}",
                    Name: string.IsNullOrWhiteSpace(attrs.Roadway) ? $"GDOT Camera {attrs.Id}" : $"GDOT: {attrs.Roadway}",
                    Latitude: attrs.Latitude,
                    Longitude: attrs.Longitude,
                    StreamUrl: attrs.Url!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://511ga.org"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch GDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection(List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(GdotCameraAttributes Attributes);

    private sealed record GdotCameraAttributes(string Id, double Latitude, double Longitude, string? Url, string? Roadway);
}
