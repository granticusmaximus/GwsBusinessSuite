using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// MDOT's "MiDrive Cameras" layer, published via ArcGIS Online - genuinely free with no API key,
// confirmed directly. Not discoverable through MDOT's own generic ArcGIS REST folder browsing -
// only found via ArcGIS Online's sharing-API search by organization id (MDOT_GIS) - worth noting
// since a plausible-looking but wrong lead (a similarly-shaped "cameras" FeatureServer under a
// different org) resolved to the US Virgin Islands, not Michigan, when checked live. The layer
// name itself contains a space ("MiDrive Cameras"), which must be percent-encoded in the query
// URL. `Image` is a static JPG thumbnail, not a live stream.
public sealed class MichiganDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<MichiganDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:mdot-mi:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "MDOT MiDrive";

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
            const string url = "arcgis/rest/services/MiDrive%20Cameras/FeatureServer/0/query" +
                "?where=1=1&outFields=OBJECTID,Route,Location,Image&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.Image))
                .Select(f => new CameraFeed(
                    Id: $"mdot-mi-{f.Attributes.ObjectId}",
                    Name: BuildName(f.Attributes),
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.Image!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://mdotjboss.state.mi.us/MiDrive/"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch MDOT MiDrive traffic cameras.");
            return [];
        }
    }

    // Location values observed live carry their own leading space (e.g. " @ Mound NB"), designed
    // to be concatenated directly after Route ("11 Mile" + " @ Mound NB") - trimming Location
    // here would collapse that into "11 Mile@ Mound NB".
    private static string BuildName(MidriveAttributes attrs)
    {
        var route = attrs.Route?.Trim() ?? "";
        var location = attrs.Location ?? "";
        var combined = $"{route}{location}".Trim();
        return combined.Length == 0 ? $"MDOT Camera {attrs.ObjectId}" : $"MDOT: {combined}";
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] MidriveAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record MidriveAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("Route")] string? Route,
        [property: JsonPropertyName("Location")] string? Location,
        [property: JsonPropertyName("Image")] string? Image);
}
