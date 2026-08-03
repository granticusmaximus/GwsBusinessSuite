using GwsBusinessSuite.Application.Growth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class GrowthReportBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<GrowthReportBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var delivered = await scope.ServiceProvider.GetRequiredService<IGrowthReportService>()
                    .DeliverDueAsync(stoppingToken);
                if (delivered > 0)
                    logger.LogInformation("Delivered {Count} scheduled Growth Studio report(s).", delivered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled Growth Studio report sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
