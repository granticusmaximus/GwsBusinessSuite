using GwsBusinessSuite.Application.Campaigns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as GrowthReportBackgroundService - a periodic sweep for due sends.
public sealed class EmailCampaignBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<EmailCampaignBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var attempted = await scope.ServiceProvider.GetRequiredService<IEmailCampaignService>()
                    .ProcessDueSendsAsync(stoppingToken);
                if (attempted > 0)
                    logger.LogInformation("Attempted {Count} email campaign step send(s).", attempted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email campaign sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
