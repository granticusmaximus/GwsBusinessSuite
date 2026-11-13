using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Weather;

public enum RadarFrameKind
{
    // A real NEXRAD scan (the national base-reflectivity mosaic).
    Observed,

    // Simulated reflectivity from NOAA's HRRR model - what the radar is forecast to look like.
    Forecast
}

// TileUrlTemplate is an XYZ (Web Mercator) template with {z}/{x}/{y} placeholders: the sharp US
// layer (NEXRAD / HRRR). GlobalTileUrlTemplate, on observed frames, is the whole planet's radar for
// the nearest time (RainViewer, 12-11-26), drawn underneath it; its tiles stop at GlobalMaxZoom and
// are stretched beyond. Forecast frames have none: no free source forecasts radar worldwide.
public sealed record RadarFrame(DateTimeOffset ValidUtc, RadarFrameKind Kind, string TileUrlTemplate, string? GlobalTileUrlTemplate = null)
{
    public const int GlobalMaxZoom = 7;
}

// One RainViewer frame: its time and tile template.
public sealed record GlobalRadarFrame(DateTimeOffset ValidUtc, string TileUrlTemplate);

// NowIndex is the newest observed frame - the timeline opens there, with the past to its left and
// the forecast to its right. ModelRunUtc is the HRRR run the forecast frames come from (null when
// the forecast couldn't be loaded and only observed frames are available).
public sealed record RadarTimeline(IReadOnlyList<RadarFrame> Frames, int NowIndex, DateTimeOffset? ModelRunUtc);

public interface IRadarTimelineService
{
    Task<RadarTimeline> GetTimelineAsync(CancellationToken cancellationToken = default);
}

// Overwatch's "future radar" timeline. Both halves come from the Iowa Environmental Mesonet's
// public tile cache (mesonet.agron.iastate.edu) - free, no key, CORS-open, verified 2026-10-09:
//   - Observed: nexrad-n0q-900913 (latest national mosaic) and -m05m ... -m55m (that many
//     minutes before it). USCOMP/n0q_0.json gives the latest scan's real valid time.
//   - Forecast: hrrr::REFD-F{minute}-0, the latest HRRR run's simulated reflectivity every 15
//     minutes out to 18 hours. hrrr/refd_0000.json gives that run's start time; the run is
//     usually 2-3 hours old when it appears, so frames whose valid time has already passed are
//     dropped and the rest are labelled by valid time, not by forecast hour.
// Forecast frames are every 15 minutes for the first 3 hours (where they are most reliable and
// most watched), then hourly - the same shape weather apps use for future radar.
public sealed class RadarTimelineService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<RadarTimelineService> logger) : IRadarTimelineService
{
    public const string TileBaseUrl = "https://mesonet.agron.iastate.edu/cache/tile.py/1.0.0/";
    private const string CacheKey = "weather:radar-timeline";
    // IEM refreshes the mosaic every 5 minutes and its tiles carry max-age=300.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private const int ObservedStepMinutes = 5;
    private const int ObservedFrames = 11; // m05m ... m55m
    private const int MaxForecastMinute = 1080;
    private const int DenseForecastWindowMinutes = 180;

