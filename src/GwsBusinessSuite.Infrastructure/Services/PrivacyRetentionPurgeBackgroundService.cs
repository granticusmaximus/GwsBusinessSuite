using GwsBusinessSuite.Application.Privacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

/// <summary>
/// Makes the Privacy dashboard's retention policy real: without this, PrivacyRetentionPolicy
/// rows and their "eligible count" preview were display-only - nothing ever purged an expired
/// WebAnalyticsEvent/FormSubmission/Comment row. Runs once a day rather than on the
/// GrowthReportBackgroundService's 1-minute cadence since a retention sweep only needs
/// day-level precision and each pass can touch a lot of rows.
/// </summary>
public sealed class PrivacyRetentionPurgeBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PrivacyRetentionPurgeBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<IPrivacyOperationsService>()
                    .PurgeEligibleRecordsAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("Privacy retention purge deleted {Count} expired record(s).", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Privacy retention purge sweep failed.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
