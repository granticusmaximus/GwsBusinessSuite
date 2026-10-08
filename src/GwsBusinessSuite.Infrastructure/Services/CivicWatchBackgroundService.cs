using GwsBusinessSuite.Application.GovernmentIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Hourly Civic Watch upkeep, separate from the 15-minute snapshot refresh: re-check the bill
// watchlist (alerts + automation trigger), pick up new county commission agendas/minutes and
// summarize a couple of them at background Ollama priority. "What changed" sightings are
// recorded by GovernmentIntelligenceRefreshBackgroundService right after each snapshot build.
public sealed class CivicWatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<CivicWatchBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        await RunAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunAsync(stoppingToken);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var civic = scope.ServiceProvider.GetRequiredService<ICivicWatchService>();
        try
        {
            var changes = await civic.CheckWatchedBillsAsync(ct);
            if (changes.Count > 0) logger.LogInformation("Civic Watch: {Count} watched bill(s) changed status", changes.Count);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Civic Watch: bill watchlist check failed");
        }

        try
        {
            await civic.RefreshMeetingDocumentsAsync(maxSummaries: 2, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Civic Watch: county meeting documents refresh failed");
        }
    }
}
