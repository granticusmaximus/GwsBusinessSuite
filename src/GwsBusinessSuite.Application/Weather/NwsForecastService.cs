using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Weather;

// Overwatch Grid's weather-on-zoom panel. Free, no API key (same api.weather.gov origin and
// User-Agent requirement as NwsAlertsService - see its own comment for why this has to be
// server-side, not a client-side fetch()). NWS's own three-step lookup: a lat/lon resolves to a
// forecast office grid cell via /points, which hands back both a forecast URL and an
// observationStations URL - the latter is a ranked list of real weather stations near that
// point, whose nearest one's own /observations/latest gives real current conditions (the
// forecast endpoint alone only ever gives forecast periods, never "what it's doing right now").
public sealed class NwsForecastService(HttpClient httpClient, IMemoryCache cache, ILogger<NwsForecastService> logger) : INwsForecastService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public async Task<WeatherSnapshot?> GetSnapshotAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        // Rounded to ~1km - weather doesn't vary meaningfully at finer resolution than this, and
        // it keeps a small pan/zoom from re-querying NWS on every single settle.
        var cacheKey = $"weather:nws:snapshot:{latitude.ToString("F2", CultureInfo.InvariantCulture)},{longitude.ToString("F2", CultureInfo.InvariantCulture)}";
        if (cache.TryGetValue(cacheKey, out WeatherSnapshot? cached))
        {
            return cached;
        }

        var snapshot = await FetchSnapshotAsync(latitude, longitude, cancellationToken);
        if (snapshot is not null)
        {
            cache.Set(cacheKey, snapshot, CacheDuration);
        }
        return snapshot;
    }

    private async Task<WeatherSnapshot?> FetchSnapshotAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        try
        {
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lon = longitude.ToString(CultureInfo.InvariantCulture);
            var pointResponse = await httpClient.GetAsync($"points/{lat},{lon}", cancellationToken);
            pointResponse.EnsureSuccessStatusCode();
            var pointRoot = JsonNode.Parse(await pointResponse.Content.ReadAsStringAsync(cancellationToken));
            var properties = pointRoot?["properties"];
            var forecastUrl = properties?["forecast"]?.GetValue<string>();
            var stationsUrl = properties?["observationStations"]?.GetValue<string>();
            if (forecastUrl is null)
            {
                return null;
            }

            var forecastResponse = await httpClient.GetAsync(forecastUrl, cancellationToken);
            forecastResponse.EnsureSuccessStatusCode();
            var forecastRoot = JsonNode.Parse(await forecastResponse.Content.ReadAsStringAsync(cancellationToken));
            var period = forecastRoot?["properties"]?["periods"]?.AsArray().FirstOrDefault();
            if (period is null)
            {
                return null;
            }

            var (currentTemperature, currentConditions) = stationsUrl is null
                ? (null, null)
                : await TryFetchCurrentConditionsAsync(stationsUrl, cancellationToken);

            return new WeatherSnapshot(
                CurrentTemperatureFahrenheit: currentTemperature,
                CurrentConditions: currentConditions,
                ForecastTemperatureFahrenheit: period["temperature"]!.GetValue<double>(),
                ShortForecast: period["shortForecast"]?.GetValue<string>() ?? "",
                DetailedForecast: period["detailedForecast"]?.GetValue<string>() ?? "",
                WindSpeed: period["windSpeed"]?.GetValue<string>() ?? "",
                WindDirection: period["windDirection"]?.GetValue<string>() ?? "",
                ChanceOfPrecipitationPercent: period["probabilityOfPrecipitation"]?["value"]?.GetValue<int?>());
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "NWS forecast lookup failed for {Latitude},{Longitude}.", latitude, longitude);
            return null;
        }
    }

    // A missing/stale observation station is routine (NWS stations do go offline) and should
    // never sink the whole snapshot - the forecast half is always far more reliably available.
    private async Task<(double? Temperature, string? Conditions)> TryFetchCurrentConditionsAsync(string stationsUrl, CancellationToken cancellationToken)
    {
        try
        {
            var stationsResponse = await httpClient.GetAsync(stationsUrl, cancellationToken);
            stationsResponse.EnsureSuccessStatusCode();
            var stationsRoot = JsonNode.Parse(await stationsResponse.Content.ReadAsStringAsync(cancellationToken));
            var stationId = stationsRoot?["features"]?.AsArray().FirstOrDefault()?["properties"]?["stationIdentifier"]?.GetValue<string>();
            if (stationId is null)
            {
                return (null, null);
            }

            var obsResponse = await httpClient.GetAsync($"stations/{stationId}/observations/latest", cancellationToken);
            obsResponse.EnsureSuccessStatusCode();
            var obsRoot = JsonNode.Parse(await obsResponse.Content.ReadAsStringAsync(cancellationToken));
            var obsProperties = obsRoot?["properties"];
            var celsius = obsProperties?["temperature"]?["value"]?.GetValue<double?>();
            var conditions = obsProperties?["textDescription"]?.GetValue<string>();
            var fahrenheit = celsius.HasValue ? celsius.Value * 9.0 / 5.0 + 32.0 : (double?)null;
            return (fahrenheit, conditions);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "NWS current-observations lookup failed for station list {StationsUrl}.", stationsUrl);
            return (null, null);
        }
    }
}
