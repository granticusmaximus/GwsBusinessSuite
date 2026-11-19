using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.HealthChecks;

public sealed class DatabaseBackupHealthCheck(
    DatabaseBackupService backupService,
    IOptions<DatabaseBackupOptions> options) : IHealthCheck
{
    private readonly DatabaseBackupOptions _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return HealthCheckResult.Healthy("Scheduled backups are disabled.");
        }

        var latest = backupService.GetLatestBackupTime();
        if (latest is null)
        {
            return HealthCheckResult.Unhealthy("No completed backup is available.");
        }

        var readinessProblem = await backupService.GetReadinessProblemAsync(cancellationToken);
        if (readinessProblem is not null)
            return HealthCheckResult.Unhealthy(readinessProblem);

        var maximumAge = TimeSpan.FromHours(Math.Clamp(_options.IntervalHours, 1, 168) * 2);
        if (DateTimeOffset.UtcNow - latest > maximumAge) return HealthCheckResult.Unhealthy($"Latest backup is older than {maximumAge}.");
        // The weekly restore drill (19-11-26): a failed or overdue one is Degraded, not Unhealthy, so
        // readiness isn't refused over it; the backups themselves are still being made.
        var drill = RestoreDrill.Read(_options.Path);
        if (drill is { Passed: false })
            return HealthCheckResult.Degraded($"Latest backup completed at {latest:O}, but the restore drill on {drill.At:O} failed: {drill.Summary}");
        if (drill is not null && DateTimeOffset.UtcNow - drill.At > RestoreDrill.OverdueAfter)
            return HealthCheckResult.Degraded($"Latest backup completed at {latest:O}; the last restore drill was {drill.At:O}.");
        return HealthCheckResult.Healthy($"Latest backup completed at {latest:O}." + (drill is null ? "" : $" Restore drill passed {drill.At:O}."));
    }
}
