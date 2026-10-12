using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record WatchedCamera(string Id, string Name, double Latitude, double Longitude, string SnapshotUrl);

public sealed record CameraWatchReadingResult(string Level, string Summary, bool Alerted);

public sealed record CameraWatchStatus(DateTimeOffset? LastCheckedAt, int CheckedLastHour, int Watched, IReadOnlyList<OverwatchCameraWatchState> NeedAttention);

// Camera watch: early warning from your own cameras. The Mac app's local vision model reads each
// watched camera (favorites first, then cameras inside watched areas) every few minutes and
// posts what it saw here. When a camera changes to "bad" (snow, ice or flooding on the road, or
// stopped traffic) the user gets one Overwatch bell alert with the frames from before and after.
// The model never runs on this server - it has no vision model - so nothing is watched while the
// Mac app is closed.
public sealed class CameraWatchService(
    IAppDbContextFactory dbContextFactory,
    ICameraFavoritesService favorites,
    CameraDirectoryService directory,
    CameraSnapshotDownloader snapshots,
    CameraWatchFrameStore frames,
    OverwatchAreaNotifier notifier,
    IMemoryCache cache,
    TimeProvider timeProvider,
    ILogger<CameraWatchService> logger)
{
    public const int MaxCameras = 40;
    public const int IntervalMinutes = 10;
    // A camera that went bad and stays bad (or flaps) alerts at most this often.
    public static readonly TimeSpan AlertCooldown = TimeSpan.FromHours(2);

    public async Task<IReadOnlyList<WatchedCamera>> GetWatchListAsync(string username, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync($"camera-watch:{username.ToLowerInvariant()}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var list = new List<WatchedCamera>();
            foreach (var camera in await favorites.GetFavoritesAsync(username, ct))
            {
                if (camera.StreamKind == CameraStreamKind.Snapshot) list.Add(ToWatched(camera));
            }
            await using var db = await dbContextFactory.CreateDbContextAsync(ct);
            var areas = await db.OverwatchWatchAreas.AsNoTracking().Where(a => a.Username == username).ToListAsync(ct);
            foreach (var area in areas)
            {
                if (list.Count >= MaxCameras) break;
                try
                {
                    var inArea = await directory.GetCamerasInBoundingBoxAsync(new BoundingBox(area.North, area.South, area.East, area.West), ct);
                    list.AddRange(inArea.Where(c => c.StreamKind == CameraStreamKind.Snapshot).Select(ToWatched));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Camera watch: couldn't list cameras for area {Area}", area.Id);
                }
            }
            return (IReadOnlyList<WatchedCamera>)list.DistinctBy(c => c.Id).Take(MaxCameras).ToList();
        }) ?? [];

    // Only cameras on this user's own watch list can be fetched through here.
    public async Task<byte[]?> GetSnapshotAsync(string username, string cameraId, CancellationToken ct = default)
    {
        var camera = (await GetWatchListAsync(username, ct)).FirstOrDefault(c => c.Id == cameraId);
        return camera is null ? null : await snapshots.DownloadAsync(camera.SnapshotUrl, ct);
    }

    public async Task<CameraWatchReadingResult?> RecordReadingAsync(
        string username, string cameraId, string? modelOutput, byte[]? frame, CancellationToken ct = default)
    {
        var camera = (await GetWatchListAsync(username, ct)).FirstOrDefault(c => c.Id == cameraId);
        if (camera is null) return null;
        var reading = CameraConditions.Parse(modelOutput);
        var summary = Summary(reading);
        var now = timeProvider.GetUtcNow();

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var state = await db.OverwatchCameraWatchStates.FirstOrDefaultAsync(s => s.Username == username && s.CameraId == cameraId, ct);
        var previousLevel = state?.LastLevel;
        if (state is null)
        {
            state = new OverwatchCameraWatchState
            {
                Username = username, CameraId = cameraId, CameraName = camera.Name, LastLevel = reading.Level,
                Latitude = camera.Latitude, Longitude = camera.Longitude, CreatedBy = username
            };
            await db.OverwatchCameraWatchStates.AddAsync(state, ct);
        }

        // An unreadable frame says nothing either way: keep the last real reading.
        if (reading.Level == CameraConditions.UnknownLevel)
        {
            state.LastCheckedUnix = now.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
            return new CameraWatchReadingResult(reading.Level, summary, false);
        }

        var before = frames.ReadLatest(username, cameraId);
        var turnedBad = reading.Level == CameraConditions.BadLevel
            && previousLevel is not null && previousLevel != CameraConditions.BadLevel
            && (state.LastAlertUnix is not { } last || now - DateTimeOffset.FromUnixTimeSeconds(last) >= AlertCooldown);
        OverwatchAreaAlert? alert = null;
        if (turnedBad)
        {
            alert = new OverwatchAreaAlert
            {
                Username = username,
                AreaId = Guid.Empty,
                Title = $"Camera watch: {camera.Name}",
                Message = $"Now {summary} (was {previousLevel}).",
                CreatedAt = now,
                CreatedBy = "camera-watch",
                CameraId = cameraId
            };
            alert.LinkUrl = $"/admin/osint?detection={alert.Id}";
            await db.OverwatchAreaAlerts.AddAsync(alert, ct);
            state.LastAlertUnix = now.ToUnixTimeSeconds();
            frames.SaveAlertFrames(alert.Id, before, frame);
        }

        state.CameraName = camera.Name;
        state.LastLevel = reading.Level;
        state.LastSummary = summary;
        state.LastCheckedUnix = now.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        if (frame is { Length: > 0 }) frames.SaveLatest(username, cameraId, frame);
        if (alert is not null)
        {
            notifier.Raise(username, new OverwatchAreaAlertView(alert.Id, alert.AreaId, alert.Title, alert.Message, false, alert.CreatedAt, alert.LinkUrl));
        }
        return new CameraWatchReadingResult(reading.Level, summary, alert is not null);
    }

    public async Task<CameraWatchStatus> GetStatusAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var states = await db.OverwatchCameraWatchStates.AsNoTracking().Where(s => s.Username == username).ToListAsync(ct);
        var hourAgo = timeProvider.GetUtcNow().AddHours(-1).ToUnixTimeSeconds();
        var last = states.Count == 0 ? (long?)null : states.Max(s => s.LastCheckedUnix);
        return new CameraWatchStatus(
            last is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
            states.Count(s => s.LastCheckedUnix >= hourAgo),
            (await GetWatchListAsync(username, ct)).Count,
            states.Where(s => s.LastLevel is CameraConditions.BadLevel or CameraConditions.CautionLevel)
                .OrderBy(s => s.LastLevel == CameraConditions.BadLevel ? 0 : 1).ThenBy(s => s.CameraName).ToList());
    }

    public async Task<(OverwatchAreaAlert Alert, OverwatchCameraWatchState? Camera)?> GetDetectionAsync(string username, Guid alertId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var alert = await db.OverwatchAreaAlerts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == alertId && a.Username == username && a.CreatedBy == "camera-watch", ct);
        if (alert is null) return null;
        var camera = await db.OverwatchCameraWatchStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Username == username && s.CameraId == alert.CameraId, ct);
        return (alert, camera);
    }

    public static string Summary(CameraConditionReading reading)
    {
        var parts = new List<string>();
        if (reading.Road is not ("unknown" or "dry")) parts.Add($"road {reading.Road}");
        if (reading.Traffic is "heavy" or "stopped") parts.Add($"traffic {reading.Traffic}");
        if (reading.Visibility is "fog" or "snow" or "rain") parts.Add(reading.Visibility);
        var text = parts.Count == 0 ? reading.Level : string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(reading.Note) ? text : $"{text} - {reading.Note}";
    }

    private static WatchedCamera ToWatched(CameraFeed camera) =>
        new(camera.Id, camera.Name, camera.Latitude, camera.Longitude, camera.StreamUrl);
}

