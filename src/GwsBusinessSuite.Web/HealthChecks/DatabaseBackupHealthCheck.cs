using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.HealthChecks;

public sealed class DatabaseBackupHealthCheck(
    DatabaseBackupService backupService,
    IOptions<DatabaseBackupOptions> options) : IHealthCheck
{
    private readonly DatabaseBackupOptions _options = options.Value;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Scheduled backups are disabled."));
        }

        var latest = backupService.GetLatestBackupTime();
        if (latest is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No completed backup is available."));
        }

        var maximumAge = TimeSpan.FromHours(Math.Clamp(_options.IntervalHours, 1, 168) * 2);
        return Task.FromResult(DateTimeOffset.UtcNow - latest <= maximumAge
            ? HealthCheckResult.Healthy($"Latest backup completed at {latest:O}.")
            : HealthCheckResult.Unhealthy($"Latest backup is older than {maximumAge}."));
    }
}
