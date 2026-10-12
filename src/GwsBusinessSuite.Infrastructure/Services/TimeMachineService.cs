using System.Collections.Concurrent;
using System.Globalization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Buffers incident sightings from every incident lookup; IncidentHistoryFlushService writes them.
// Only crashes, closures and hazards are kept - roadwork and events run for days and number in
// the thousands, and the time machine is about what happened, not what's scheduled.
public sealed class IncidentHistoryRecorder(TimeProvider timeProvider) : IIncidentHistoryRecorder
{
    private static readonly HashSet<string> KeptCategories =
        [IncidentCategories.Crash, IncidentCategories.Closure, IncidentCategories.Hazard];
    private const int MaxPending = 20_000;
    private readonly ConcurrentDictionary<string, (TrafficIncident Incident, long SeenUnix)> _pending = new();

    public void Record(IEnumerable<TrafficIncident> incidents)
    {
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        foreach (var incident in incidents)
        {
            if (!KeptCategories.Contains(incident.Category) || _pending.Count >= MaxPending) continue;
            _pending[KeyOf(incident)] = (incident, now);
        }
    }

    public static string KeyOf(TrafficIncident incident) => $"{incident.SourceName}:{incident.Id}";

    public IReadOnlyList<(string Key, TrafficIncident Incident, long SeenUnix)> Drain()
    {
        var drained = new List<(string, TrafficIncident, long)>();
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var value)) drained.Add((key, value.Incident, value.SeenUnix));
        }
        return drained;
    }
}

