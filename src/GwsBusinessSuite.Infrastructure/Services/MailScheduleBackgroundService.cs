using GwsBusinessSuite.Application.Mail;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Durable scheduled sends and snooze wakes (22-11-26). Each round uses a fresh scope; individual
// mailbox failures stay on their queue row and are retried by MailService with bounded backoff.
public sealed class MailScheduleBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<MailScheduleBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var mail = scope.ServiceProvider.GetRequiredService<IMailService>();
                var sent = await mail.ProcessScheduledSendsAsync(stoppingToken);
                var restored = await mail.ProcessSnoozesAsync(stoppingToken);
                if (sent + restored > 0)
                    logger.LogInformation("Mail schedule processed {Sent} send(s) and {Restored} snooze wake(s)", sent, restored);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mail schedule round failed; it runs again in thirty seconds");
            }
        }
    }
}
