using GwsBusinessSuite.Application.Campaigns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as EmailCampaignBackgroundService, but every minute: new-article alerts should go
// out promptly once their grace period ends, and each tick only sends a small batch.
public sealed class ArticleAlertBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<ArticleAlertBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<IArticleAlertService>().ProcessAsync(stoppingToken);
                if (sent > 0)
                    logger.LogInformation("Sent {Count} new-article alert email(s).", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "New-article alert sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
