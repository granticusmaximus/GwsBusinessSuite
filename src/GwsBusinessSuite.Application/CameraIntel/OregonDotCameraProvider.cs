using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// ODOT/TripCheck's statewide traffic camera layer, published as a plain ArcGIS FeatureServer -
// genuinely free with no API key, confirmed directly (a real camera's attributes_filename field
// resolved live, HTTP 200 image/jpeg). This is a separate, keyless path from TripCheck's own
// paid/registered Data API - the ArcGIS FeatureServer route bypasses that entirely. Note this
// layer's own `geometry` is Web Mercator (EPSG:3857) natively, which is why outSR=4326 is
// requested explicitly here (confirmed directly: without it, geometry.x/y are Mercator meters,
// not degrees - with it, they match this layer's own attributes_latitude/attributes_longitude
// fields to 5 decimal places). ~1,190 cameras exceeds this layer's 1,000-per-request
// maxRecordCount, so results are paged via resultOffset.
public sealed class OregonDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<OregonDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:odot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const int PageSize = 1000;

    public string SourceName => "ODOT";

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
                var url = "arcgis/rest/services/TripCheck_Cameras/FeatureServer/0/query" +
                    $"?where=1=1&outFields=ObjectId,attributes_title,attributes_filename&outSR=4326&resultOffset={offset}&resultRecordCount={PageSize}&f=json";
                var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
                var features = response?.Features;
                if (features is null || features.Count == 0) break;

                foreach (var feature in features)
                {
                    var attrs = feature.Attributes;
                    var geometry = feature.Geometry;
                    if (geometry is null || string.IsNullOrWhiteSpace(attrs.Filename)) continue;

                    results.Add(new CameraFeed(
                        Id: $"odot-{attrs.ObjectId}",
                        Name: string.IsNullOrWhiteSpace(attrs.Title) ? $"ODOT Camera {attrs.ObjectId}" : $"ODOT: {attrs.Title.Trim()}",
                        Latitude: geometry.Y,
                        Longitude: geometry.X,
                        StreamUrl: attrs.Filename!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://tripcheck.com"));
                }

                if (features.Count < PageSize) break;
                offset += PageSize;
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch ODOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] OdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record OdotAttributes(
        [property: JsonPropertyName("ObjectId")] long ObjectId,
        [property: JsonPropertyName("attributes_title")] string? Title,
        [property: JsonPropertyName("attributes_filename")] string? Filename);
}
