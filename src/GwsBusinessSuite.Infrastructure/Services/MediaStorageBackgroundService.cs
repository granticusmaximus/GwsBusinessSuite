using GwsBusinessSuite.Application.CmsBuilder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Moves media held only in the database onto disk, and later clears the database copies once
// they're past the grace period (MediaLibraryService). Starts 10 minutes after the app does: a
// deploy that fails its health check rolls back within that window, and the previous release
// only reads the database copies - which this never clears early anyway.
public sealed class MediaStorageBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MediaStorageOptions> options,
    ILogger<MediaStorageBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private const int BatchSize = 25;
    private const int MaxBatchesPerTick = 40;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var media = scope.ServiceProvider.GetRequiredService<IMediaLibraryService>();
                var moved = 0;
                for (var batch = 0; batch < MaxBatchesPerTick; batch++)
                {
                    var count = await media.MoveToFilesAsync(BatchSize, stoppingToken);
                    moved += count;
                    if (count < BatchSize) break;
                }
                var released = await media.ReleaseDatabaseCopiesAsync(
                    TimeSpan.FromDays(Math.Max(1, options.Value.DatabaseCopyGraceDays)), 200, stoppingToken);
                if (moved > 0 || released > 0)
                    logger.LogInformation("Media storage: moved {Moved} image(s) to disk, cleared {Released} database copies.", moved, released);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media storage sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
