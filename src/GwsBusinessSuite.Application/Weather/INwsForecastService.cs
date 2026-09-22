namespace GwsBusinessSuite.Application.Weather;

public interface INwsForecastService
{
    Task<WeatherSnapshot?> GetSnapshotAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
}
