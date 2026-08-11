using GwsBusinessSuite.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Mirrors PrivacyRetentionPurgeBackgroundService's shape exactly, kept as a separate service
// since it purges a different, non-privacy-policy-governed set of tables (see
// IOperationalDataRetentionService's own comment for why the two are split).
public sealed class OperationalDataRetentionBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<OperationalDataRetentionBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<IOperationalDataRetentionService>()
                    .PurgeExpiredRecordsAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("Operational data retention purge deleted {Count} expired record(s).", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Operational data retention purge sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
