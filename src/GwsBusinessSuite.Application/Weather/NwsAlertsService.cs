using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Weather;

// NOAA's National Weather Service alerts API (https://api.weather.gov/alerts/active) - free, no
// API key, CORS-open (confirmed directly: access-control-allow-origin: *), but still called
// server-side rather than straight from the browser: NWS's own developer docs ask every
// consumer to send a descriptive User-Agent identifying the application, and a browser's
// fetch()/XHR can never override that header (a Fetch-spec "forbidden header name") - only a
// server-side HttpClient can actually honor that request. Same "set a real UA, don't ride the
// default" convention already established for dev.to in TrendResearchService.
public sealed class NwsAlertsService(HttpClient httpClient, IMemoryCache cache, ILogger<NwsAlertsService> logger) : INwsAlertsService
{
    private const string CacheKey = "weather:nws:active-alerts";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    // Most alerts are issued by forecast zone with no outline of their own (13-11-26: 301 active,
    // none with geometry), so the layer drew almost nothing on the web or the Mac. Their zones'
    // outlines come from api.weather.gov/zones (key-less), kept a week (zones don't move); at most
    // MaxZoneFetches new zones per refresh, so the first refreshes fill in over a few minutes rather
    // than asking NWS for a thousand zones at once.
    private static readonly TimeSpan ZoneCacheDuration = TimeSpan.FromDays(7);
    public const int MaxZoneFetches = 150;
    private const int ZoneParallel = 6;
    // A zone outline is thinned to about this many corners; counties have thousands.
    public const int MaxRingVertices = 120;

