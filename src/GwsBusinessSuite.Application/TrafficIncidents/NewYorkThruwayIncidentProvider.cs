using System.Globalization;
using System.Xml.Linq;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.TrafficIncidents;

// The NY State Thruway Authority's own Developers Resource Center events feed - genuinely free
// with no API key, confirmed directly, the page itself explicitly states "a FREE service."
// Covers only the Thruway's own roads (I-90/I-87/I-190/I-287 and related), not New York state as
// a whole - statewide NY incidents/cameras remain behind 511NY's key-gated API, not built here.
// Published as plain XML with every &lt;event&gt; a self-closing element carrying its data as
// attributes rather than child elements - the simplest XML shape of any provider in this project.
public sealed class NewYorkThruwayIncidentProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NewYorkThruwayIncidentProvider> logger) : ITrafficIncidentProvider
{
    private const string CacheKey = "traffic-incidents:ny-thruway:all";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(3);

    public string SourceName => "NY Thruway";

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
            var xml = await httpClient.GetStringAsync("api/data/events/xml", cancellationToken);
            var doc = XDocument.Parse(xml);
            var results = new List<TrafficIncident>();

            foreach (var eventEl in doc.Descendants("event"))
            {
                var eventId = (string?)eventEl.Attribute("eventid");
                var latText = (string?)eventEl.Attribute("latitude");
                var lonText = (string?)eventEl.Attribute("longitude");
                if (string.IsNullOrWhiteSpace(eventId)) continue;
                if (!double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)) continue;
                if (!double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;

                results.Add(new TrafficIncident(
                    Id: $"ny-thruway-{eventId}",
                    RoadwayName: (string?)eventEl.Attribute("route") ?? "Unknown roadway",
                    Description: (string?)eventEl.Attribute("eventdesc") ?? "",
                    EventType: (string?)eventEl.Attribute("eventtype") ?? "unknown",
                    Severity: (string?)eventEl.Attribute("category") ?? "unknown",
                    Latitude: lat,
                    Longitude: lon,
                    SourceName: SourceName,
                    SourceAttributionUrl: "https://www.thruway.ny.gov"));
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            logger.LogWarning(ex, "Failed to fetch NY Thruway traffic incidents.");
            return [];
        }
    }
}
