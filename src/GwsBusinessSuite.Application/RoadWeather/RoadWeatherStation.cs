using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.RoadWeather;

public static class RoadIceRisk
{
    public const string IceLikely = "ice-likely";
    public const string NearFreezing = "near-freezing";
    public const string Clear = "clear";
}

// A roadside weather station (RWIS) reading. PavementF is the road surface temperature where the
// station measures it; Risk/Reason are RoadIceRiskClassifier's verdict on the reading.
public sealed record RoadWeatherStation(
    string Id,
    string Name,
    double Latitude,
    double Longitude,
    double? PavementF,
    double? AirF,
    double? DewpointF,
    string? Precipitation,
    DateTimeOffset? ObservedUtc,
    string Risk,
    string Reason,
    string SourceName,
    string SourceAttributionUrl);

public interface IRoadWeatherProvider
{
    string SourceName { get; }

    BoundingBox Coverage { get; }

    Task<IReadOnlyList<RoadWeatherStation>> GetStationsAsync(CancellationToken cancellationToken = default);
}

// Ice risk from a single reading. Water freezes on a road at or below 32 F; it needs water to get
// there - falling precipitation, or frost when the surface is at or below the dew point. The
// pavement's own temperature is what matters (a bridge deck can be below freezing at 36 F air),
// so air temperature is only the fallback for stations without a pavement sensor, and the reason
// says which one was used.
public static class RoadIceRiskClassifier
{
    public const double FreezingF = 32;
    public const double NearFreezingF = 35;

    public static (string Risk, string Reason) Classify(double? pavementF, double? airF, double? dewpointF, string? precipitation)
    {
        var (temperature, what) = pavementF is { } p ? (p, "pavement") : airF is { } a ? (a, "air") : (double.NaN, "");
        if (double.IsNaN(temperature)) return (RoadIceRisk.Clear, "No temperature reported.");

        var wet = IsFalling(precipitation);
        var frost = dewpointF is { } dew && temperature <= dew;
        if (temperature <= FreezingF && (wet || frost))
        {
            return (RoadIceRisk.IceLikely, $"{what} {temperature:0}°F with {(wet ? precipitation!.ToLowerInvariant() : "frost conditions (at or below the dew point)")}");
        }
        if (temperature <= NearFreezingF)
        {
            return (RoadIceRisk.NearFreezing, $"{what} {temperature:0}°F - near freezing");
        }
        return (RoadIceRisk.Clear, $"{what} {temperature:0}°F");
    }

    private static bool IsFalling(string? precipitation) =>
        !string.IsNullOrWhiteSpace(precipitation)
        && !precipitation.Equals("None", StringComparison.OrdinalIgnoreCase)
        && !precipitation.Equals("No Precipitation", StringComparison.OrdinalIgnoreCase);
}

public sealed class RoadWeatherDirectoryService(IEnumerable<IRoadWeatherProvider> providers, ILogger<RoadWeatherDirectoryService> logger)
{
    public async Task<IReadOnlyList<RoadWeatherStation>> GetStationsInBoundingBoxAsync(BoundingBox bbox, CancellationToken cancellationToken = default)
    {
        var relevant = providers.Where(p => p.Coverage.South <= bbox.North && p.Coverage.North >= bbox.South
                                            && p.Coverage.West <= bbox.East && p.Coverage.East >= bbox.West).ToList();
        var results = await Task.WhenAll(relevant.Select(async provider =>
        {
            try
            {
                return await provider.GetStationsAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Road weather source {Source} failed.", provider.SourceName);
                return (IReadOnlyList<RoadWeatherStation>)[];
            }
        }));
        return results.SelectMany(r => r).Where(s => bbox.Contains(s.Latitude, s.Longitude)).ToList();
    }
}
