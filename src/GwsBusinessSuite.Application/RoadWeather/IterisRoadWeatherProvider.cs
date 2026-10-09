using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RoadWeather;

// Road weather stations from an Iteris ATIS 511 state's public RWIS layer - the same
// icons.rwis.geojson the IterisAtisCameraProvider reads for cameras. Verified live 2026-10-09:
// Montana 118 stations, 90 with a pavement temperature, readings minutes old; South Dakota 135
// stations with air temperature/dew point/precipitation but no pavement sensor values. The two
// states encode the same fields differently (Montana numbers, South Dakota strings), so values are
// read leniently. Surface condition text was null everywhere on that date, so it isn't relied on.
public sealed class IterisRoadWeatherProvider(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<IterisRoadWeatherProvider> logger,
    string stateCode,
    string sourceName,
    string sourceAttributionUrl,
    BoundingBox coverage) : IRoadWeatherProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);
    // A reading older than this says nothing about the road now.
    private static readonly TimeSpan MaxReadingAge = TimeSpan.FromHours(3);

    public string SourceName => sourceName;

    public BoundingBox Coverage => coverage;

    public async Task<IReadOnlyList<RoadWeatherStation>> GetStationsAsync(CancellationToken cancellationToken = default)
    {
        var cacheKey = $"road-weather:iteris:{stateCode}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<RoadWeatherStation>? cached) && cached is not null) return cached;

        try
        {
            var collection = await httpClient.GetFromJsonAsync<StationCollection>(
                $"https://{stateCode}.cdn.iteris-atis.com/geojson/icons/metadata/icons.rwis.geojson", cancellationToken);
            var freshAfter = DateTimeOffset.UtcNow - MaxReadingAge;
            var stations = new List<RoadWeatherStation>();
            foreach (var feature in collection?.Features ?? [])
            {
                if (feature.Geometry?.Coordinates is not { Count: >= 2 } coordinates || feature.Properties is not { } p) continue;
                var atmos = p.Atmos?.FirstOrDefault();
                // Several surface sensors (lanes) at one station: the coldest one is the one that ices.
                var pavement = (p.Surface ?? []).Select(s => Number(s.SurfaceTemperature)).Where(t => t is not null).Min();
                var air = Number(atmos?.AirTemperature);
                var dewpoint = Number(atmos?.DewpointTemperature);
                var precipitation = Text(atmos?.PrecipType) is { } type && !type.Equals("None", StringComparison.OrdinalIgnoreCase)
                                    && !type.Equals("No Precipitation", StringComparison.OrdinalIgnoreCase)
                    ? type
                    : Text(atmos?.PrecipIntensity);
                var observed = Unix(atmos?.ObservationTime) ?? (p.Surface ?? []).Select(s => Unix(s.ObservationTime)).Max();
                if (pavement is null && air is null) continue;
                if (observed is { } at && at < freshAfter) continue;

                var (risk, reason) = RoadIceRiskClassifier.Classify(pavement, air, dewpoint, precipitation);
                stations.Add(new RoadWeatherStation(
                    Id: $"rwis-{stateCode}-{feature.Id}",
                    Name: string.IsNullOrWhiteSpace(p.Name) ? $"{sourceName} station {feature.Id}" : p.Name.Trim(),
                    Latitude: coordinates[1],
                    Longitude: coordinates[0],
                    PavementF: pavement,
                    AirF: air,
                    DewpointF: dewpoint,
                    Precipitation: precipitation,
                    ObservedUtc: observed,
                    Risk: risk,
                    Reason: reason,
                    SourceName: sourceName,
                    SourceAttributionUrl: sourceAttributionUrl));
            }
            if (stations.Count > 0) cache.Set(cacheKey, (IReadOnlyList<RoadWeatherStation>)stations, CacheDuration);
            return stations;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch {Source} road weather stations.", sourceName);
            return [];
        }
    }

    private static double? Number(Reading? reading) => reading?.Value switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s when double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => null
    };

    private static string? Text(Reading? reading) =>
        reading?.Value is { ValueKind: JsonValueKind.String } s && !string.IsNullOrWhiteSpace(s.GetString()) ? s.GetString()!.Trim() : null;

    private static DateTimeOffset? Unix(Reading? reading) =>
        Number(reading) is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;

    private sealed record StationCollection([property: JsonPropertyName("features")] List<StationFeature>? Features);

    private sealed record StationFeature(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("geometry")] StationGeometry? Geometry,
        [property: JsonPropertyName("properties")] StationProperties? Properties);

    private sealed record StationGeometry([property: JsonPropertyName("coordinates")] List<double>? Coordinates);

    private sealed record StationProperties(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("atmos")] List<AtmosReading>? Atmos,
        [property: JsonPropertyName("surface")] List<SurfaceReading>? Surface);

    private sealed record AtmosReading(
        [property: JsonPropertyName("air_temperature")] Reading? AirTemperature,
        [property: JsonPropertyName("dewpoint_temperature")] Reading? DewpointTemperature,
        [property: JsonPropertyName("precip_type")] Reading? PrecipType,
        [property: JsonPropertyName("precip_intensity")] Reading? PrecipIntensity,
        [property: JsonPropertyName("observation_time")] Reading? ObservationTime);

    private sealed record SurfaceReading(
        [property: JsonPropertyName("surface_temperature")] Reading? SurfaceTemperature,
        [property: JsonPropertyName("observation_time")] Reading? ObservationTime);

    private sealed record Reading([property: JsonPropertyName("value")] JsonElement Value);
}
