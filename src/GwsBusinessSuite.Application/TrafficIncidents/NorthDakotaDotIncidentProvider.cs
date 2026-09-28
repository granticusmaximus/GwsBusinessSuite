using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// NDDOT's statewide road-condition/incident feed - genuinely free with no API key, confirmed
// directly, same domain as NorthDakotaDotCameraProvider's own camera feed. Real, confirmed shape
// gotcha: the top-level JSON response is a plain array of GeoJSON-shaped Feature objects, not a
// FeatureCollection wrapper (no top-level "features" key) - deserialized directly as
// List&lt;NdAlertFeature&gt; here rather than reusing the FeatureCollection-wrapper record shape
// every other GeoJSON provider in this project uses.
public sealed class NorthDakotaDotIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NorthDakotaDotIncidentProvider> logger) : ITrafficIncidentProvider
{
    private const string CacheKey = "traffic-incidents:nddot:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

    public string SourceName => "NDDOT";

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
            var features = await httpClient.GetFromJsonAsync<List<NdAlertFeature>>("geojson_nc/alerts.json", cancellationToken);
            if (features is null) return [];

            var results = new List<TrafficIncident>();
            foreach (var feature in features)
            {
                var coords = feature.Geometry?.Coordinates;
                var props = feature.Properties;
                if (coords is not { Count: >= 2 } || props is null) continue;

                results.Add(new TrafficIncident(
                    Id: $"nddot-event-{feature.Id}",
                    RoadwayName: props.HwyDesc ?? "Unknown roadway",
                    Description: props.Comment ?? "",
                    EventType: props.ConditionDesc ?? "unknown",
                    Severity: props.DelayTimeDesc ?? "unknown",
                    Latitude: coords[1],
                    Longitude: coords[0],
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://travel.dot.nd.gov"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed to fetch NDDOT traffic incidents.");
            return [];
        }
    }

    private sealed record NdAlertFeature(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("properties")] NdAlertProperties? Properties,
        [property: JsonPropertyName("geometry")] GeoJsonGeometry? Geometry);

    private sealed record GeoJsonGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record NdAlertProperties(
        [property: JsonPropertyName("HwyDesc")] string? HwyDesc,
        [property: JsonPropertyName("Comment")] string? Comment,
        [property: JsonPropertyName("ConditionDesc")] string? ConditionDesc,
        [property: JsonPropertyName("DelayTimeDesc")] string? DelayTimeDesc);
}
