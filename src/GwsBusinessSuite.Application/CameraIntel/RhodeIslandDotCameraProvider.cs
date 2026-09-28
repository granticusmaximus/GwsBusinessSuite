using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// RIDOT's traffic camera layer (Rhodeways MapServer, layer 6) - genuinely free with no API key,
// confirmed directly. An earlier research pass flagged Rhode Island as having no discoverable
// API; this ArcGIS layer simply wasn't surfaced by that search, another case worth re-verifying
// rather than trusting a "not found" finding indefinitely. This is a MapServer, not a
// FeatureServer, but the /query endpoint syntax is identical - confirmed live.
// CCVEWebURL values can contain spaces and parentheses (e.g. a camera named with "(Pawt)") -
// HtmlEncode in the renderer already makes that safe in an href/src attribute, no extra
// escaping needed here.
public sealed class RhodeIslandDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<RhodeIslandDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:ridot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "RIDOT";

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
            const string url = "hosting/rest/services/RIDOT/Rhodeways/MapServer/6/query" +
                "?where=1=1&outFields=OBJECTID,Description,CCVEWebURL&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.CcveWebUrl))
                .Select(f => new CameraFeed(
                    Id: $"ridot-{f.Attributes.ObjectId}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.Description) ? $"RIDOT Camera {f.Attributes.ObjectId}" : $"RIDOT: {f.Attributes.Description}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.CcveWebUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.dot.ri.gov"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch RIDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] RiDotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record RiDotAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("Description")] string? Description,
        [property: JsonPropertyName("CCVEWebURL")] string? CcveWebUrl);
}
