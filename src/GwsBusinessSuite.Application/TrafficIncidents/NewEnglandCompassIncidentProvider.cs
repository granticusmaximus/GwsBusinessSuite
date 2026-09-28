using System.Globalization;
using System.Xml.Linq;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// Maine/New Hampshire/Vermont's shared "Compass" ITS C2C XML Data Portal - genuinely free with no
// API key anywhere in the flow, confirmed directly for all three states (this is a distinct,
// unauthenticated product from newengland511.org's own separate, key-gated developer API on the
// same site - easy to conflate, but they are different endpoints). One class, parameterized per
// state (matching Cars511IncidentProvider's own precedent), since the schema and portal are
// identical across all three - only the `networks` query value and attribution differ.
// lat/lon are published as plain integers scaled by 1,000,000 (e.g. "43513494" means
// 43.513494) - confirmed live, divided back out here. This portal also serves camera location/
// status data (cctvStatusData) but no confirmed public image-delivery mechanism was found for
// it, so only incidents are built from this source - see the Overwatch master plan for that
// documented gap.
public sealed class NewEnglandCompassIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NewEnglandCompassIncidentProvider> logger,
    string network,
    string sourceName,
    string sourceAttributionUrl) : ITrafficIncidentProvider
{
    private static readonly XNamespace C2C = "http://its.gov/c2c_icd";
    private const double CoordinateScale = 1_000_000d;

    private readonly string cacheKey = $"traffic-incidents:necompass:{network}";
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
            var url = $"NEC.XmlDataPortal/api/c2c?networks={network}&dataTypes=incidentData";
            var xml = await httpClient.GetStringAsync(url, cancellationToken);
            var doc = XDocument.Parse(xml);
            var results = new List<TrafficIncident>();

            foreach (var incidentEl in doc.Descendants(C2C + "incident"))
            {
                var id = (string?)incidentEl.Attribute("id");
                var startLocation = incidentEl.Element(C2C + "startLocation");
                var latText = startLocation?.Element(C2C + "lat")?.Value;
                var lonText = startLocation?.Element(C2C + "lon")?.Value;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!double.TryParse(latText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var latRaw)) continue;
                if (!double.TryParse(lonText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lonRaw)) continue;

                results.Add(new TrafficIncident(
                    Id: $"necompass-{network}-{id}",
                    RoadwayName: startLocation?.Element(C2C + "roadway")?.Value ?? "Unknown roadway",
                    Description: incidentEl.Element(C2C + "desc")?.Value?.Trim() ?? "",
                    EventType: incidentEl.Element(C2C + "eventType")?.Value ?? "unknown",
                    Severity: incidentEl.Element(C2C + "severity")?.Value ?? "unknown",
                    Latitude: latRaw / CoordinateScale,
                    Longitude: lonRaw / CoordinateScale,
                    SourceName: sourceName,
                    SourceAttributionUrl: sourceAttributionUrl));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            logger.LogWarning(ex, "Failed to fetch New England Compass ({Network}) traffic incidents.", network);
            return [];
        }
    }
}
