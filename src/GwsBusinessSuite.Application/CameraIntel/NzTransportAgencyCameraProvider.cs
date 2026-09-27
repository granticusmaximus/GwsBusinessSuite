using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// New Zealand's live traffic camera layer, published as a plain ArcGIS FeatureServer - genuinely
// free with no API key, confirmed directly (a real camera's `imageurl` resolved live, HTTP 200
// image/jpeg, Last-Modified under a minute old). Uses `outSR=4326` on every query so `geometry`
// is always plain WGS84 degrees regardless of a layer's native spatial reference - simpler and
// more robust than hunting for a lat/lon attribute pair per source (some publish one, some
// don't, and some publish one in a different reference than geometry itself, e.g. Oregon's own
// provider). One documented caveat: this specific layer is labeled a "prototype" hosted by a
// third party (Eagle Technology Group) rather than NZTA's own polished/official API (which
// requires emailing NZTA directly for access) - the underlying image host (trafficnz.info) is
// still NZTA's own official camera domain and works today, but this source may be less stable
// long-term than a state DOT's own first-party API.
public sealed class NzTransportAgencyCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NzTransportAgencyCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:nzta:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "NZTA";

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
            const string url = "arcgis/rest/services/LiveCamerasNZTA_Public_View/FeatureServer/0/query" +
                "?where=1=1&outFields=id,name,region,offline,undermaintenance,imageurl&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null
                    && !string.IsNullOrWhiteSpace(f.Attributes.ImageUrl)
                    && !string.Equals(f.Attributes.Offline, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(f.Attributes.UnderMaintenance, "true", StringComparison.OrdinalIgnoreCase))
                .Select(f => new CameraFeed(
                    Id: $"nzta-{f.Attributes.Id}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.Name) ? $"NZTA Camera {f.Attributes.Id}" : $"NZTA: {f.Attributes.Name}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.ImageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.trafficnz.info"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch NZTA traffic cameras.");
            return [];
        }
    }

    // ArcGIS's own JSON keys ("features"/"attributes"/"geometry"/"x"/"y") and this layer's own
    // attribute field names are all lowercase - explicit [JsonPropertyName] on every field
    // rather than relying on GetFromJsonAsync's case-sensitivity behavior, matching
    // GdotTrafficIncidentProvider's own documented precedent for exactly this gotcha.
    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] NztaCameraAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record NztaCameraAttributes(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("offline")] string? Offline,
        [property: JsonPropertyName("undermaintenance")] string? UnderMaintenance,
        [property: JsonPropertyName("imageurl")] string? ImageUrl);
}
