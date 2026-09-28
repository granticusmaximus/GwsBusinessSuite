using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// MoDOT's statewide incident layer via ArcGIS - genuinely free with no API key, confirmed
// directly. Real, confirmed gotcha: this layer's schema is much thinner than most other incident
// providers in this project - only STYLE/MESSAGE/HEADLINE plus OBJECTID exist, no Route, no
// StartTime/EndTime, no Priority field at all. RoadwayName and Severity are always "Unknown"/
// "unknown" here since the source genuinely doesn't publish that structured data, not because of
// a mapping bug - the incident's own message/headline text carries whatever location detail
// exists.
public sealed class MissouriDotIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<MissouriDotIncidentProvider> logger) : ITrafficIncidentProvider
{
    private const string CacheKey = "traffic-incidents:modot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

    public string SourceName => "MoDOT";

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
            const string url = "arcgis/rest/services/TravelerInformation/NWSDATA/MapServer/0/query" +
                "?where=1=1&outFields=OBJECTID,STYLE,MESSAGE,HEADLINE&outSR=4326&f=json";
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
                    Id: $"modot-event-{attrs.ObjectId}",
                    RoadwayName: "Unknown roadway",
                    Description: attrs.Message ?? attrs.Headline ?? "",
                    EventType: attrs.Style ?? "unknown",
                    Severity: "unknown",
                    Latitude: geometry.Y,
                    Longitude: geometry.X,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.modot.org"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch MoDOT traffic incidents.");
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] ModotEventAttributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record ModotEventAttributes(
        [property: JsonPropertyName("OBJECTID")] long ObjectId,
        [property: JsonPropertyName("STYLE")] string? Style,
        [property: JsonPropertyName("MESSAGE")] string? Message,
        [property: JsonPropertyName("HEADLINE")] string? Headline);
}
