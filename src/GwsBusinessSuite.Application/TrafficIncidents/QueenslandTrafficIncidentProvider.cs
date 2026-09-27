using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// Queensland, Australia's public traffic incidents feed - the same free, zero-auth source as
// QueenslandTrafficCameraProvider, just a different endpoint (events_v2.geojson). Verified live
// during implementation: real event_type/event_subtype/impact/road_summary fields, hundreds of
// currently-active records. Geometry is a genuine mix of GeoJSON types across records
// (MultiLineString for a road-segment closure, MultiPoint for a point hazard, occasionally
// GeometryCollection) - unlike a plain camera feed, there's no single fixed coordinate shape, so
// geometry is read as a raw JsonElement and the first coordinate pair found (at whatever nesting
// depth) is used as this incident's representative point, which is all TrafficIncident's flat
// Latitude/Longitude needs.
public sealed class QueenslandTrafficIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<QueenslandTrafficIncidentProvider> logger) : ITrafficIncidentProvider
{
    private const string CacheKey = "traffic-incidents:qldtraffic:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

    public string SourceName => "QLD Traffic";

    public async Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<TrafficIncident>? allIncidents) || allIncidents is null)
        {
            allIncidents = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allIncidents, CacheDuration);
        }

        return allIncidents.Where(incident => bbox.Contains(incident.Latitude, incident.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<TrafficIncident>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var collection = await httpClient.GetFromJsonAsync<GeoJsonFeatureCollection>("events_v2.geojson", cancellationToken);
            var features = collection?.Features;
            if (features is null) return [];

            var results = new List<TrafficIncident>();
            foreach (var feature in features)
            {
                if (!TryExtractFirstPoint(feature.Geometry, out var lon, out var lat)) continue;

                var props = feature.Properties;
                var roadwayName = props.RoadSummary?.RoadName ?? "Unknown roadway";
                var description = string.IsNullOrWhiteSpace(props.Description) ? (props.EventSubtype ?? "") : props.Description;

                results.Add(new TrafficIncident(
                    Id: $"qldtraffic-event-{props.Id}",
                    RoadwayName: roadwayName,
                    Description: description,
                    EventType: props.EventType ?? "unknown",
                    Severity: props.EventPriority ?? "unknown",
                    Latitude: lat,
                    Longitude: lon,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.qldtraffic.qld.gov.au"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch Queensland traffic incidents.");
            return [];
        }
    }

    // GeoJSON coordinates nest one level deeper per geometry type (Point: [lon,lat],
    // MultiPoint/LineString: [[lon,lat],...], MultiLineString/Polygon: [[[lon,lat],...],...]).
    // Descending into element[0] until hitting a two-number array handles every shape actually
    // observed in this feed (MultiLineString, MultiPoint, GeometryCollection) without needing a
    // distinct strongly-typed record per geometry type.
    private static bool TryExtractFirstPoint(JsonElement? geometry, out double longitude, out double latitude)
    {
        longitude = 0;
        latitude = 0;
        if (geometry is not { ValueKind: JsonValueKind.Object } geom) return false;

        if (geom.TryGetProperty("geometries", out var geometries) && geometries.ValueKind == JsonValueKind.Array)
        {
            // GeometryCollection - try each sub-geometry until one yields a point.
            foreach (var sub in geometries.EnumerateArray())
            {
                if (TryExtractFirstPoint(sub, out longitude, out latitude)) return true;
            }
            return false;
        }

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

    private sealed record GeoJsonFeatureCollection([property: JsonPropertyName("features")] List<GeoJsonFeature>? Features);

    private sealed record GeoJsonFeature(
        [property: JsonPropertyName("properties")] QldEventProperties Properties,
        [property: JsonPropertyName("geometry")] JsonElement? Geometry);

    private sealed record QldEventProperties(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("event_type")] string? EventType,
        [property: JsonPropertyName("event_subtype")] string? EventSubtype,
        [property: JsonPropertyName("event_priority")] string? EventPriority,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("road_summary")] QldRoadSummary? RoadSummary);

    private sealed record QldRoadSummary([property: JsonPropertyName("road_name")] string? RoadName);
}
