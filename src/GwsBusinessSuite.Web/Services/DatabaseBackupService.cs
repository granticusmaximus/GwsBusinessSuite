using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Web.Services;

public sealed class DatabaseBackupOptions
{
    public const string SectionName = "Backups";

    public bool Enabled { get; set; }
    public string Path { get; set; } = "/app/backups";
    public string DataProtectionKeysPath { get; set; } = "/app/data/data-protection-keys";
    public string EncryptionKeyPath { get; set; } = "/app/data/backup-encryption.key";
    public string EncryptionKey { get; set; } = string.Empty;
    public string LiveShowRecordingsPath { get; set; } = "/app/data/live-show-recordings";
    public int IntervalHours { get; set; } = 6;
    public int RetentionDays { get; set; } = 30;
}

public sealed class DatabaseBackupService(
    IConfiguration configuration,
    IOptions<DatabaseBackupOptions> options,
    ILogger<DatabaseBackupService> logger)
{
    private const string ArchiveExtension = ".gwsbackup";
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
        var workingDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gws-backup-{Guid.NewGuid():N}");
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

            var copiedKeysPath = System.IO.Path.Combine(workingDirectory, "data-protection-keys");
            if (!Directory.Exists(copiedKeysPath)
                || !Directory.EnumerateFiles(copiedKeysPath, "*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException("A backup cannot be created without the Data Protection key ring.");

            if (Directory.Exists(_options.LiveShowRecordingsPath))
                CopyDirectory(_options.LiveShowRecordingsPath, System.IO.Path.Combine(workingDirectory, "live-show-recordings"));

            var manifest = await BackupArchive.CreateManifestAsync(workingDirectory, timestamp, cancellationToken);
            await File.WriteAllTextAsync(System.IO.Path.Combine(workingDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, BackupArchive.JsonOptions), cancellationToken);

            var plaintextArchive = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gws-backup-{Guid.NewGuid():N}.zip");
            var archivePath = System.IO.Path.Combine(_options.Path, $"gws-backup-{timestamp}{ArchiveExtension}");
            try
            {
                ZipFile.CreateFromDirectory(workingDirectory, plaintextArchive, CompressionLevel.SmallestSize, false);
                await BackupArchive.EncryptAsync(plaintextArchive, archivePath, GetOrCreateEncryptionKey(), cancellationToken);
            }
            finally { if (File.Exists(plaintextArchive)) File.Delete(plaintextArchive); }
            PruneExpiredBackups();
            logger.LogInformation("Created encrypted GWS backup {BackupPath}.", archivePath);
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

        return Directory.EnumerateFiles(_options.Path, $"gws-backup-*{ArchiveExtension}")
            .Select(path => new FileInfo(path).LastWriteTimeUtc)
            .OrderByDescending(value => value)
            .Select(value => (DateTimeOffset?)value)
            .FirstOrDefault();
    }

    private void PruneExpiredBackups()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
        foreach (var file in Directory.EnumerateFiles(_options.Path, $"gws-backup-*{ArchiveExtension}"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
            {
                File.Delete(file);
            }
        }
    }

    public string? GetLatestBackupPath() => !Directory.Exists(_options.Path) ? null
        : Directory.EnumerateFiles(_options.Path, $"gws-backup-*{ArchiveExtension}")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    public string? GetReadinessProblem()
    {
        var latest = GetLatestBackupPath();
        if (latest is null) return "No encrypted GWS backup is available.";
        if (!BackupArchive.HasHeader(latest)) return "The latest backup does not have an authenticated GWS archive header.";
        if (!Directory.Exists(_options.DataProtectionKeysPath)
            || !Directory.EnumerateFiles(_options.DataProtectionKeysPath, "*", SearchOption.AllDirectories).Any())
            return "The matching Data Protection key ring is unavailable.";
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.EncryptionKey)) _ = BackupArchive.ParseKey(_options.EncryptionKey);
            else if (File.Exists(_options.EncryptionKeyPath)) _ = BackupArchive.ParseKey(File.ReadAllText(_options.EncryptionKeyPath));
            else return "The backup encryption key is unavailable.";
        }
        catch (InvalidOperationException ex) { return ex.Message; }
        return null;
    }

    public Task<BackupVerificationResult> VerifyLatestBackupAsync(CancellationToken cancellationToken = default)
    {
        var path = GetLatestBackupPath() ?? throw new InvalidOperationException("No encrypted GWS backup is available.");
        return VerifyBackupAsync(path, cancellationToken);
    }

    public async Task<BackupVerificationResult> VerifyBackupAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var restoreDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gws-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(restoreDirectory);
        var plaintextArchive = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gws-restore-{Guid.NewGuid():N}.zip");
        try
        {
            await BackupArchive.DecryptAsync(archivePath, plaintextArchive, GetOrCreateEncryptionKey(), cancellationToken);
            ZipFile.ExtractToDirectory(plaintextArchive, restoreDirectory);
            var manifestPath = System.IO.Path.Combine(restoreDirectory, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("The backup manifest is missing.");
            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath, cancellationToken), BackupArchive.JsonOptions)
                ?? throw new InvalidDataException("The backup manifest is invalid.");
            await BackupArchive.VerifyManifestAsync(restoreDirectory, manifest, cancellationToken);

            var databasePath = System.IO.Path.Combine(restoreDirectory, "gws-suite.db");
            var keysPath = System.IO.Path.Combine(restoreDirectory, "data-protection-keys");
            if (!File.Exists(databasePath) || !Directory.Exists(keysPath)
                || !Directory.EnumerateFiles(keysPath, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("The database or matching Data Protection key ring is missing.");

            var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={databasePath}").Options;
            await using var restoredDb = new ApplicationDbContext(dbOptions);
            await MigrationHistoryCompatibility.NormalizeAsync(restoredDb, cancellationToken);
            await restoredDb.Database.MigrateAsync(cancellationToken);
            await using var integrity = restoredDb.Database.GetDbConnection().CreateCommand();
            await restoredDb.Database.OpenConnectionAsync(cancellationToken);
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals(Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite integrity verification failed.");

            var audit = new SecurityAuditService(new RestoredDbContextFactory(dbOptions), FixedCurrentUserAccessor.Unknown,
                new RestoredSecretProtector(keysPath), TimeProvider.System);
            var auditIntegrity = await audit.VerifyIntegrityAsync(cancellationToken);
            if (!auditIntegrity.IsValid) throw new InvalidDataException($"Security audit integrity failed: {auditIntegrity.FailureReason}");

            var secretProtector = new RestoredSecretProtector(keysPath);
            var protectedSecretsChecked = await VerifyProtectedSecretsAsync(restoredDb, secretProtector, cancellationToken);
            var activeAdmins = await restoredDb.AppUsers.CountAsync(x => x.IsActive && x.Role == GwsBusinessSuite.Domain.Entities.AppRoles.Admin, cancellationToken);
            var mfaAdmins = await restoredDb.AppUsers.CountAsync(x => x.IsActive && x.Role == GwsBusinessSuite.Domain.Entities.AppRoles.Admin && x.MfaEnabled, cancellationToken);
            if (activeAdmins == 0 || mfaAdmins != activeAdmins) throw new InvalidDataException("The restored database does not have MFA enabled for every active administrator.");
            var pages = await restoredDb.WikiPages.CountAsync(cancellationToken);
            return new(archivePath, manifest.CreatedAtUtc, manifest.Files.Count,
                manifest.Files.Count(x => x.Path.StartsWith("live-show-recordings/", StringComparison.Ordinal)),
                activeAdmins, pages, auditIntegrity.EventsChecked, protectedSecretsChecked, true);
        }
        finally
        {
            if (File.Exists(plaintextArchive)) File.Delete(plaintextArchive);
            if (Directory.Exists(restoreDirectory)) Directory.Delete(restoreDirectory, true);
        }
    }

    private byte[] GetOrCreateEncryptionKey()
    {
        if (!string.IsNullOrWhiteSpace(_options.EncryptionKey)) return BackupArchive.ParseKey(_options.EncryptionKey);
        if (File.Exists(_options.EncryptionKeyPath)) return BackupArchive.ParseKey(File.ReadAllText(_options.EncryptionKeyPath));
        var directory = System.IO.Path.GetDirectoryName(_options.EncryptionKeyPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            using var keyFile = new FileStream(_options.EncryptionKeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(keyFile);
            writer.Write(Convert.ToBase64String(key));
            writer.Flush();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_options.EncryptionKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            logger.LogWarning("Generated a backup encryption key at {KeyPath}. Escrow this key separately from the backup volume.", _options.EncryptionKeyPath);
            return key;
        }
        catch (IOException) when (File.Exists(_options.EncryptionKeyPath))
        {
            CryptographicOperations.ZeroMemory(key);
            return BackupArchive.ParseKey(File.ReadAllText(_options.EncryptionKeyPath));
        }
    }

    private static async Task<int> VerifyProtectedSecretsAsync(ApplicationDbContext db, ISecretProtector protector, CancellationToken cancellationToken)
    {
        var values = new List<string>();
        values.AddRange(await db.NotionConnectorSettings.AsNoTracking().Select(x => x.IntegrationToken).Where(x => x != "").ToListAsync(cancellationToken));
        values.AddRange(await db.SocialAccounts.AsNoTracking().Select(x => x.ProtectedAccessToken).Where(x => x != "").ToListAsync(cancellationToken));
        values.AddRange(await db.AutomationCredentials.AsNoTracking().Select(x => x.ProtectedData).Where(x => x != "").ToListAsync(cancellationToken));
        foreach (var value in values) _ = protector.Unprotect(value);
        return values.Count;
    }

    private sealed class RestoredDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class RestoredSecretProtector : ISecretProtector
    {
        private readonly IDataProtector _protector;
        public RestoredSecretProtector(string keysPath) => _protector = DataProtectionProvider
            .Create(new DirectoryInfo(keysPath), options => options.SetApplicationName("GwsBusinessSuite"))
            .CreateProtector("GwsBusinessSuite.Secrets.v1");
        public string Protect(string plaintext) => _protector.Protect(plaintext);
        public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
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

public sealed record BackupVerificationResult(string ArchivePath, DateTimeOffset CreatedAtUtc,
    int FileCount, int RecordingFileCount, int ActiveAdministratorCount, int SentinelPageCount, int AuditEventCount,
    int ProtectedSecretsChecked, bool IsValid);

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
