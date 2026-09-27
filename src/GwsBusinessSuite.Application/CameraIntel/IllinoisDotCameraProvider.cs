using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// IDOT/Gateway's statewide traffic camera layer, published as a plain ArcGIS FeatureServer -
// genuinely free with no API key, confirmed directly. Real, non-obvious trap found during
// implementation: this layer's own `ImgPath` field looks like the image URL but actually links
// to a travelmidwest.com HTML *viewer page*, not an image - `SnapShot` is the field that
// actually resolves to a live image (confirmed via a direct fetch, HTTP 200 image/jpeg).
// ~3,670 cameras exceeds this layer's 1,000-per-request maxRecordCount, so results are paged via
// resultOffset. outSR=4326 requested explicitly on every query, same rationale as the other
// ArcGIS-based providers in this file.
public sealed class IllinoisDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<IllinoisDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:idot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const int PageSize = 1000;

    public string SourceName => "IDOT";

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
            var results = new List<CameraFeed>();
            var offset = 0;
            while (true)
            {
                var url = "arcgis/rest/services/TrafficCamerasTM_Public/FeatureServer/0/query" +
                    $"?where=1=1&outFields=OBJECTID,CameraLocation,SnapShot&outSR=4326&resultOffset={offset}&resultRecordCount={PageSize}&f=json";
                var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
                var features = response?.Features;
                if (features is null || features.Count == 0) break;

                foreach (var feature in features)
                {
                    var attrs = feature.Attributes;
                    var geometry = feature.Geometry;
                    if (geometry is null || string.IsNullOrWhiteSpace(attrs.SnapShot)) continue;

                    results.Add(new CameraFeed(
                        Id: $"idot-{attrs.ObjectId}",
                        Name: string.IsNullOrWhiteSpace(attrs.CameraLocation) ? $"IDOT Camera {attrs.ObjectId}" : $"IDOT: {attrs.CameraLocation}",
                        Latitude: geometry.Y,
                        Longitude: geometry.X,
                        StreamUrl: attrs.SnapShot!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://www.travelmidwest.com"));
                }

                if (features.Count < PageSize) break;
                offset += PageSize;
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch IDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] IdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record IdotAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("CameraLocation")] string? CameraLocation,
        [property: JsonPropertyName("SnapShot")] string? SnapShot);
}
