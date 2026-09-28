using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// TxDOT's statewide traffic camera layer via ArcGIS - genuinely free with no API key, confirmed
// directly (a real windy.com-hosted snapshot resolved live). A real statewide upgrade over the
// Datumfeed-routed Austin-only coverage this app already had. Real gotcha, confirmed via a live
// sample: `Camera_ID` is NOT unique across records (multiple distinct cameras share the same
// Camera_ID value) - OBJECTID is the field that's actually unique, and is what this provider
// keys its Id on. ~2,825 total records but only ~892 carry a populated `url` - filtered
// server-side via `where=url<>''` so pagination only has to walk the smaller, relevant subset.
public sealed class TexasDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<TexasDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:txdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const int PageSize = 1000;

    public string SourceName => "TxDOT";

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
                var url = "arcgis/rest/services/TxDoT_Cameras/FeatureServer/0/query" +
                    $"?where=url%3C%3E''&outFields=OBJECTID,Equipment_Name,url,status&outSR=4326&resultOffset={offset}&resultRecordCount={PageSize}&f=json";
                var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
                var features = response?.Features;
                if (features is null || features.Count == 0) break;

                foreach (var feature in features)
                {
                    var attrs = feature.Attributes;
                    var geometry = feature.Geometry;
                    if (geometry is null || string.IsNullOrWhiteSpace(attrs.Url)) continue;
                    if (!string.Equals(attrs.Status, "Active", StringComparison.OrdinalIgnoreCase)) continue;

                    results.Add(new CameraFeed(
                        Id: $"txdot-{attrs.ObjectId}",
                        Name: string.IsNullOrWhiteSpace(attrs.EquipmentName) ? $"TxDOT Camera {attrs.ObjectId}" : $"TxDOT: {attrs.EquipmentName}",
                        Latitude: geometry.Y,
                        Longitude: geometry.X,
                        StreamUrl: attrs.Url!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://drivetexas.org"));
                }

                if (features.Count < PageSize) break;
                offset += PageSize;
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch TxDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] TxdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record TxdotAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("Equipment_Name")] string? EquipmentName,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("status")] string? Status);
}
