using GwsBusinessSuite.Application.NewsIntelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class NewsRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<NewsRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run an initial refresh shortly after startup so the feed isn't empty
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        await RunRefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            await RunRefreshAsync(stoppingToken);
    }

    private async Task RunRefreshAsync(CancellationToken ct)
    {
        logger.LogInformation("News Intelligence: starting scheduled refresh");
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<INewsIntelligenceService>();
            await svc.RefreshAllAsync(ct);
            logger.LogInformation("News Intelligence: refresh complete");
        }
        catch (OperationCanceledException)
        {
            // Shutting down — not an error
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "News Intelligence: refresh failed");
        }
    }
}