    public async Task<IReadOnlyList<WeatherAlert>> GetActiveAlertsAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<WeatherAlert>? allAlerts) || allAlerts is null)
        {
            allAlerts = await FetchActiveAlertsAsync(cancellationToken);
            cache.Set(CacheKey, allAlerts, CacheDuration);
        }

        return allAlerts
            .Where(alert => alert.Rings.Any(ring => ring.Any(vertex => bbox.Contains(vertex.Latitude, vertex.Longitude))))
            .ToList();
    }

    private async Task<IReadOnlyList<WeatherAlert>> FetchActiveAlertsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.GetAsync("alerts/active?status=actual&message_type=alert", cancellationToken);
            response.EnsureSuccessStatusCode();

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var features = root?["features"]?.AsArray();
            if (features is null)
            {
                return [];
            }

            var alerts = new List<WeatherAlert>();
            var zoneOnly = new List<(JsonNode Feature, IReadOnlyList<string> Zones)>();
            foreach (var feature in features)
            {
                var alert = TryParse(feature);
                if (alert is not null)
                {
                    alerts.Add(alert);
                }
                else if (ZonesOf(feature) is { Count: > 0 } zones)
                {
                    zoneOnly.Add((feature!, zones));
                }
            }
            if (zoneOnly.Count > 0)
            {
                await FetchZonesAsync(zoneOnly.SelectMany(z => z.Zones).Distinct(StringComparer.Ordinal), cancellationToken);
                foreach (var (feature, zones) in zoneOnly)
                {
                    var rings = zones.SelectMany(zone => cache.TryGetValue(ZoneKey(zone), out IReadOnlyList<IReadOnlyList<(double, double)>>? zoneRings) && zoneRings is not null
                        ? zoneRings : []).ToList();
                    if (rings.Count > 0 && TryParse(feature, rings) is { } alert) alerts.Add(alert);
                }
            }
            return alerts;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch NWS active alerts.");
            return [];
        }
    }

    // The zone outlines not yet cached, fetched a few at a time. Only api.weather.gov zone links.
    private async Task FetchZonesAsync(IEnumerable<string> zones, CancellationToken cancellationToken)
    {
        var wanted = zones.Where(zone => !cache.TryGetValue(ZoneKey(zone), out _)).Take(MaxZoneFetches).ToList();
        await Parallel.ForEachAsync(wanted, new ParallelOptions { MaxDegreeOfParallelism = ZoneParallel, CancellationToken = cancellationToken }, async (zone, ct) =>
        {
            try
            {
                using var response = await httpClient.GetAsync(zone, ct);
                if (!response.IsSuccessStatusCode) return;
                var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                var rings = Rings(root?["geometry"]?["type"]?.GetValue<string>(), root?["geometry"]?["coordinates"]?.AsArray())
                    .Select(Thin).ToList();
                cache.Set(ZoneKey(zone), (IReadOnlyList<IReadOnlyList<(double, double)>>)rings, ZoneCacheDuration);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogDebug(ex, "NWS zone {Zone} couldn't be read", zone);
            }
        });
    }

    private static string ZoneKey(string zone) => "weather:nws:zone:" + zone;

    // properties.affectedZones: "https://api.weather.gov/zones/forecast/GAZ033" and the like.
    public static IReadOnlyList<string> ZonesOf(JsonNode? feature) =>
        feature?["properties"]?["affectedZones"] is JsonArray zones
            ? zones.Select(z => z?.GetValue<string>()).OfType<string>()
                .Where(z => Uri.TryCreate(z, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                    && uri.Host == "api.weather.gov" && uri.AbsolutePath.StartsWith("/zones/", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal).ToList()
            : [];

    // Every n-th corner so a ring has at most MaxRingVertices, closed again at the end.
    public static IReadOnlyList<(double Longitude, double Latitude)> Thin(IReadOnlyList<(double Longitude, double Latitude)> ring)
    {
        if (ring.Count <= MaxRingVertices) return ring;
        var step = (int)Math.Ceiling(ring.Count / (double)MaxRingVertices);
        var thinned = ring.Where((_, i) => i % step == 0).ToList();
        if (thinned[^1] != ring[^1]) thinned.Add(ring[^1]);
        return thinned;
    }

    private static List<IReadOnlyList<(double Longitude, double Latitude)>> Rings(string? geometryType, JsonArray? coordinates) =>
        coordinates is null ? [] : geometryType switch
        {
            "Polygon" => ParsePolygonRings(coordinates),
            "MultiPolygon" => coordinates.SelectMany(polygon => ParsePolygonRings(polygon!.AsArray())).ToList(),
            _ => []
        };

    private static WeatherAlert? TryParse(JsonNode? feature, IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>>? zoneRings = null)
    {
        var id = feature?["properties"]?["id"]?.GetValue<string>() ?? feature?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;
        // Its own outline, else (a zone-only alert) its zones' outlines; with neither, nothing to draw.
        var rings = zoneRings?.ToList() ?? Rings(feature?["geometry"]?["type"]?.GetValue<string>(), feature?["geometry"]?["coordinates"]?.AsArray());
        if (rings.Count == 0)
        {
            return null;
        }

        var properties = feature?["properties"];
        return new WeatherAlert(
            Id: id,
            Event: properties?["event"]?.GetValue<string>() ?? "Weather Alert",
            Severity: properties?["severity"]?.GetValue<string>() ?? "Unknown",
            AreaDescription: properties?["areaDesc"]?.GetValue<string>() ?? "",
            Expires: properties?["expires"]?.GetValue<string>() ?? "",
            Rings: rings);
    }

    private static List<IReadOnlyList<(double Longitude, double Latitude)>> ParsePolygonRings(JsonArray polygonCoordinates)
    {
        var rings = new List<IReadOnlyList<(double, double)>>();
        foreach (var ring in polygonCoordinates)
        {
            var vertices = new List<(double, double)>();
            foreach (var point in ring!.AsArray())
            {
                var pair = point!.AsArray();
                if (pair.Count >= 2)
                {
                    vertices.Add((pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
                }
            }
            if (vertices.Count >= 3)
            {
                rings.Add(vertices);
            }
        }
        return rings;
    }
}
