using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// VDOT's statewide traffic camera layer, published as a plain ArcGIS FeatureServer - genuinely
// free with no API key, confirmed directly (objectIdField is "FID", not "OBJECTID" - confirmed
// via a live query, matters for pagination). Native geometry is Web Mercator, so outSR=4326 is
// requested explicitly, same rationale as every other ArcGIS-based provider in this file. Also
// carries an HLS stream (ios_url) per camera, but image_url (a plain JPG thumbnail) is used here
// instead - it's the simpler, always-available field and this provider has no confirmed need for
// live video over a periodically-refreshed still, matching this project's existing preference for
// the plainer confirmed path when both are available (see IowaDotCameraProvider's own reasoning).
// ~1,293 cameras exceeds this layer's per-request cap, so results are paged via resultOffset.
public sealed class VirginiaDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<VirginiaDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:vdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private const int PageSize = 1000;

    public string SourceName => "VDOT";

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
                var url = "arcgis/rest/services/CameraLocationVDOT/FeatureServer/0/query" +
                    $"?where=1=1&outFields=FID,id,descriptio,route,image_url,active&outSR=4326&resultOffset={offset}&resultRecordCount={PageSize}&f=json";
                var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
                var features = response?.Features;
                if (features is null || features.Count == 0) break;

                foreach (var feature in features)
                {
                    var attrs = feature.Attributes;
                    var geometry = feature.Geometry;
                    if (geometry is null || string.IsNullOrWhiteSpace(attrs.ImageUrl)) continue;
                    if (!string.Equals(attrs.Active, "true", StringComparison.OrdinalIgnoreCase)) continue;

                    results.Add(new CameraFeed(
                        Id: $"vdot-{attrs.Fid}",
                        Name: string.IsNullOrWhiteSpace(attrs.Description) ? $"VDOT Camera {attrs.Fid}" : $"VDOT: {attrs.Description}",
                        Latitude: geometry.Y,
                        Longitude: geometry.X,
                        StreamUrl: attrs.ImageUrl!,
                        StreamKind: CameraStreamKind.Snapshot,
                        SourceName: SourceName,
                        SourceAttributionUrl: "https://www.511virginia.org"));
                }

                if (features.Count < PageSize) break;
                offset += PageSize;
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch VDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] VdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record VdotAttributes(
        [property: JsonPropertyName("FID")] long Fid,
        [property: JsonPropertyName("descriptio")] string? Description,
        [property: JsonPropertyName("image_url")] string? ImageUrl,
        [property: JsonPropertyName("active")] string? Active);
}
