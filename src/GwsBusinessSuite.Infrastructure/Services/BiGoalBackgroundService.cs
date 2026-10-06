using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.BusinessIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Hourly BI Dashboards jobs: checks every chart that has a goal, then sends any weekly report
// emails that are due. A chart that crossed its goal since the previous check (reached a target, or went over a limit) fires the "BI Goal Crossed"
// automation trigger once - EvaluateGoalsAsync tracks the last state, so it doesn't repeat.
public sealed class BiGoalBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<BiGoalBackgroundService> logger) : BackgroundService
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
        try
        {
            using var scope = scopeFactory.CreateScope();
            var bi = scope.ServiceProvider.GetRequiredService<IBusinessIntelligenceService>();
            var triggers = scope.ServiceProvider.GetRequiredService<IAutomationTriggerService>();
            foreach (var crossing in await bi.EvaluateGoalsAsync(ct))
            {
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    widgetTitle = crossing.Title,
                    owner = crossing.OwnerUsername,
                    metric = crossing.MetricLabel,
                    total = crossing.Total,
                    goal = crossing.GoalValue,
                    goalType = crossing.GoalIsCeiling ? "ceiling" : "floor",
                    goalMet = crossing.GoalMet
                });
                await triggers.TriggerBiGoalCrossedAsync(crossing.Title, payload, ct);
            }

            // Weekly report emails ride the same hourly tick (each is due once a week).
            await scope.ServiceProvider.GetRequiredService<IBiReportEmailService>().SendDueReportsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BI Dashboards: hourly goal check / report email run failed.");
        }
    }
}
