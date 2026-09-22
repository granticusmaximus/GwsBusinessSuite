using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// Georgia DOT's public 511 events feed - the same free, unauthenticated ArcGIS org already used
// for GdotTrafficCameraProvider, just a different layer (GDOT_511_Events_Public_View). Verified
// live during implementation: EventType is a real, populated category field with values
// including "accidentsAndIncidents", "roadwork", "closures", and "specialEvents" - confirmed via
// a live distinct-values query, not assumed from the field name alone.
public sealed class GdotTrafficIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<GdotTrafficIncidentProvider> logger) : ITrafficIncidentProvider
{
    private const string CacheKey = "traffic-incidents:gdot:all";
    // Shorter than the camera list's 10-minute cache - incidents (especially accidents) start
    // and clear far more often than a fixed camera inventory does.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

    public string SourceName => "GDOT";

    public async Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        if (!cache.TryGetValue(CacheKey, out IReadOnlyList<TrafficIncident>? allIncidents) || allIncidents is null)
        {
            allIncidents = await FetchAllAsync(cancellationToken);
            cache.Set(CacheKey, allIncidents, CacheDuration);
        }

        // Only ~145 events statewide at any time (well under the service's own 1000-record
        // page size) - cheap to cache the whole list and filter client-side per view, same as
        // the camera provider.
        return allIncidents.Where(incident => bbox.Contains(incident.Latitude, incident.Longitude)).ToList();
    }

    private async Task<IReadOnlyList<TrafficIncident>> FetchAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            const string url = "arcgis/rest/services/GDOT_511_Events_Public_View/FeatureServer/0/query" +
                "?where=1=1&outFields=ID,RoadwayName,Description,EventType,Severity,Latitude,Longitude&f=json";
            var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
            var features = response?.Features;
            if (features is null)
            {
                return [];
            }

            return features
                .Select(feature => feature.Attributes)
                .Where(attrs => attrs is { Latitude: not 0, Longitude: not 0 })
                .Select(attrs => new TrafficIncident(
                    Id: $"gdot-event-{attrs.Id}",
                    RoadwayName: attrs.RoadwayName ?? "Unknown roadway",
                    Description: attrs.Description ?? "",
                    EventType: attrs.EventType ?? "unknown",
                    Severity: attrs.Severity ?? "unknown",
                    Latitude: attrs.Latitude,
                    Longitude: attrs.Longitude,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://511ga.org"))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch GDOT traffic incidents.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection(List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(GdotEventAttributes Attributes);

    // ArcGIS's field for this layer is genuinely "ID" (all-caps) - confirmed via a live query,
    // not assumed - unlike GDOT's own camera layer, which uses "Id". System.Text.Json's default
    // GetFromJsonAsync options are case-sensitive, so this would silently bind to 0 without the
    // explicit attribute.
    private sealed record GdotEventAttributes(
        [property: JsonPropertyName("ID")] int Id,
        string? RoadwayName, string? Description, string? EventType, string? Severity, double Latitude, double Longitude);
}
