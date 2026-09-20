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
            foreach (var feature in features)
            {
                var alert = TryParse(feature);
                if (alert is not null)
                {
                    alerts.Add(alert);
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

    private static WeatherAlert? TryParse(JsonNode? feature)
    {
        var id = feature?["properties"]?["id"]?.GetValue<string>() ?? feature?["id"]?.GetValue<string>();
        var geometryType = feature?["geometry"]?["type"]?.GetValue<string>();
        var coordinates = feature?["geometry"]?["coordinates"]?.AsArray();
        if (string.IsNullOrWhiteSpace(id) || coordinates is null)
        {
            // No usable geometry (e.g. a zone-only alert with geometry: null) - nothing to draw.
            return null;
        }

        var rings = geometryType switch
        {
            "Polygon" => ParsePolygonRings(coordinates),
            "MultiPolygon" => coordinates
                .SelectMany(polygon => ParsePolygonRings(polygon!.AsArray()))
                .ToList(),
            _ => []
        };
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
