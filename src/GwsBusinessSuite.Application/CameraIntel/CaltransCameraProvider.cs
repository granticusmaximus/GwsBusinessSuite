using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CameraIntel;

// Caltrans' own public CCTV feed, published as one JSON file per district (cwwp2.dot.ca.gov) -
// genuinely free with no API key, confirmed directly (all 12 district endpoints return HTTP 200;
// a real camera's currentImageURL resolved live). This is Caltrans' own DIRECT feed, distinct
// from Datumfeed's aggregated republish of some California cameras - fetching it directly here
// gives full statewide coverage rather than whatever subset Datumfeed happens to carry.
// Districts are fetched in parallel (12 small independent requests) rather than sequentially,
// since each is a separate, unrelated HTTP call with no shared state.
public sealed class CaltransCameraProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<CaltransCameraProvider> logger) : ICameraFeedProvider
{
    private const string CacheKey = "camera-intel:caltrans:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    private static readonly int[] Districts = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    public string SourceName => "Caltrans";

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
        var districtResults = await Task.WhenAll(Districts.Select(d => FetchDistrictAsync(d, cancellationToken)));
        return districtResults.SelectMany(cameras => cameras).ToList();
    }

    private async Task<List<CameraFeed>> FetchDistrictAsync(int district, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"data/d{district}/cctv/cctvStatusD{district:D2}.json";
            var response = await httpClient.GetFromJsonAsync<CaltransResponse>(url, cancellationToken);
            var records = response?.Data;
            if (records is null) return [];

            var results = new List<CameraFeed>();
            foreach (var record in records)
            {
                var cctv = record.Cctv;
                var location = cctv?.Location;
                var imageUrl = cctv?.ImageData?.Static?.CurrentImageUrl;
                if (cctv is null || location is null || string.IsNullOrWhiteSpace(imageUrl)) continue;
                if (!string.Equals(cctv.InService, "true", StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryParseCoordinate(location.Latitude, out var lat) || !TryParseCoordinate(location.Longitude, out var lon)) continue;
                if (lat == 0 && lon == 0) continue;

                var name = string.IsNullOrWhiteSpace(location.LocationName)
                    ? $"Caltrans D{district} Camera {cctv.Index}"
                    : $"Caltrans: {location.LocationName}";

                results.Add(new CameraFeed(
                    Id: $"caltrans-d{district}-{cctv.Index}",
                    Name: name,
                    Latitude: lat,
                    Longitude: lon,
                    StreamUrl: imageUrl!,
                    StreamKind: CameraStreamKind.Snapshot,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://cwwp2.dot.ca.gov"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Caltrans District {District} traffic cameras.", district);
            return [];
        }
    }

    // Caltrans serves lat/lon as strings, not numbers - confirmed directly during implementation.
    private static bool TryParseCoordinate(string? raw, out double value) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private sealed record CaltransResponse([property: JsonPropertyName("data")] List<CaltransRecord>? Data);

    private sealed record CaltransRecord([property: JsonPropertyName("cctv")] CaltransCctv? Cctv);

    private sealed record CaltransCctv(
        [property: JsonPropertyName("index")] string? Index,
        [property: JsonPropertyName("location")] CaltransLocation? Location,
        [property: JsonPropertyName("inService")] string? InService,
        [property: JsonPropertyName("imageData")] CaltransImageData? ImageData);

    private sealed record CaltransLocation(
        [property: JsonPropertyName("locationName")] string? LocationName,
        [property: JsonPropertyName("latitude")] string? Latitude,
        [property: JsonPropertyName("longitude")] string? Longitude);

    private sealed record CaltransImageData([property: JsonPropertyName("static")] CaltransStaticImage? Static);

    private sealed record CaltransStaticImage([property: JsonPropertyName("currentImageURL")] string? CurrentImageUrl);
}
