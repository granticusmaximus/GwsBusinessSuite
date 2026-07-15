using GwsBusinessSuite.Application.NewsIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Top News (the general, not-tied-to-a-topic feed) refreshes far more often than watched
// topics - it's meant to be the "what's happening right now" view, so an hourly cadence
// (NewsRefreshBackgroundService's interval, shared with every topic) was too stale for it.
// Shares NewsRefreshBackgroundService's static RefreshLock so this can never overlap the
// hourly full refresh or a manual "Refresh All"/"Refresh Top News" click and double-write
// the same NewsItems rows.
public sealed class TopNewsRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<TopNewsRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // NewsRefreshBackgroundService already does an initial full refresh (which
        // includes Top News) 30s after startup - offset this one's first tick past that
        // so they don't both fire in the same instant on a cold start.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunRefreshAsync(stoppingToken);
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        if (!await NewsRefreshBackgroundService.RefreshLock.WaitAsync(0, ct))
        {
            logger.LogInformation("Top News: scheduled refresh skipped (another refresh already running)");
            return;
        }

        try
        {
            logger.LogInformation("Top News: starting scheduled refresh");
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<INewsIntelligenceService>();
            await svc.RefreshTopNewsAsync(ct);
            logger.LogInformation("Top News: refresh complete");
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Top News: refresh failed");
        }
        finally
        {
            NewsRefreshBackgroundService.RefreshLock.Release();
        }
    }
}
