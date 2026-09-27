using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// FL511's statewide traffic camera layer, published as a plain ArcGIS FeatureServer - genuinely
// free with no API key, confirmed directly (a real camera's IMAGE field resolved live, HTTP 200
// image/jpeg). ~4,000 cameras exceeds this layer's own 2,000-per-request maxRecordCount, so
// results are paged via resultOffset until a page comes back short. Every query passes
// outSR=4326 so geometry is always plain WGS84 degrees regardless of a layer's native spatial
// reference (this layer's own IS already 4326, but requesting it explicitly is free and keeps
// every ArcGIS-based provider in this file using one consistent, always-correct approach rather
// than needing to special-case which sources publish a matching lat/lon attribute pair).
public sealed class FloridaDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<FloridaDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:fldot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const int PageSize = 2000;

    public string SourceName => "FL511";

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
                var url = "arcgis/rest/services/FL511_Traffic_Cameras/FeatureServer/0/query" +
                    $"?where=1=1&outFields=ID,DESCRIPT,HIGHWAY,IMAGE&outSR=4326&resultOffset={offset}&resultRecordCount={PageSize}&f=json";
                var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
                var features = response?.Features;
                if (features is null || features.Count == 0) break;

                foreach (var feature in features)
                {
                    var attrs = feature.Attributes;
                    var geometry = feature.Geometry;
                    if (geometry is null || string.IsNullOrWhiteSpace(attrs.Image)) continue;

                    var name = string.IsNullOrWhiteSpace(attrs.Highway) ? (attrs.Descript ?? $"FL511 Camera {attrs.Id}") : $"FL511: {attrs.Highway} - {attrs.Descript}";
                    results.Add(new CameraFeed(
                        Id: $"fl511-{attrs.Id}",
                        Name: name,
                        Latitude: geometry.Y,
                        Longitude: geometry.X,
                        StreamUrl: attrs.Image!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://fl511.com"));
                }

                if (features.Count < PageSize) break;
                offset += PageSize;
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch FL511 traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] Fl511Attributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record Fl511Attributes(
        [property: JsonPropertyName("ID")] string? Id,
        [property: JsonPropertyName("DESCRIPT")] string? Descript,
        [property: JsonPropertyName("HIGHWAY")] string? Highway,
        [property: JsonPropertyName("IMAGE")] string? Image);
}
