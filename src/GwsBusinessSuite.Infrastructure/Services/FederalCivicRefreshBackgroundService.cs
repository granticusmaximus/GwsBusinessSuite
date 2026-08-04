using GwsBusinessSuite.Application.GovernmentIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors LocalEventsRefreshBackgroundService: own PeriodicTimer + lock, independent of the
// 15-minute GovernmentIntelligence snapshot cycle, per the "auto-fetch triggered every hour"
// requirement for federal news/floor status. Kept independent (not folded into
// GovernmentIntelligenceRefreshBackgroundService) since it hits a different host
// (api.congress.gov) on a different, slower cadence and also owns a DB write (the
// transcript archive upsert) that the read-only snapshot refresh doesn't need to wait on.
public sealed class FederalCivicRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<FederalCivicRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

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
            logger.LogInformation("Federal Civic Feed: refresh skipped (already running)");
            return;
        }
        try
        {
            logger.LogInformation("Federal Civic Feed: starting hourly refresh");
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFederalCivicFeedService>();
            await svc.RefreshAsync(ct);
            logger.LogInformation("Federal Civic Feed: refresh complete");
        }
        catch (OperationCanceledException)
        {
            // Shutting down - not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Federal Civic Feed: refresh failed");
        }
        finally
        {
            RefreshLock.Release();
        }
    }
}
