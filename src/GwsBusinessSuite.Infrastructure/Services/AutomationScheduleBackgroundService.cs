using GwsBusinessSuite.Application.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class AutomationScheduleBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AutomationScheduleBackgroundService> logger) : BackgroundService
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
                var count = await service.RunDueSchedulesAsync(stoppingToken);
                if (count > 0) logger.LogInformation("Started {Count} scheduled automation workflow(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Automation schedule sweep failed."); }
        }
    }
}