// Frames for camera watch: each watched camera's latest frame (the "before" of a future alert)
// and the before/after pair saved with each alert. Kept beside the time-lapse frames.
public sealed class CameraWatchFrameStore(string rootPath, ILogger<CameraWatchFrameStore> logger)
{
    private const int MaxFrameBytes = 2 * 1024 * 1024;
    private string Root => Path.Combine(rootPath, "_camera-watch");

    public void SaveLatest(string username, string cameraId, byte[] frame) =>
        Write(Path.Combine(Root, "latest", Key(username), Key(cameraId) + ".jpg"), frame);

    public byte[]? ReadLatest(string username, string cameraId) =>
        Read(Path.Combine(Root, "latest", Key(username), Key(cameraId) + ".jpg"));

    public void SaveAlertFrames(Guid alertId, byte[]? before, byte[]? after)
    {
        if (before is { Length: > 0 }) Write(AlertPath(alertId, "before"), before);
        if (after is { Length: > 0 }) Write(AlertPath(alertId, "after"), after);
    }

    // which: "before" or "after".
    public byte[]? ReadAlertFrame(Guid alertId, string which) =>
        which is "before" or "after" ? Read(AlertPath(alertId, which)) : null;

    private string AlertPath(Guid alertId, string which) => Path.Combine(Root, "alerts", $"{alertId:N}-{which}.jpg");

    // File names never come from user input directly.
    private static string Key(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant())))[..32];

    private void Write(string path, byte[] bytes)
    {
        if (bytes.Length > MaxFrameBytes) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Camera watch: couldn't save a frame");
        }
    }

    private byte[]? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Camera watch: couldn't read a frame");
            return null;
        }
    }
}
