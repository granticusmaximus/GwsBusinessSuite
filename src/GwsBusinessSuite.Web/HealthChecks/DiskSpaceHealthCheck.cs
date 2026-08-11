using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.HealthChecks;

// The SQLite database, the backup archive, and Live Show recordings all live under the same
// data volume in production (a single droplet) - if that fills up, SQLite writes start
// failing and DatabaseBackupService can no longer produce a backup either, at the exact
// moment a backup would matter most. Nothing else in this app watches free disk space, so
// this is the only early warning before that happens.
public sealed class DiskSpaceHealthCheck(
    IConfiguration configuration,
    IOptions<DatabaseBackupOptions> backupOptions) : IHealthCheck
{
    private const long WarningThresholdBytes = 2L * 1024 * 1024 * 1024;
    private const long CriticalThresholdBytes = 512L * 1024 * 1024;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pathsToCheck = new[] { backupOptions.Value.Path, GetDatabaseDirectory() }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct()
                .ToList();

            if (pathsToCheck.Count == 0)
                return Task.FromResult(HealthCheckResult.Healthy("No data paths configured to check."));

            var worst = pathsToCheck
                .Select(DescribeFreeSpace)
                .OrderBy(entry => entry.AvailableBytes)
                .First();

            var message = $"{FormatBytes(worst.AvailableBytes)} free on the volume backing {worst.Path}.";
            if (worst.AvailableBytes < CriticalThresholdBytes)
                return Task.FromResult(HealthCheckResult.Unhealthy(message));
            if (worst.AvailableBytes < WarningThresholdBytes)
                return Task.FromResult(HealthCheckResult.Degraded(message));
            return Task.FromResult(HealthCheckResult.Healthy(message));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Disk space could not be determined.", ex));
        }
    }

    private string? GetDatabaseDirectory()
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:"
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
    }

    private static (string Path, long AvailableBytes) DescribeFreeSpace(string path)
    {
        var probe = path;
        // Walk up to the nearest existing ancestor - the configured directory may not exist
        // yet on a fresh deploy (DatabaseBackupService creates it on first backup), but its
        // parent volume still does and that's what actually matters here.
        while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
        {
            probe = Path.GetDirectoryName(probe);
        }
        if (string.IsNullOrEmpty(probe)) probe = Path.GetPathRoot(path) ?? "/";

        var drive = new DriveInfo(probe);
        return (path, drive.AvailableFreeSpace);
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value:0.#} {units[unitIndex]}";
    }
}