    public async Task<RadarTimeline> GetTimelineAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out RadarTimeline? cached) && cached is not null) return cached;

        var timeline = Build(
            await ReadTimeAsync<RadarScanMetadata>("data/gis/images/4326/USCOMP/n0q_0.json", m => m.Meta?.Valid, cancellationToken),
            await ReadTimeAsync<HrrrMetadata>("data/gis/images/4326/hrrr/refd_0000.json", m => m.ModelInitUtc, cancellationToken),
            DateTimeOffset.UtcNow,
            await ReadGlobalFramesAsync(cancellationToken));
        cache.Set(CacheKey, timeline, CacheDuration);
        return timeline;
    }

    // Pure so it can be tested against fixed times.
    public static RadarTimeline Build(DateTimeOffset? latestScanUtc, DateTimeOffset? modelRunUtc, DateTimeOffset nowUtc,
        IReadOnlyList<GlobalRadarFrame>? globalFrames = null)
    {
        globalFrames ??= [];
        // The world frame nearest each US scan, if one is within 10 minutes (RainViewer is every 10).
        string? GlobalFor(DateTimeOffset valid) => globalFrames
            .Where(frame => (frame.ValidUtc - valid).Duration() <= TimeSpan.FromMinutes(10))
            .OrderBy(frame => (frame.ValidUtc - valid).Duration())
            .FirstOrDefault()?.TileUrlTemplate;

        // If the scan time can't be read, the latest mosaic is at most a few minutes old.
        var latestScan = latestScanUtc ?? FloorToMinutes(nowUtc, ObservedStepMinutes).AddMinutes(-ObservedStepMinutes);

        var frames = new List<RadarFrame>();
        for (var minutesAgo = ObservedFrames * ObservedStepMinutes; minutesAgo >= ObservedStepMinutes; minutesAgo -= ObservedStepMinutes)
        {
            var valid = latestScan.AddMinutes(-minutesAgo);
            frames.Add(new RadarFrame(
                valid,
                RadarFrameKind.Observed,
                $"{TileBaseUrl}nexrad-n0q-900913-m{minutesAgo:00}m/{{z}}/{{x}}/{{y}}.png",
                GlobalFor(valid)));
        }
        frames.Add(new RadarFrame(latestScan, RadarFrameKind.Observed, $"{TileBaseUrl}nexrad-n0q-900913/{{z}}/{{x}}/{{y}}.png", GlobalFor(latestScan)));
        var nowIndex = frames.Count - 1;

        if (modelRunUtc is { } run)
        {
            for (var minute = 15; minute <= MaxForecastMinute; minute += 15)
            {
                var valid = run.AddMinutes(minute);
                if (valid <= latestScan) continue;
                // Hourly beyond the dense window; the dense window is measured from the latest
                // scan so it stays 3 hours long however old the model run is.
                if (valid > latestScan.AddMinutes(DenseForecastWindowMinutes) && minute % 60 != 0) continue;

                frames.Add(new RadarFrame(
                    valid,
                    RadarFrameKind.Forecast,
                    $"{TileBaseUrl}hrrr::REFD-F{minute:0000}-0/{{z}}/{{x}}/{{y}}.png"));
            }
        }

        return new RadarTimeline(frames, nowIndex, modelRunUtc);
    }

    private async Task<DateTimeOffset?> ReadTimeAsync<T>(string path, Func<T, string?> select, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await httpClient.GetFromJsonAsync<T>(path, cancellationToken);
            var text = metadata is null ? null : select(metadata);
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't read radar timing from {Path}.", path);
            return null;
        }
    }

    // RainViewer's public map list (free, no key, verified 12-11-26): every radar frame of the last
    // two hours, every 10 minutes, for the whole planet. Tiles stop at zoom 7 on the free tier.
    public const string GlobalRadarIndexUrl = "https://api.rainviewer.com/public/weather-maps.json";

    private async Task<IReadOnlyList<GlobalRadarFrame>> ReadGlobalFramesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var maps = await httpClient.GetFromJsonAsync<RainViewerMaps>(GlobalRadarIndexUrl, cancellationToken);
            return ParseGlobalFrames(maps?.Host, maps?.Radar?.Past?.Select(frame => (frame.Time, frame.Path)));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Couldn't read the global radar frames.");
            return [];
        }
    }

    public static IReadOnlyList<GlobalRadarFrame> ParseGlobalFrames(string? host, IEnumerable<(long Time, string? Path)>? frames)
    {
        if (string.IsNullOrWhiteSpace(host) || !Uri.TryCreate(host, UriKind.Absolute, out var hostUri) || hostUri.Scheme != Uri.UriSchemeHttps || frames is null) return [];
        return frames
            .Where(frame => frame.Time > 0 && frame.Path is { Length: > 1 } path && path.StartsWith('/') && !path.Contains(".."))
            .Select(frame => new GlobalRadarFrame(
                DateTimeOffset.FromUnixTimeSeconds(frame.Time),
                // 256 px tiles, colour scheme 2, smoothed, snow shown.
                $"{host.TrimEnd('/')}{frame.Path}/256/{{z}}/{{x}}/{{y}}/2/1_1.png"))
            .OrderBy(frame => frame.ValidUtc)
            .ToList();
    }

    private sealed record RainViewerMaps(
        [property: JsonPropertyName("host")] string? Host,
        [property: JsonPropertyName("radar")] RainViewerRadar? Radar);

    private sealed record RainViewerRadar([property: JsonPropertyName("past")] List<RainViewerFrame>? Past);

    private sealed record RainViewerFrame(
        [property: JsonPropertyName("time")] long Time,
        [property: JsonPropertyName("path")] string? Path);

    private static DateTimeOffset FloorToMinutes(DateTimeOffset value, int minutes) =>
        new(value.Ticks - value.Ticks % TimeSpan.FromMinutes(minutes).Ticks, value.Offset);

    private sealed record RadarScanMetadata([property: JsonPropertyName("meta")] RadarScanMeta? Meta);

    private sealed record RadarScanMeta([property: JsonPropertyName("valid")] string? Valid);

    private sealed record HrrrMetadata([property: JsonPropertyName("model_init_utc")] string? ModelInitUtc);
}
