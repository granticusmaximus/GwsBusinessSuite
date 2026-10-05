using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// WZDx (Work Zone Data Exchange) is a federally-standardized GeoJSON schema several state DOTs
// publish for free with no API key, even when their own camera API is key-gated - confirmed live
// for Arizona, Nevada, Utah, Oklahoma, Hawaii, and Idaho, each on a completely different domain
// but the identical WZDx v4.x schema. One class, parameterized per state/feed (matching
// Cars511IncidentProvider's and NewEnglandCompassIncidentProvider's own precedent), constructed
// with the feed's full absolute URL rather than a shared BaseAddress + relative path, since each
// state's feed lives on its own unrelated domain. Per an explicit decision: these are work-zone/
// construction events, not crash-style incidents, but are surfaced through the same
// ITrafficIncidentProvider/TrafficIncident model (EventType carries the source's own
// "work-zone"/"detour" category strings, same "pass the raw category through" convention as
// every other incident provider in this project).
public sealed class WzdxIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<WzdxIncidentProvider> logger,
    string feedUrl,
    string sourceKey,
    string sourceName,
    string sourceAttributionUrl,
    TimeProvider? timeProvider = null) : ITrafficIncidentProvider
{
    private readonly string cacheKey = $"traffic-incidents:wzdx:{sourceKey}";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public string SourceName => sourceName;

    public async Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(cacheKey, out IReadOnlyList<TrafficIncident>? allIncidents) || allIncidents is null)
        {
            allIncidents = await FetchAllAsync(cancellationToken);
            cache.Set(cacheKey, allIncidents, CacheDuration);
        }

        return allIncidents.Where(incident => bbox.Contains(incident.Latitude, incident.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<TrafficIncident>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>(feedUrl, cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var results = new List<TrafficIncident>();
            foreach (var feature in features)
            {
                // Statewide feeds publish planned and finished work too - Florida's had 26,109
                // entries of which 1,629 were happening now (2026-10-04). Only show current work;
                // a missing date means no limit on that side.
                if (!IsCurrent(feature.Properties, now)) continue;
                if (!TryExtractFirstPoint(feature.Geometry, out var lon, out var lat)) continue;

                var core = feature.Properties?.CoreDetails;
                if (core is null) continue;

                var roadwayName = core.RoadNames is { Count: > 0 } ? string.Join(", ", core.RoadNames) : "Unknown roadway";

                results.Add(new TrafficIncident(
                    Id: $"wzdx-{sourceKey}-{feature.Id ?? feature.Properties!.RoadEventId ?? Guid.NewGuid().ToString("N")}",
                    RoadwayName: roadwayName,
                    Description: core.Description ?? "",
                    EventType: core.EventType ?? "work-zone",
                    Severity: feature.Properties!.VehicleImpact ?? "unknown",
                    Latitude: lat,
                    Longitude: lon,
                    SourceName: sourceName,
                    SourceAttributionUrl: sourceAttributionUrl));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch WZDx ({SourceKey}) traffic incidents.", sourceKey);
            return [];
        }
    }

    private static bool IsCurrent(WzdxProperties? properties, DateTimeOffset now)
    {
        if (properties is null) return true;
        if (string.Equals(properties.EventStatus, "completed", StringComparison.OrdinalIgnoreCase)) return false;
        if (TryParseDate(properties.StartDate, out var start) && start > now) return false;
        if (TryParseDate(properties.EndDate, out var end) && end < now) return false;
        return true;
    }

    private static bool TryParseDate(string? value, out DateTimeOffset date) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out date);

    // WZDx geometry is commonly LineString (a work zone spans a road segment) but can also be
    // Point or MultiPoint - the same "descend into nested coordinate arrays until a 2-number leaf
    // is found" approach already proven for QueenslandTrafficIncidentProvider's mixed-geometry
    // feed handles every WZDx shape without a distinct record type per geometry kind.
    private static bool TryExtractFirstPoint(JsonElement? geometry, out double longitude, out double latitude)
    {
        longitude = 0;
        latitude = 0;
        if (geometry is not { ValueKind: JsonValueKind.Object } geom) return false;
        if (!geom.TryGetProperty("coordinates", out var coords)) return false;

        var current = coords;
        while (current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 0)
        {
            var first = current[0];
            if (first.ValueKind is JsonValueKind.Number && current.GetArrayLength() >= 2)
            {
                longitude = current[0].GetDouble();
                latitude = current[1].GetDouble();
                return true;
            }
            current = first;
        }
        return false;
    }

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<WzdxFeature>? Features);

    private sealed record WzdxFeature(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("properties")] WzdxProperties? Properties,
        [property: JsonPropertyName("geometry")] JsonElement? Geometry);

    private sealed record WzdxProperties(
        [property: JsonPropertyName("core_details")] WzdxCoreDetails? CoreDetails,
        [property: JsonPropertyName("road_event_id")] string? RoadEventId,
        [property: JsonPropertyName("vehicle_impact")] string? VehicleImpact,
        [property: JsonPropertyName("start_date")] string? StartDate,
        [property: JsonPropertyName("end_date")] string? EndDate,
        [property: JsonPropertyName("event_status")] string? EventStatus);

    private sealed record WzdxCoreDetails(
        [property: JsonPropertyName("event_type")] string? EventType,
        [property: JsonPropertyName("road_names")] List<string>? RoadNames,
        [property: JsonPropertyName("description")] string? Description);
}
