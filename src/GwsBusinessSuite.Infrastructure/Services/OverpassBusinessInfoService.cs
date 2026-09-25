using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Two free, no-key public APIs combined into one hover-card lookup:
//   1. OpenStreetMap's Overpass API (overpass-api.de) for the nearest named business/POI tag
//      within a small radius - confirmed directly during implementation (real OSM3S JSON
//      responses, no auth). No bbox/pagination support needed here - this is a small,
//      point-radius query, not the wide-area queries CameraIntel's providers do.
//   2. Wikimedia Commons' geosearch API (commons.wikimedia.org) for nearby geotagged photos -
//      confirmed directly: a combined generator=geosearch + prop=imageinfo query returns real,
//      direct thumbnail URLs in one call, no key required.
// Both results are combined into one BusinessCardInfo so the JS side only ever makes one
// [JSInvokable] call, matching the existing hover-identify/ReverseGeocodeAsync convention.
//
// Cached per-grid-cell (lat/lon rounded to 4 decimal places, roughly an 11m cell) rather than
// per-exact-hover-position - a hover gesture naturally jitters across many nearly-identical
// coordinates, and Overpass's shared public instance is a resource this app should be a good
// citizen of (per its own documented request to cache rather than hammer it), not a per-request
// budget to spend freely.
public sealed class OverpassBusinessInfoService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<OverpassBusinessInfoService> logger) : IBusinessInfoService
{
    private const double SearchRadiusMeters = 40;
    private const int MaxPhotos = 6;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    public async Task<BusinessCardInfo?> GetBusinessCardAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"business-card:{Math.Round(latitude, 4)}:{Math.Round(longitude, 4)}";
        if (cache.TryGetValue(cacheKey, out BusinessCardInfo? cached))
        {
            return cached;
        }

        var poi = await FindNearestPoiAsync(latitude, longitude, cancellationToken);
        if (poi is null)
        {
            cache.Set(cacheKey, (BusinessCardInfo?)null, CacheDuration);
            return null;
        }

        var photos = await FindNearbyPhotosAsync(latitude, longitude, cancellationToken);
        var result = new BusinessCardInfo(poi.Value.Name, poi.Value.Website, poi.Value.Category, photos);
        cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private async Task<(string Name, string? Website, string? Category)?> FindNearestPoiAsync(
        double latitude, double longitude, CancellationToken cancellationToken)
    {
        try
        {
            var lat = latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lon = longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var query = $"[out:json][timeout:10];" +
                $"(node(around:{SearchRadiusMeters},{lat},{lon})[\"shop\"];" +
                $"node(around:{SearchRadiusMeters},{lat},{lon})[\"amenity\"][\"amenity\"!=\"parking\"];" +
                $"node(around:{SearchRadiusMeters},{lat},{lon})[\"office\"];);" +
                "out body 10;";

            using var client = httpClientFactory.CreateClient();
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", query)]);
            using var response = await client.PostAsync("https://overpass-api.de/api/interpreter", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var elements = root?["elements"]?.AsArray();
            if (elements is null || elements.Count == 0)
            {
                return null;
            }

            (string Name, string? Website, string? Category, double DistanceMeters)? nearest = null;
            foreach (var element in elements)
            {
                var tags = element?["tags"];
                var name = tags?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var elementLat = element?["lat"]?.GetValue<double>();
                var elementLon = element?["lon"]?.GetValue<double>();
                if (elementLat is null || elementLon is null)
                {
                    continue;
                }

                var distance = HaversineMeters(latitude, longitude, elementLat.Value, elementLon.Value);
                if (nearest is null || distance < nearest.Value.DistanceMeters)
                {
                    var website = tags?["website"]?.GetValue<string>() ?? tags?["contact:website"]?.GetValue<string>();
                    var category = tags?["shop"]?.GetValue<string>() ?? tags?["amenity"]?.GetValue<string>() ?? tags?["office"]?.GetValue<string>();
                    nearest = (name!, website, category, distance);
                }
            }

            return nearest is null ? null : (nearest.Value.Name, nearest.Value.Website, nearest.Value.Category);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to query Overpass for nearby business info.");
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> FindNearbyPhotosAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        try
        {
            var coord = $"{latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var url = "https://commons.wikimedia.org/w/api.php" +
                "?action=query&generator=geosearch" +
                $"&ggscoord={Uri.EscapeDataString(coord)}&ggsradius=200&ggslimit={MaxPhotos}&ggsnamespace=6" +
                "&prop=imageinfo&iiprop=url&iiurlwidth=400&format=json";

            using var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var pages = root?["query"]?["pages"]?.AsObject();
            if (pages is null)
            {
                return [];
            }

            var urls = new List<string>();
            foreach (var (_, page) in pages)
            {
                var thumbUrl = page?["imageinfo"]?.AsArray().FirstOrDefault()?["thumburl"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(thumbUrl))
                {
                    urls.Add(thumbUrl);
                }
            }
            return urls;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to query Wikimedia Commons for nearby photos.");
            return [];
        }
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6_371_000;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) +
            (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMeters * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
