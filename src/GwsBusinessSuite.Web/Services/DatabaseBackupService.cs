using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.Services;

public sealed class DatabaseBackupOptions
{
    public const string SectionName = "Backups";

    public bool Enabled { get; set; }
    public string Path { get; set; } = "/app/backups";
    public string DataProtectionKeysPath { get; set; } = "/app/data/data-protection-keys";
    public int IntervalHours { get; set; } = 6;
    public int RetentionDays { get; set; } = 30;
}

public sealed class DatabaseBackupService(
    IConfiguration configuration,
    IOptions<DatabaseBackupOptions> options,
    ILogger<DatabaseBackupService> logger)
{
    private readonly DatabaseBackupOptions _options = options.Value;

    public async Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
        var sourceBuilder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(sourceBuilder.DataSource)
            || string.Equals(sourceBuilder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Backups require a file-backed SQLite database.");
        }

        Directory.CreateDirectory(_options.Path);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var workingDirectory = System.IO.Path.Combine(_options.Path, $".sentinel-{timestamp}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var databasePath = System.IO.Path.Combine(workingDirectory, "gws-suite.db");
            await using (var source = new SqliteConnection(connectionString))
            await using (var destination = new SqliteConnection($"Data Source={databasePath}"))
            {
                await source.OpenAsync(cancellationToken);
                await destination.OpenAsync(cancellationToken);
                source.BackupDatabase(destination);
            }

            if (Directory.Exists(_options.DataProtectionKeysPath))
            {
                CopyDirectory(
                    _options.DataProtectionKeysPath,
                    System.IO.Path.Combine(workingDirectory, "data-protection-keys"));
            }

            var archivePath = System.IO.Path.Combine(_options.Path, $"sentinel-backup-{timestamp}.zip");
            ZipFile.CreateFromDirectory(workingDirectory, archivePath, CompressionLevel.SmallestSize, false);
            PruneExpiredBackups();
            logger.LogInformation("Created Sentinel backup {BackupPath}.", archivePath);
            return archivePath;
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, true);
            }
        }
    }

    public DateTimeOffset? GetLatestBackupTime()
    {
        if (!Directory.Exists(_options.Path))
        {
            return null;
        }

        return Directory.EnumerateFiles(_options.Path, "sentinel-backup-*.zip")
            .Select(path => new FileInfo(path).LastWriteTimeUtc)
            .OrderByDescending(value => value)
            .Select(value => (DateTimeOffset?)value)
            .FirstOrDefault();
    }

    private void PruneExpiredBackups()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        foreach (var file in Directory.EnumerateFiles(_options.Path, "sentinel-backup-*.zip"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
            {
                File.Delete(file);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                directory,
                System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)));
        }
    }
}

public sealed class DatabaseBackupBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<DatabaseBackupOptions> options,
    ILogger<DatabaseBackupBackgroundService> logger) : BackgroundService
{
    private readonly DatabaseBackupOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DatabaseBackupService>()
                    .CreateBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The scheduled Sentinel backup failed.");
            }

            await Task.Delay(
                TimeSpan.FromHours(Math.Clamp(_options.IntervalHours, 1, 168)),
                stoppingToken);
        }
    }
}
