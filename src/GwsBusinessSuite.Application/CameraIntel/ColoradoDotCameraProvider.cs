using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// CDOT's statewide traffic camera layer via ArcGIS - genuinely free with no API key, confirmed
// directly (URL_Cam resolves to a real cotrip.org image). Owning ArcGIS account name looks
// informal ("archiveDRP") but the data itself is genuine CDOT/cotrip.org content - confirmed via
// each record's own ImageSource attribution text ("Courtesy of ITS" / "Courtesy of City of
// Lakewood", etc.). Native SR is already 4326, outSR passed explicitly for consistency with every
// other ArcGIS provider in this file. ~908 cameras fits in a single request under this layer's
// own cap, confirmed live - no pagination needed today.
public sealed class ColoradoDotCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<ColoradoDotCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:cdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public string SourceName => "CDOT";

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
            const string url = "arcgis/rest/services/CDOT_Traffic_Cameras_V2/FeatureServer/0/query" +
                "?where=1=1&outFields=CameraId,CameraName,URL_Cam&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            return features
                .Where(f => f.Geometry is not null && !string.IsNullOrWhiteSpace(f.Attributes.UrlCam))
                .Select(f => new CameraFeed(
                    Id: $"cdot-{f.Attributes.CameraId}",
                    Name: string.IsNullOrWhiteSpace(f.Attributes.CameraName) ? $"CDOT Camera {f.Attributes.CameraId}" : $"CDOT: {f.Attributes.CameraName}",
                    Latitude: f.Geometry!.Y,
                    Longitude: f.Geometry.X,
                    StreamUrl: f.Attributes.UrlCam!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.cotrip.org"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch CDOT traffic cameras.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] CdotAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record CdotAttributes(
        [property: JsonPropertyName("CameraId")] long CameraId,
        [property: JsonPropertyName("CameraName")] string? CameraName,
        [property: JsonPropertyName("URL_Cam")] string? UrlCam);
}
