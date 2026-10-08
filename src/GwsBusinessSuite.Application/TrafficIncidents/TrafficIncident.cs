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
    string SourceAttributionUrl,
    // Normalized across sources by IncidentNormalizer (filled in by TrafficIncidentDirectoryService
    // when a provider leaves them empty): one of IncidentCategories / IncidentSeverityLevels.
    string Category = "",
    string SeverityLevel = "",
    // Plain-language lane impact ("Some lanes closed", "Right lane blocked") when known.
    string? Lanes = null,
    // When the source says the incident or work is expected to end.
    DateTimeOffset? EndsAt = null);
