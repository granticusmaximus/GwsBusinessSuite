using GwsBusinessSuite.Application.NewsIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    OllamaWorkloadScheduler ollamaWorkloads,
    ILogger<NewsRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // Prevents the background timer from overlapping a manually-triggered refresh.
    // SemaphoreSlim(1,1) acts as a non-reentrant async lock.
    public static readonly SemaphoreSlim RefreshLock = new(1, 1);

    // Takes the model was too busy to write at refresh time are filled every few minutes (20-11-26).
    private static readonly TimeSpan FillTakesInterval = TimeSpan.FromMinutes(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RefreshLoopAsync(stoppingToken), FillTakesLoopAsync(stoppingToken));

    private async Task FillTakesLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(FillTakesInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!await RefreshLock.WaitAsync(0, stoppingToken)) continue;
                try
                {
                    using var workloadPriority = ollamaWorkloads.UseBackgroundPriority();
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var written = await scope.ServiceProvider.GetRequiredService<INewsIntelligenceService>().FillMissingTakesAsync(stoppingToken);
                    if (written > 0) logger.LogInformation("Media Watch: {Count} AI takes written in the background", written);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Media Watch: background AI takes failed");
                }
                finally
                {
                    RefreshLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    private async Task RefreshLoopAsync(CancellationToken stoppingToken)
    {
        // Run an initial refresh shortly after startup so the feed isn't empty
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        await RunRefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunRefreshAsync(stoppingToken);
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        if (!await RefreshLock.WaitAsync(0, ct))
        {
            logger.LogInformation("News Intelligence: scheduled refresh skipped (manual refresh already running)");
            return;
        }
        try
        {
            using var workloadPriority = ollamaWorkloads.UseBackgroundPriority();
            logger.LogInformation("News Intelligence: starting scheduled refresh");
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<INewsIntelligenceService>();
            await svc.RefreshAllAsync(ct);
            logger.LogInformation("News Intelligence: refresh complete");

            // The hourly tick doubles as the digest clock: it sends once the chosen hour has passed.
            var digest = await scope.ServiceProvider.GetRequiredService<INewsWatchService>().SendDigestAsync(force: false, ct);
            if (digest.Sent) logger.LogInformation("Media Watch digest: {Message}", digest.Message);

            // Mondays: each reader's weekly reading page in Sentinel (20-11-26).
            var pages = await scope.ServiceProvider.GetRequiredService<INewsWatchService>().RunReadingDigestsIfDueAsync(ct);
            if (pages > 0) logger.LogInformation("Media Watch: weekly reading page rebuilt for {Count} readers", pages);
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "News Intelligence: refresh failed");
        }
        finally
        {
            RefreshLock.Release();
        }
    }
}
