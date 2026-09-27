using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// CARS511 is a multi-state 511-events sharing hub hosted on Iowa DOT's own ArcGIS org
// (confirmed live during implementation: Minnesota's and Nebraska's incident layers both live at
// the identical org id, org "8lRhdTsQyJpO52F1", under an "IowaDOT_SODA"-owned collection) -
// genuinely free with no API key. One class, parameterized per state, rather than a near-
// duplicate class per state, since the schema and org are identical across states in this
// family - only the layer name (and therefore SourceName/attribution) differs. Native geometry
// is Web Mercator (EPSG:3857); outSR=4326 is requested explicitly so geometry.x/y are always
// plain WGS84 degrees (confirmed directly: without it, coordinates are Mercator meters).
public sealed class Cars511IncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<Cars511IncidentProvider> logger,
    string stateCode,
    string sourceName,
    string sourceAttributionUrl) : ITrafficIncidentProvider
{
    private readonly string cacheKey = $"traffic-incidents:cars511:{stateCode}";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

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
            var url = $"arcgis/rest/services/CARS511_{stateCode}_Events_View/FeatureServer/0/query" +
                "?where=1=1&outFields=ID,Route,headline,phrase,cause,STYLE&outSR=4326&f=json";
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
                    Id: $"cars511-{stateCode}-{attrs.Id}",
                    RoadwayName: attrs.Route ?? "Unknown roadway",
                    Description: attrs.Cause ?? attrs.Headline ?? "",
                    EventType: attrs.Style ?? attrs.Phrase ?? "unknown",
                    Severity: "unknown",
                    Latitude: geometry.Y,
                    Longitude: geometry.X,
                    SourceName: SourceName,
                    SourceAttributionUrl: sourceAttributionUrl));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch CARS511 {StateCode} traffic incidents.", stateCode);
            return [];
        }
    }

    private sealed record ArcGisFeatureCollection([property: JsonPropertyName("features")] List<ArcGisFeature>? Features);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] Cars511Attributes Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record Cars511Attributes(
        [property: JsonPropertyName("ID")] string? Id,
        [property: JsonPropertyName("Route")] string? Route,
        [property: JsonPropertyName("headline")] string? Headline,
        [property: JsonPropertyName("phrase")] string? Phrase,
        [property: JsonPropertyName("cause")] string? Cause,
        [property: JsonPropertyName("STYLE")] string? Style);
}
