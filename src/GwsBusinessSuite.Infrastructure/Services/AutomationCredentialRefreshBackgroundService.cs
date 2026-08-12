using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Periodic rotation for oauth2-type AutomationCredentials, mirroring
// AutomationScheduleBackgroundService's own sweep shape. Refreshes anything expiring within the
// next hour (or with no known expiry at all, since some providers never report one) so a node
// using the credential rarely if ever hits a stale-token failure mid-run.
public sealed class AutomationCredentialRefreshBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOperationalAlertService alerts,
    ILogger<AutomationCredentialRefreshBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var credentials = scope.ServiceProvider.GetRequiredService<IAutomationCredentialService>();
                var count = await credentials.RefreshExpiringOAuthCredentialsAsync(RefreshWindow, stoppingToken);
                if (count > 0) logger.LogInformation("Refreshed {Count} OAuth2 automation credential(s).", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automation credential refresh sweep failed.");
                await alerts.NotifyFailureAsync("automation-credential-refresh-sweep", "The automation OAuth2 credential refresh sweep failed.", ex, stoppingToken);
            }
        }
    }
}
