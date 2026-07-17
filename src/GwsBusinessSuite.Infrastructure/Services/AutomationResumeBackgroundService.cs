using GwsBusinessSuite.Application.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class AutomationResumeBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationResumeBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IAutomationTriggerService>();
                var count = await service.ResumeDueWaitsAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Resumed {Count} waiting automation execution(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Automation resume sweep failed."); }
        }
    }
}
