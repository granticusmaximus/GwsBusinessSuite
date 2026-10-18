using GwsBusinessSuite.Application.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as EmailCampaignBackgroundService: a periodic sweep that re-sends overdue invoices
// marked "Remind automatically" (BillingService.SendDueRemindersAsync holds the rules). Hourly is
// plenty - the rules space reminders a week apart.
public sealed class InvoiceReminderBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<InvoiceReminderBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<IBillingService>()
                    .SendDueRemindersAsync(stoppingToken);
                if (sent > 0)
                    logger.LogInformation("Sent {Count} invoice payment reminder(s).", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Invoice reminder sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
