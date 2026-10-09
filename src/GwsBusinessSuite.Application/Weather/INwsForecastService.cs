namespace GwsBusinessSuite.Application.Weather;

public interface INwsForecastService
{
    Task<WeatherSnapshot?> GetSnapshotAsync(double latitude, double longitude, CancellationToken cancellationToken = default);

    // NWS's hourly forecast for the point (about 6 days of hours), oldest first. Empty when NWS
    // has no forecast there (outside the US) or the lookup fails.
    Task<IReadOnlyList<HourlyForecastPeriod>> GetHourlyAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}

public sealed record HourlyForecastPeriod(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double TemperatureFahrenheit,
    int? ChanceOfPrecipitationPercent,
    string ShortForecast,
    string WindSpeed,
    string WindDirection);
