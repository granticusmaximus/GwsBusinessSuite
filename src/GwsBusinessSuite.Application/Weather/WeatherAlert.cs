namespace GwsBusinessSuite.Application.Weather;

// A single active NWS alert and the areas to draw: its own polygon, or (since 13-11-26) for an
// alert issued by zone, its zones' outlines from api.weather.gov/zones (see NwsAlertsService).
public sealed record WeatherAlert(
    string Id,
    string Event,
    string Severity,
    string AreaDescription,
    string Expires,
    IReadOnlyList<IReadOnlyList<(double Longitude, double Latitude)>> Rings);
