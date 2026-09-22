namespace GwsBusinessSuite.Application.TrafficIncidents;

// EventType is one of the source's own raw category strings (e.g. "accidentsAndIncidents",
// "roadwork", "closures", "specialEvents") - passed through as-is rather than mapped to an enum,
// since different state DOT sources use their own category vocabularies and a shared enum would
// either lose information or need constant expansion as more sources are added.
public sealed record TrafficIncident(
    string Id,
    string RoadwayName,
    string Description,
    string EventType,
    string Severity,
    double Latitude,
    double Longitude,
    string SourceName,
    string SourceAttributionUrl);
