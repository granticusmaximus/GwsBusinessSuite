namespace GwsBusinessSuite.Application.Weather;

// A single active NWS severe weather alert with a real polygon to draw - alerts issued against
// UGC zone codes only (no precise polygon) are filtered out upstream (see NwsAlertsService)
// since there's nothing to render for them without a separate zone-to-shape lookup.
public sealed record WeatherAlert(
    string Id,
    string Event,
    string Severity,
    string AreaDescription,
    string Expires,
    IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>> Rings);
