using System.Text.Json;
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

public sealed record OverwatchAreaView(Guid Id, string Name, BoundingBox Bounds, bool WatchIncidents, bool WatchWeather,
    bool WatchHazards, string MinSeverity, DateTimeOffset? LastCheckedAt);

public sealed record OverwatchAreaAlertView(Guid Id, Guid AreaId, string Title, string Message, bool IsRead, DateTimeOffset CreatedAt);

// One thing worth telling someone about, with a stable key so it's only reported once.
// Latitude/Longitude are set for incidents, which can start an incident capture.
public sealed record OverwatchAreaItem(string Key, string Text, string Severity, double? Latitude = null, double? Longitude = null);

// Raised when a check writes an alert, so an open notification bell updates without a reload.
public sealed class OverwatchAreaNotifier
{
    public event Action<string, OverwatchAreaAlertView>? OnAlert;
    public void Raise(string username, OverwatchAreaAlertView alert) => OnAlert?.Invoke(username, alert);
}

public sealed class OverwatchAreaService(
    IAppDbContextFactory dbContextFactory,
    TrafficIncidentDirectoryService incidents,
    INwsAlertsService weatherAlerts,
    HazardLayerService hazards,
    OverwatchAreaNotifier notifier,
    TimeProvider timeProvider,
    ILogger<OverwatchAreaService> logger,
    IncidentCaptureService? captures = null)
{
    public const int MaxAreasPerUser = 20;
    private const int MaxSeenKeys = 2000;
    private const int MaxItemsInMessage = 4;
    // New incidents per check that start a camera capture - a pile-up of closures in one sweep
    // shouldn't fan out into dozens of captures.
    private const int MaxCapturesPerCheck = 3;
    // Roadwork and events are routine and plentiful (thousands nationally); an area alert is for
    // things that change a plan.
    private static readonly HashSet<string> AlertingCategories =
        [IncidentCategories.Crash, IncidentCategories.Closure, IncidentCategories.Hazard];

    public async Task<IReadOnlyList<OverwatchAreaView>> ListAreasAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.OverwatchWatchAreas.AsNoTracking().Where(a => a.Username == username).ToListAsync(ct);
        return rows.OrderBy(a => a.Name).Select(ToView).ToList();
    }

    public async Task<OverwatchAreaView> AddAreaAsync(string username, string name, BoundingBox bounds, bool watchIncidents,
        bool watchWeather, bool watchHazards, string minSeverity, CancellationToken ct = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80) throw new ArgumentException("Give the area a name (up to 80 characters).");
        if (bounds.North - bounds.South > 20 || bounds.East - bounds.West > 30)
            throw new ArgumentException("That view is too large to watch - zoom in to about a few states across first.");
        if (minSeverity is not (OverwatchAreaSeverity.Major or OverwatchAreaSeverity.Moderate or OverwatchAreaSeverity.Any))
            minSeverity = OverwatchAreaSeverity.Moderate;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        if (await db.OverwatchWatchAreas.CountAsync(a => a.Username == username, ct) >= MaxAreasPerUser)
            throw new ArgumentException($"You can watch up to {MaxAreasPerUser} areas.");
        var area = new OverwatchWatchArea
        {
            Username = username, Name = name, North = bounds.North, South = bounds.South, East = bounds.East, West = bounds.West,
            WatchIncidents = watchIncidents, WatchWeather = watchWeather, WatchHazards = watchHazards, MinSeverity = minSeverity,
            CreatedBy = username, CreatedAt = timeProvider.GetUtcNow()
        };
        db.OverwatchWatchAreas.Add(area);
        await db.SaveChangesAsync(ct);
        return ToView(area);
    }

    public async Task RemoveAreaAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.OverwatchWatchAreas.Where(a => a.Id == id && a.Username == username).ExecuteDeleteAsync(ct);
    }

    public async Task<OverwatchAreaView?> GetAreaAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var area = await db.OverwatchWatchAreas.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Username == username, ct);
        return area is null ? null : ToView(area);
    }

    // ---- Alerts (notification bell) ----

    public async Task<int> CountUnreadAlertsAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.OverwatchAreaAlerts.CountAsync(a => a.Username == username && a.ReadAt == null, ct);
    }

    public async Task<IReadOnlyList<OverwatchAreaAlertView>> ListAlertsAsync(string username, int take = 10, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.OverwatchAreaAlerts.AsNoTracking().Where(a => a.Username == username).ToListAsync(ct);
        return rows.OrderByDescending(a => a.CreatedAt).Take(take)
            .Select(a => new OverwatchAreaAlertView(a.Id, a.AreaId, a.Title, a.Message, a.ReadAt is not null, a.CreatedAt)).ToList();
    }

    public async Task MarkAlertReadAsync(string username, Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var alert = await db.OverwatchAreaAlerts.FirstOrDefaultAsync(a => a.Id == id && a.Username == username, ct);
        if (alert is null || alert.ReadAt is not null) return;
        alert.ReadAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkAllAlertsReadAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var unread = await db.OverwatchAreaAlerts.Where(a => a.Username == username && a.ReadAt == null).ToListAsync(ct);
        var now = timeProvider.GetUtcNow();
        unread.ForEach(a => a.ReadAt = now);
        await db.SaveChangesAsync(ct);
    }

    // ---- Checking ----

    public async Task<int> CheckAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var areas = await db.OverwatchWatchAreas.ToListAsync(ct);
        var written = new List<OverwatchAreaAlert>();
        foreach (var area in areas)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await CheckAreaAsync(db, area, ct) is { } alert) written.Add(alert);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Overwatch area check failed for {Area}", area.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        // Only after the rows exist, so a click on the live bell entry can mark it read.
        foreach (var alert in written)
            notifier.Raise(alert.Username, new OverwatchAreaAlertView(alert.Id, alert.AreaId, alert.Title, alert.Message, false, alert.CreatedAt));
        return written.Count;
    }

    private async Task<OverwatchAreaAlert?> CheckAreaAsync(IAppDbContext db, OverwatchWatchArea area, CancellationToken ct)
    {
        var bounds = new BoundingBox(area.North, area.South, area.East, area.West);
        var items = new List<OverwatchAreaItem>();
        if (area.WatchIncidents) items.AddRange(IncidentItems(await incidents.GetIncidentsInBoundingBoxAsync(bounds, ct), area.MinSeverity));
        if (area.WatchWeather) items.AddRange(WeatherItems(await weatherAlerts.GetActiveAlertsAsync(bounds, ct), area.MinSeverity));
        if (area.WatchHazards) items.AddRange(HazardItems((await hazards.GetHazardsAsync(bounds, ct)).Features, area.MinSeverity));

        var seen = JsonSerializer.Deserialize<List<string>>(area.SeenKeysJson) ?? [];
        var seenSet = seen.ToHashSet(StringComparer.Ordinal);
        var fresh = items.Where(i => seenSet.Add(i.Key)).ToList();
        var isBaseline = area.LastCheckedAt is null;
        seen.AddRange(fresh.Select(i => i.Key));
        if (seen.Count > MaxSeenKeys) seen.RemoveRange(0, seen.Count - MaxSeenKeys);
        area.SeenKeysJson = JsonSerializer.Serialize(seen);
        area.LastCheckedAt = timeProvider.GetUtcNow();

        // The first check only learns what's already there - a new area shouldn't open with a
        // flood of "new" items that were happening before it was saved.
        if (isBaseline || fresh.Count == 0) return null;

        var captured = await StartCapturesAsync(area, fresh, ct);
        var alert = new OverwatchAreaAlert
        {
            Username = area.Username,
            AreaId = area.Id,
            Title = $"Overwatch: {area.Name}",
            Message = Summary(fresh) + (captured > 0 ? $" - recording {captured} camera{(captured == 1 ? "" : "s")} nearby (AREAS > CAPTURES)" : string.Empty),
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = "overwatch"
        };
        db.OverwatchAreaAlerts.Add(alert);
        return alert;
    }

    // Returns how many cameras started recording across the new incidents.
    private async Task<int> StartCapturesAsync(OverwatchWatchArea area, IReadOnlyList<OverwatchAreaItem> fresh, CancellationToken ct)
    {
        if (captures is null) return 0;
        var cameraCount = 0;
        foreach (var item in fresh.Where(i => i.Latitude is not null && i.Longitude is not null).Take(MaxCapturesPerCheck))
        {
            try
            {
                var capture = await captures.StartAsync(area.Username, area.Id, area.Name, item.Text, item.Latitude!.Value, item.Longitude!.Value, ct);
                cameraCount += capture?.Cameras.Count ?? 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Incident capture couldn't start for {Item} in area {Area}", item.Key, area.Id);
            }
        }
        return cameraCount;
    }

    public static string Summary(IReadOnlyList<OverwatchAreaItem> fresh)
    {
        // Two closures on the same road read as one line "(x2)" rather than a repeated phrase.
        var lines = fresh
            .GroupBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Text: g.Count() > 1 ? $"{g.Key} (\u00d7{g.Count()})" : g.Key,
                Rank: g.Min(i => i.Severity switch { "major" => 0, "moderate" => 1, _ => 2 })))
            .OrderBy(l => l.Rank)
            .ToList();
        var shown = string.Join("; ", lines.Take(MaxItemsInMessage).Select(l => l.Text));
        var hidden = lines.Skip(MaxItemsInMessage).Count();
        return hidden > 0 ? $"{fresh.Count} new: {shown}; and {hidden} more" : $"{fresh.Count} new: {shown}";
    }

    public static bool PassesSeverity(string severity, string minSeverity) => minSeverity switch
    {
        OverwatchAreaSeverity.Major => severity == IncidentSeverityLevels.Major,
        OverwatchAreaSeverity.Moderate => severity is IncidentSeverityLevels.Major or IncidentSeverityLevels.Moderate,
        _ => true
    };

    public static IEnumerable<OverwatchAreaItem> IncidentItems(IEnumerable<TrafficIncident> list, string minSeverity) =>
        list.Where(i => AlertingCategories.Contains(i.Category))
            // An unknown severity on a crash or closure is still worth a look unless only majors are wanted.
            .Where(i => PassesSeverity(i.SeverityLevel, minSeverity)
                        || (i.SeverityLevel == IncidentSeverityLevels.Unknown && minSeverity != OverwatchAreaSeverity.Major))
            .Select(i => new OverwatchAreaItem($"incident:{i.Id}",
                $"{IncidentCategories.SingularLabel(i.Category)} on {i.RoadwayName}", i.SeverityLevel, i.Latitude, i.Longitude));

    // NWS severities: Extreme/Severe -> major, Moderate -> moderate, Minor/Unknown -> minor.
    public static IEnumerable<OverwatchAreaItem> WeatherItems(IEnumerable<WeatherAlert> list, string minSeverity) =>
        list.Select(a => (Alert: a, Severity: a.Severity switch { "Extreme" or "Severe" => "major", "Moderate" => "moderate", _ => "minor" }))
            .Where(x => PassesSeverity(x.Severity, minSeverity))
            .Select(x => new OverwatchAreaItem($"weather:{x.Alert.Id}", x.Alert.Event, x.Severity));

    public static IEnumerable<OverwatchAreaItem> HazardItems(IEnumerable<HazardFeature> list, string minSeverity) =>
        list.Where(h => PassesSeverity(h.Severity, minSeverity))
            .Select(h => new OverwatchAreaItem($"hazard:{h.Id}", h.Title, h.Severity));

    private static OverwatchAreaView ToView(OverwatchWatchArea a) => new(a.Id, a.Name, new BoundingBox(a.North, a.South, a.East, a.West),
        a.WatchIncidents, a.WatchWeather, a.WatchHazards, a.MinSeverity, a.LastCheckedAt);
}

public sealed class OverwatchAreaBackgroundService(IServiceScopeFactory scopeFactory, ILogger<OverwatchAreaBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var written = await scope.ServiceProvider.GetRequiredService<OverwatchAreaService>().CheckAllAsync(stoppingToken);
                if (written > 0) logger.LogInformation("Overwatch areas: {Count} alert(s) written", written);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Overwatch area check run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
