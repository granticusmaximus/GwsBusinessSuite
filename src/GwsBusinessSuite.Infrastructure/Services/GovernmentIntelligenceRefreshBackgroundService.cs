using GwsBusinessSuite.Application.GovernmentIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors NewsRefreshBackgroundService's PeriodicTimer + scoped-service-resolution
// pattern. Before this, GetSnapshotAsync's 15-minute IMemoryCache entry was only ever
// populated on-demand by a page load or the manual "Refresh" button, so the snapshot
// could silently go stale for hours if nobody opened the page.
public sealed class GovernmentIntelligenceRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<GovernmentIntelligenceRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    // Prevents this timer from overlapping a manually-triggered "Refresh" click.
    public static readonly SemaphoreSlim RefreshLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            logger.LogInformation("Government Intelligence: scheduled refresh skipped (manual refresh already running)");
            return;
        }
        try
        {
            logger.LogInformation("Government Intelligence: starting scheduled refresh");
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IGovernmentIntelligenceService>();
            await svc.GetSnapshotAsync(forceRefresh: true, ct);
            logger.LogInformation("Government Intelligence: refresh complete");
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Government Intelligence: refresh failed");
        }
        finally
        {
            RefreshLock.Release();
        }
    }
}
