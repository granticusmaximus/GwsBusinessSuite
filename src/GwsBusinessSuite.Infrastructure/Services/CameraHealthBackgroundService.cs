using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Builds camera health history for favorited cameras (every admin's, deduplicated) every 10
// minutes, so "frozen" can be judged for the cameras people actually watch without crawling
// thousands of public cameras. A few checks at a time keeps this polite to the DOT servers.
public sealed class CameraHealthBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<CameraHealthBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private const int MaxCamerasPerSweep = 200;
    private const int Parallelism = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        await SweepAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await SweepAsync(stoppingToken);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var health = scope.ServiceProvider.GetRequiredService<CameraHealthService>();
            var timelapse = scope.ServiceProvider.GetRequiredService<CameraTimelapseStore>();
            await using var db = await scope.ServiceProvider.GetRequiredService<IAppDbContextFactory>().CreateDbContextAsync(ct);
            var favorites = (await db.CameraFavorites.AsNoTracking().ToListAsync(ct))
                .Where(f => f.StreamKind == nameof(CameraStreamKind.Snapshot))
                .GroupBy(f => f.CameraId)
                .Select(g => g.First())
                .Take(MaxCamerasPerSweep)
                .Select(f => new CameraFeed(f.CameraId, f.Name, f.Latitude, f.Longitude, f.StreamUrl,
                    CameraStreamKind.Snapshot, f.SourceName, f.SourceAttributionUrl))
                .ToList();
            if (favorites.Count == 0) return;

            await Parallel.ForEachAsync(favorites, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (camera, token) =>
                {
                    // Favorites double as the time-lapse list: keep each frame that changed.
                    var result = await health.CheckWithFrameAsync(camera, token);
                    if (result.NewImage is { } image) await timelapse.SaveFrameAsync(camera.Id, image, DateTimeOffset.UtcNow, token);
                });
            logger.LogDebug("Camera health: checked {Count} favorited cameras", favorites.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Camera health sweep failed");
        }
    }
}
