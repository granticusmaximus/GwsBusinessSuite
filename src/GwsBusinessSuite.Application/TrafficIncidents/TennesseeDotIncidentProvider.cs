using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// TDOT SmartWay's statewide events layer via ArcGIS - genuinely free with no API key, confirmed
// directly. Native geometry is already WGS84 (WKID 4326), unlike most other ArcGIS layers in this
// project - outSR=4326 is passed anyway for consistency, but is a no-op here. CD_ROAD_NAMES was
// null on every sampled live record - RoadwayName falls back to "Unknown roadway" the same way
// MissouriDotIncidentProvider does, since the roadway/location detail this source does publish
// lives inside the free-text DESCRIPTION field instead.
public sealed class TennesseeDotIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<TennesseeDotIncidentProvider> logger) : ITrafficIncidentProvider
{
    private const string CacheKey = "traffic-incidents:tdot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

    public string SourceName => "TDOT SmartWay";

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
            const string url = "arcgis/rest/services/Smartway/Smartway_Events/FeatureServer/0/query" +
                "?where=1=1&outFields=OBJECTID,CD_ROAD_NAMES,DESCRIPTION,EVENT_TYPE,HAS_CLOSURE&outSR=4326&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null) return [];

            var results = new List<TrafficIncident>();
            foreach (var feature in features)
            {
                var attrs = feature.Attributes;
                var geometry = feature.Geometry;
                if (geometry is null) continue;

                results.Add(new TrafficIncident(
                    Id: $"tdot-event-{attrs.ObjectId}",
                    RoadwayName: attrs.RoadNames ?? "Unknown roadway",
                    Description: attrs.Description ?? "",
                    EventType: attrs.EventType ?? "unknown",
                    Severity: attrs.HasClosure == 1 ? "closure" : "unknown",
                    Latitude: geometry.Y,
                    Longitude: geometry.X,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://smartway.tn.gov"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch TDOT SmartWay traffic incidents.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] TdotEventAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record TdotEventAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("CD_ROAD_NAMES")] string? RoadNames,
        [property: JsonPropertyName("DESCRIPTION")] string? Description,
        [property: JsonPropertyName("EVENT_TYPE")] string? EventType,
        [property: JsonPropertyName("HAS_CLOSURE")] int? HasClosure);
}
