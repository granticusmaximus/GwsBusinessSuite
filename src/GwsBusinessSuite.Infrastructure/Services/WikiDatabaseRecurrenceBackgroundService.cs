using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors AutomationScheduleBackgroundService's own shape (PeriodicTimer sweep, one failure
// alerted rather than crashing the host) - a separate hosted service rather than folding this
// sweep into that one, since recurring rows have nothing to do with the automation engine and
// pairing them would make an unrelated future change to either one risk breaking the other.
public sealed class WikiDatabaseRecurrenceBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOperationalAlertService alerts,
    ILogger<WikiDatabaseRecurrenceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IWikiDatabaseRecurrenceService>();
                var count = await service.RunDueAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Created {Count} recurring row(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Recurring row sweep failed.");
                await alerts.NotifyFailureAsync("wiki-database-recurrence-sweep", "The recurring row sweep failed.", ex, stoppingToken);
            }
        }
    }
}
