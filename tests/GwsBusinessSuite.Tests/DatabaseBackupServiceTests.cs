using System.Security.Cryptography;
using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gws-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAndVerify_ShouldRestoreDatabaseKeysRecordingsAuditAndAuthenticationPrerequisites()
    {
        var fixture = await CreateFixtureAsync();

        var archivePath = await fixture.Service.CreateBackupAsync();
        var verification = await fixture.Service.VerifyBackupAsync(archivePath);

        File.Exists(archivePath).Should().BeTrue();
        Path.GetExtension(archivePath).Should().Be(".gwsbackup");
        (await File.ReadAllBytesAsync(archivePath)).AsSpan().IndexOf("SQLite format 3"u8).Should().Be(-1);
        verification.IsValid.Should().BeTrue();
        verification.ActiveAdministratorCount.Should().Be(1);
        verification.SentinelPageCount.Should().Be(1);
        verification.ProtectedSecretsChecked.Should().Be(1);
        verification.RecordingFileCount.Should().Be(1);
        verification.MediaFileCount.Should().Be(1, "the media library's file-only image");
        verification.FileCount.Should().BeGreaterThanOrEqualTo(3);
        fixture.Service.GetLatestBackupTime().Should().NotBeNull();
    }

    [Fact]
    public async Task RehearseLatestMigration_ShouldRoundTripLatestMigrationOnIsolatedCopyWithoutChangingSource()
    {
        var fixture = await CreateFixtureAsync();
        var archivePath = await fixture.Service.CreateBackupAsync();
        var sourceBefore = SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath));

        string[] migrations;
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={fixture.DatabasePath}").Options;
        await using (var db = new ApplicationDbContext(dbOptions))
            migrations = db.Database.GetMigrations().ToArray();

        var verification = await fixture.Service.RehearseLatestMigrationAsync(archivePath);

        verification.IsValid.Should().BeTrue();
        verification.MigrationRehearsalFrom.Should().Be(migrations[^2]);
        verification.MigrationRehearsalTo.Should().Be(migrations[^1]);
        SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath)).Should().Equal(sourceBefore);
    }

    [Fact]
    public async Task GetReadinessProblemAsync_ShouldDetectATruncatedBackup_EvenThoughItsHeaderIsStillValid()
    {
        // Regression guard for a real finding: the health check previously only verified the
        // archive's 8-byte magic header (BackupArchive.HasHeader), not real integrity - a crash
        // mid-encryption (or any other truncation) leaves a file that still starts with a valid
        // header but is missing part of its ciphertext and/or its trailing HMAC tag, and that
        // still reported Healthy. This simulates exactly that: a real, valid backup truncated
        // after the fact (header intact, tail cut off) should now be flagged.
        var fixture = await CreateFixtureAsync();
        var archivePath = await fixture.Service.CreateBackupAsync();
        (await fixture.Service.GetReadinessProblemAsync()).Should().BeNull("a freshly created backup should verify cleanly");

        var bytes = await File.ReadAllBytesAsync(archivePath);
        await File.WriteAllBytesAsync(archivePath, bytes[..(bytes.Length - 20)]);

        var problem = await fixture.Service.GetReadinessProblemAsync();

        problem.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateBackupAsync_ShouldNotLeaveATempFileBehindOnSuccess()
    {
        // Regression guard for the write-temp-then-atomic-rename fix: EncryptAsync now writes
        // to a sibling .tmp path and only renames it onto the real archive path once both the
        // ciphertext and its HMAC tag are fully written - a crash before that rename previously
        // could leave a partially-written file sitting at the exact path GetLatestBackupPath's
        // directory scan discovers as "the latest backup".
        var fixture = await CreateFixtureAsync();

        var archivePath = await fixture.Service.CreateBackupAsync();

        Directory.EnumerateFiles(Path.GetDirectoryName(archivePath)!, "*.tmp").Should().BeEmpty();
        fixture.Service.GetLatestBackupPath().Should().Be(archivePath);
    }

    [Fact]
    public async Task Verify_ShouldRejectAuthenticatedArchiveAfterSingleByteTampering()
    {
        var fixture = await CreateFixtureAsync();
        var archivePath = await fixture.Service.CreateBackupAsync();
        var bytes = await File.ReadAllBytesAsync(archivePath);
        bytes[bytes.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(archivePath, bytes);

        var action = async () => await fixture.Service.VerifyBackupAsync(archivePath);

        await action.Should().ThrowAsync<CryptographicException>().WithMessage("*authentication failed*");
    }

    [Fact]
    public async Task Verify_ShouldFail_WhenAnImageHeldOnlyOnDiskIsMissingFromTheBackup()
    {
        var fixture = await CreateFixtureAsync();
        File.Delete(Path.Combine(_root, "media", MediaFilePath));
        var archivePath = await fixture.Service.CreateBackupAsync();

        var action = async () => await fixture.Service.VerifyBackupAsync(archivePath);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("1 media file(s) held only on disk are missing*");
    }

    private const string MediaFilePath = "originals/ab/ab000000000000000000000000000001.png";

    private async Task<(DatabaseBackupService Service, string DatabasePath)> CreateFixtureAsync()
    {
        var databasePath = Path.Combine(_root, "data", "source.db");
        var backupPath = Path.Combine(_root, "backups");
        var keysPath = Path.Combine(_root, "keys");
        var recordingsPath = Path.Combine(_root, "recordings");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Directory.CreateDirectory(keysPath);
        Directory.CreateDirectory(recordingsPath);
        await File.WriteAllTextAsync(Path.Combine(recordingsPath, "session.webm"), "recording-evidence");
        // A media library image whose database copy has already been cleared: it exists only as a file.
        var mediaPath = Path.Combine(_root, "media");
        Directory.CreateDirectory(Path.Combine(mediaPath, "originals", "ab"));
        Directory.CreateDirectory(Path.Combine(mediaPath, "variants"));
        await File.WriteAllBytesAsync(Path.Combine(mediaPath, MediaFilePath), [0x89, 0x50, 0x4E, 0x47]);
        await File.WriteAllBytesAsync(Path.Combine(mediaPath, "variants", "cache-only.webp"), [1]);

        var provider = DataProtectionProvider.Create(new DirectoryInfo(keysPath), options => options.SetApplicationName("GwsBusinessSuite"));
        var protector = new DataProtectionSecretProtector(provider);
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={databasePath}").Options;
        await using (var db = new ApplicationDbContext(dbOptions))
        {
            await db.Database.MigrateAsync();
            db.AppUsers.Add(new AppUser { Username = "admin", Role = AppRoles.Admin, IsActive = true, MfaEnabled = true, PasswordHash = "hash" });
            db.WikiPages.Add(new WikiPage { Title = "Recovered page", Slug = "recovered-page" });
            db.MediaAssets.Add(new MediaAsset { FileName = "logo.png", ContentType = "image/png", DataUri = string.Empty, StoragePath = MediaFilePath, SizeBytes = 4 });
            db.AutomationCredentials.Add(new AutomationCredential { Name = "Test", TypeKey = "apiKey", ProtectedData = protector.Protect("test-token") });
            await db.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}" }).Build();
        var options = Options.Create(new DatabaseBackupOptions
        {
            Enabled = true, Path = backupPath, DataProtectionKeysPath = keysPath,
            LiveShowRecordingsPath = recordingsPath,
            MediaPath = Path.Combine(_root, "media"),
            EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            EncryptionKeyPath = Path.Combine(_root, "backup.key")
        });
        return (new DatabaseBackupService(configuration, options, NullLogger<DatabaseBackupService>.Instance), databasePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
