using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// MoDOT's traffic-impact and planned-event point layers. NWSDATA/0 contains cameras,
// not incidents. TravelerInformationMod publishes roadway/type/impact fields for these
// six layers; work-zone layers use a different schema and are not queried here.
public sealed class MissouriDotIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<MissouriDotIncidentProvider> logger) : ITrafficIncidentProvider
{
    private static readonly int[] IncidentLayers = [0, 3, 5, 8, 9, 11];
    private const int PageSize = 1000;
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
        var results = new Dictionary<long, TrafficIncident>();
        foreach (var layer in IncidentLayers)
        {
            try
            {
                var offset = 0;
                while (true)
                {
                    var url = $"arcgis/rest/services/TravelerInformation/TravelerInformationMod/MapServer/{layer}/query" +
                        $"?where=1=1&outFields=DATA_ID,ROUTE,TYPE_CODE,LEVEL_OF_IMPACT_CODE,EXT_COMMENT&outSR=4326&orderByFields=OBJECT_ID&resultOffset={offset}&resultRecordCount={PageSize}&f=json";
                    var response = await httpClient.GetFromJsonAsync<ArcGisFeatureCollection>(url, cancellationToken);
                    if (response?.Error is not null)
                        throw new HttpRequestException($"MoDOT layer {layer} returned an ArcGIS query error.");
                    var features = response?.Features;
                    if (features is null || features.Count == 0) break;

                    foreach (var feature in features)
                    {
                        var attrs = feature.Attributes;
                        var geometry = feature.Geometry;
                        if (geometry is null || attrs?.DataId is not { } id) continue;

                        results[id] = new TrafficIncident(
                            Id: $"modot-event-{id}",
                            RoadwayName: attrs.Route ?? "Unknown roadway",
                            Description: attrs.Comment ?? "",
                            EventType: attrs.TypeCode ?? "unknown",
                            Severity: attrs.Impact ?? "unknown",
                            Latitude: geometry.Y,
                            Longitude: geometry.X,
                            SourceName: SourceName,
                            SourceAttributionUrl: "https://www.modot.org");
                    }

                    if (response!.ExceededTransferLimit != true && features.Count < PageSize) break;
                    offset += features.Count;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Failed to fetch MoDOT traffic incidents from layer {Layer}.", layer);
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
        return results.Values.ToList();
    }

    private sealed record ArcGisFeatureCollection(
        [property: JsonPropertyName("features")] List<ArcGisFeature>? Features,
        [property: JsonPropertyName("exceededTransferLimit")] bool? ExceededTransferLimit,
        [property: JsonPropertyName("error")] JsonElement? Error);

    private sealed record ArcGisFeature(
        [property: JsonPropertyName("attributes")] ModotEventAttributes? Attributes,
        [property: JsonPropertyName("geometry")] ArcGisPoint? Geometry);

    private sealed record ArcGisPoint(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y);

    private sealed record ModotEventAttributes(
        [property: JsonPropertyName("DATA_ID")] long? DataId,
        [property: JsonPropertyName("ROUTE")] string? Route,
        [property: JsonPropertyName("TYPE_CODE")] string? TypeCode,
        [property: JsonPropertyName("LEVEL_OF_IMPACT_CODE")] string? Impact,
        [property: JsonPropertyName("EXT_COMMENT")] string? Comment);
}
