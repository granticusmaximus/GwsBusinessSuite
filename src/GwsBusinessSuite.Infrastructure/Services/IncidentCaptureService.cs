using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// One camera in a capture. FrameKey is the CameraTimelapseStore key its frames are saved under,
// so the existing time-lapse endpoint and player serve them unchanged.
public sealed record IncidentCaptureCamera(
    string FrameKey, string CameraId, string Name, double Latitude, double Longitude,
    string StreamUrl, string SourceName, string SourceAttributionUrl, double MilesAway);

public sealed record IncidentCapture(
    Guid Id, string Username, Guid AreaId, string AreaName, string Title,
    double Latitude, double Longitude, DateTimeOffset StartedAt, DateTimeOffset Until,
    List<IncidentCaptureCamera> Cameras);

// "Evidence capture" for watch areas: when a check finds a new crash, closure or road hazard, the
// nearest public still-image cameras start saving frames - for an hour from when it was first
// seen, plus the half hour before for any of them that are already favorites (their time-lapse
// frames are copied in). Only changed frames are kept, as with the favorites time-lapse.
// Captures are files, not database rows: a small JSON manifest each under the time-lapse root's
// _captures folder, frames alongside the time-lapse frames (same caps, not backed up, disposable).
public sealed class IncidentCaptureService(
    CameraDirectoryService cameras,
    CameraHealthService health,
    CameraTimelapseStore frames,
    IncidentCaptureManifestStore manifests,
    TimeProvider timeProvider,
    ILogger<IncidentCaptureService> logger)
{
    public const double CaptureRadiusMiles = 1.0;
    public const int MaxCamerasPerCapture = 3;
    public static readonly TimeSpan CaptureDuration = TimeSpan.FromHours(1);
    public static readonly TimeSpan BeforeWindow = TimeSpan.FromMinutes(30);
    public const int MaxCapturesPerUser = 20;

    // Null when no still-image camera is close enough - there's nothing to capture.
    public async Task<IncidentCapture?> StartAsync(string username, Guid areaId, string areaName, string title,
        double latitude, double longitude, CancellationToken ct = default)
    {
        var nearby = await NearestSnapshotCamerasAsync(latitude, longitude, ct);
        if (nearby.Count == 0) return null;

        var id = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        var capture = new IncidentCapture(id, username, areaId, areaName, title, latitude, longitude, now, now + CaptureDuration,
            nearby.Select((c, i) => new IncidentCaptureCamera($"capture-{id:N}-{i}", c.Camera.Id, c.Camera.Name,
                c.Camera.Latitude, c.Camera.Longitude, c.Camera.StreamUrl, c.Camera.SourceName, c.Camera.SourceAttributionUrl, c.Miles)).ToList());
        manifests.Save(capture);

        foreach (var camera in capture.Cameras)
        {
            frames.CopyFramesSince(camera.CameraId, camera.FrameKey, now - BeforeWindow);
        }
        await CaptureFramesAsync(capture, ct);
        PruneOld(username);
        return capture;
    }

    // Called every few minutes: one new frame per camera of every capture still running.
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var active = manifests.LoadAll().Where(c => c.Until > now).ToList();
        foreach (var capture in active)
        {
            ct.ThrowIfCancellationRequested();
            await CaptureFramesAsync(capture, ct);
        }
        return active.Count;
    }

    public IReadOnlyList<IncidentCapture> ListForUser(string username) =>
        manifests.LoadAll().Where(c => c.Username == username).OrderByDescending(c => c.StartedAt).ToList();

    public void Delete(string username, Guid id)
    {
        if (manifests.Load(id) is not { } capture || capture.Username != username) return;
        foreach (var camera in capture.Cameras) frames.DeleteFrames(camera.FrameKey);
        manifests.Delete(id);
    }

    private async Task CaptureFramesAsync(IncidentCapture capture, CancellationToken ct)
    {
        foreach (var camera in capture.Cameras)
        {
            try
            {
                var feed = new CameraFeed(camera.CameraId, camera.Name, camera.Latitude, camera.Longitude, camera.StreamUrl,
                    CameraStreamKind.Snapshot, camera.SourceName, camera.SourceAttributionUrl);
                var result = await health.CheckWithFrameAsync(feed, ct);
                if (result.NewImage is { } image)
                {
                    await frames.SaveFrameAsync(camera.FrameKey, image, timeProvider.GetUtcNow(), ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Incident capture {Capture}: frame from {Camera} failed", capture.Id, camera.CameraId);
            }
        }
    }

    private async Task<IReadOnlyList<(CameraFeed Camera, double Miles)>> NearestSnapshotCamerasAsync(double latitude, double longitude, CancellationToken ct)
    {
        var dLat = CaptureRadiusMiles / 69.0;
        var dLon = CaptureRadiusMiles / (69.0 * Math.Max(0.2, Math.Cos(latitude * Math.PI / 180)));
        var inBox = await cameras.GetCamerasInBoundingBoxAsync(
            new BoundingBox(latitude + dLat, latitude - dLat, longitude + dLon, longitude - dLon), ct);
        return inBox
            .Where(c => c.StreamKind == CameraStreamKind.Snapshot)
            .Select(c => (Camera: c, Miles: Miles(latitude, longitude, c.Latitude, c.Longitude)))
            .Where(x => x.Miles <= CaptureRadiusMiles)
            .OrderBy(x => x.Miles)
            .Take(MaxCamerasPerCapture)
            .ToList();
    }

    private void PruneOld(string username)
    {
        foreach (var old in ListForUser(username).Skip(MaxCapturesPerUser))
        {
            Delete(username, old.Id);
        }
    }

    private static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = (lat2 - lat1) * 69.0;
        var dLon = (lon2 - lon1) * 69.0 * Math.Cos((lat1 + lat2) / 2 * Math.PI / 180);
        return Math.Sqrt(dLat * dLat + dLon * dLon);
    }
}

// The capture manifests: one JSON file per capture under {root}/_captures.
public sealed class IncidentCaptureManifestStore(string rootPath, ILogger<IncidentCaptureManifestStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string Folder => Path.Combine(rootPath, "_captures");

    public void Save(IncidentCapture capture)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(capture.Id), JsonSerializer.Serialize(capture, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Incident capture: couldn't save manifest {Id}", capture.Id);
        }
    }

    public IncidentCapture? Load(Guid id) => Read(PathFor(id));

    public IReadOnlyList<IncidentCapture> LoadAll()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*.json").Select(Read).OfType<IncidentCapture>().ToList();
    }

    public void Delete(Guid id)
    {
        try
        {
            File.Delete(PathFor(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Incident capture: couldn't delete manifest {Id}", id);
        }
    }

    private string PathFor(Guid id) => Path.Combine(Folder, $"{id:N}.json");

    private IncidentCapture? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<IncidentCapture>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Incident capture: couldn't read manifest {Path}", path);
            return null;
        }
    }
}

public sealed class IncidentCaptureBackgroundService(IServiceScopeFactory scopeFactory, ILogger<IncidentCaptureBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IncidentCaptureService>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Incident capture sweep failed");
            }
        }
    }
}