public sealed class IncidentHistoryFlushService(
    IServiceScopeFactory scopeFactory,
    IncidentHistoryRecorder recorder,
    TimeProvider timeProvider,
    ILogger<IncidentHistoryFlushService> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await FlushAsync(scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>(), recorder, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Incident history flush failed");
            }
        }
    }

    // Upserts the buffered sightings (extending LastSeenUnix) and drops anything older than Retention.
    public static async Task FlushAsync(IAppDbContextFactory factory, IncidentHistoryRecorder recorder, TimeProvider timeProvider, CancellationToken ct)
    {
        var batch = recorder.Drain();
        await using var db = await factory.CreateDbContextAsync(ct);
        if (batch.Count > 0)
        {
            var keys = batch.Select(item => item.Key).ToList();
            var existing = new Dictionary<string, OverwatchIncidentSighting>();
            foreach (var chunk in keys.Chunk(500))
            {
                foreach (var row in await db.OverwatchIncidentSightings.Where(s => chunk.Contains(s.Key)).ToListAsync(ct))
                    existing[row.Key] = row;
            }
            foreach (var (key, incident, seen) in batch)
            {
                if (existing.TryGetValue(key, out var row))
                {
                    row.LastSeenUnix = Math.Max(row.LastSeenUnix, seen);
                    row.Description = Limit(incident.Description, 500);
                    row.SeverityLevel = incident.SeverityLevel;
                    continue;
                }
                await db.OverwatchIncidentSightings.AddAsync(new OverwatchIncidentSighting
                {
                    Key = key, Category = incident.Category, SeverityLevel = incident.SeverityLevel,
                    RoadwayName = Limit(incident.RoadwayName, 200), Description = Limit(incident.Description, 500),
                    SourceName = incident.SourceName, Latitude = incident.Latitude, Longitude = incident.Longitude,
                    FirstSeenUnix = seen, LastSeenUnix = seen, CreatedBy = "incident-history"
                }, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        var cutoff = timeProvider.GetUtcNow().Subtract(Retention).ToUnixTimeSeconds();
        await db.OverwatchIncidentSightings.Where(s => s.LastSeenUnix < cutoff).ExecuteDeleteAsync(ct);
    }

    private static string Limit(string? text, int max) => (text ?? string.Empty).Length <= max ? text ?? string.Empty : text![..max];
}

public sealed record TimeMachineCamera(string Id, string Name, double Latitude, double Longitude, string FrameUrl, DateTimeOffset CapturedAt);

public sealed record TimeMachineSnapshot(
    DateTimeOffset At,
    string RadarTileUrl,
    IReadOnlyList<OverwatchIncidentSighting> Incidents,
    IReadOnlyList<HazardFeature> Earthquakes,
    IReadOnlyList<TimeMachineCamera> Cameras,
    DateTimeOffset? IncidentHistoryStartsAt);

// Overwatch's time machine: what the current view looked like at a moment in the past 24 hours.
//   - radar: the Iowa Environmental Mesonet's archived national mosaic for that 5-minute slot
//     (ridge::USCOMP-N0Q-yyyyMMddHHmm, verified live 2026-10-11);
//   - incidents: crashes/closures/hazards Overwatch saw active then (from when recording began);
//   - earthquakes: USGS's past-day feed, those that had happened by then;
//   - cameras: each snapshot favorite's saved time-lapse frame nearest that moment.
public sealed class TimeMachineService(
    IAppDbContextFactory dbContextFactory,
    HazardLayerService hazards,
    ICameraFavoritesService favorites,
    CameraTimelapseStore frames,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    // A sighting counts as still active this long after it was last seen (lookups are ~10 min apart).
    private static readonly TimeSpan SightingGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FrameTolerance = TimeSpan.FromMinutes(20);
    private const int MaxIncidents = 500;

    public static string RadarTileUrl(DateTimeOffset at, DateTimeOffset now)
    {
        // The newest few minutes aren't archived yet; use the live mosaic for them.
        if (now - at < TimeSpan.FromMinutes(10)) return $"{RadarTimelineService.TileBaseUrl}nexrad-n0q-900913/{{z}}/{{x}}/{{y}}.png";
        var utc = at.UtcDateTime;
        var slot = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 5 * 5, 0, DateTimeKind.Utc);
        return $"{RadarTimelineService.TileBaseUrl}ridge::USCOMP-N0Q-{slot.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}/{{z}}/{{x}}/{{y}}.png";
    }

    public async Task<TimeMachineSnapshot> GetAsync(string username, BoundingBox bounds, DateTimeOffset at, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        if (at > now) at = now;
        if (at < now - Window) at = now - Window;
        var atUnix = at.ToUnixTimeSeconds();
        var graceUnix = atUnix - (long)SightingGrace.TotalSeconds;

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var active = await db.OverwatchIncidentSightings.AsNoTracking()
            .Where(s => s.FirstSeenUnix <= atUnix && s.LastSeenUnix >= graceUnix)
            .ToListAsync(ct);
        var inView = active.Where(s => bounds.Contains(s.Latitude, s.Longitude)).Take(MaxIncidents).ToList();
        long? earliest = await db.OverwatchIncidentSightings.AsNoTracking().AnyAsync(ct)
            ? await db.OverwatchIncidentSightings.AsNoTracking().MinAsync(s => s.FirstSeenUnix, ct)
            : null;

        var quakes = (await hazards.GetHazardsAsync(bounds, ct)).Features
            .Where(f => f.Kind == "earthquake" && f.ObservedAt is { } when && when <= at && when >= now - Window)
            .ToList();

        var cameras = new List<TimeMachineCamera>();
        foreach (var camera in await favorites.GetFavoritesAsync(username, ct))
        {
            if (camera.StreamKind != CameraStreamKind.Snapshot) continue;
            var nearest = frames.ListFrames(camera.Id).MinBy(frame => Math.Abs((frame.CapturedAt - at).TotalSeconds));
            if (nearest is null || (nearest.CapturedAt - at).Duration() > FrameTolerance) continue;
            cameras.Add(new TimeMachineCamera(camera.Id, camera.Name, camera.Latitude, camera.Longitude,
                $"/admin/api/overwatch/timelapse/{Uri.EscapeDataString(camera.Id)}/{Uri.EscapeDataString(nearest.FileName)}", nearest.CapturedAt));
        }

        return new TimeMachineSnapshot(at, RadarTileUrl(at, now), inView, quakes, cameras,
            earliest is { } first ? DateTimeOffset.FromUnixTimeSeconds(first) : null);
    }
}
