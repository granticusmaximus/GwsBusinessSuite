using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors GovernmentIntelligenceRefreshBackgroundService's PeriodicTimer + scoped-service
// pattern, but deliberately kept independent (own interval, own lock) rather than folded
// into that service's snapshot refresh - a Playwright browser render (~5-10s per site) is
// far slower than the plain HTTP fetches the rest of Civic Watch does, and hanging this
// off the same RefreshLock would make every manual "Refresh" click and 15-minute cycle
// pay that latency too. The two scrape entirely different hosts, so there's no need for
// them to serialize with each other.
public sealed class LocalEventsRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<LocalEventsRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public static readonly SemaphoreSlim RefreshLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        await RunRefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunRefreshAsync(stoppingToken);
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        if (!await RefreshLock.WaitAsync(0, ct))
        {
            logger.LogInformation("Local Events: refresh skipped (already running)");
            return;
        }
        try
        {
            logger.LogInformation("Local Events: starting Playwright refresh");
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<ILocalEventsScraperService>();
            var events = await svc.RefreshAsync(ct);
            logger.LogInformation("Local Events: refresh complete ({Count} events)", events.Count);
        }
        catch (OperationCanceledException)
        {
            // Shutting down - not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local Events: refresh failed");
        }
        finally
        {
            RefreshLock.Release();
        }
    }
}
