namespace GwsBusinessSuite.Application.Weather;

// Powers Overwatch's weather-on-zoom panel. CurrentTemperatureFahrenheit/CurrentConditions
// come from the nearest live observation station (may be null - stations occasionally go
// stale/offline); the rest always comes from NWS's own gridpoint forecast, which is far more
// reliably available than any single station's live sensor feed.
public sealed record WeatherSnapshot(
    double? CurrentTemperatureFahrenheit,
    string? CurrentConditions,
    double ForecastTemperatureFahrenheit,
    string ShortForecast,
    string DetailedForecast,
    string WindSpeed,
    string WindDirection,
    int? ChanceOfPrecipitationPercent);
