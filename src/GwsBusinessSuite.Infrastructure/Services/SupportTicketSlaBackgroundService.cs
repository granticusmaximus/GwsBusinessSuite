using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Application.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SupportTicketSlaBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOperationalAlertService alerts,
    ILogger<SupportTicketSlaBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var breaches = await scope.ServiceProvider.GetRequiredService<ISupportTicketService>()
                    .ProcessSlaBreachesAsync(stoppingToken);
                if (breaches > 0)
                    logger.LogWarning("Detected {Count} new support ticket SLA breach event(s).", breaches);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Support ticket SLA sweep failed.");
                await alerts.NotifyFailureAsync(
                    "support-ticket-sla-sweep", "The support ticket SLA sweep failed.", ex, stoppingToken);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
