using GwsBusinessSuite.Application.NewsIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<NewsRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // Prevents the background timer from overlapping a manually-triggered refresh.
    // SemaphoreSlim(1,1) acts as a non-reentrant async lock.
    public static readonly SemaphoreSlim RefreshLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
            logger.LogInformation("News Intelligence: starting scheduled refresh");
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<INewsIntelligenceService>();
            await svc.RefreshAllAsync(ct);
            logger.LogInformation("News Intelligence: refresh complete");
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
