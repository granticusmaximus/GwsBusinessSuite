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
        verification.FileCount.Should().BeGreaterThanOrEqualTo(3);
        fixture.Service.GetLatestBackupTime().Should().NotBeNull();
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

        var provider = DataProtectionProvider.Create(new DirectoryInfo(keysPath), options => options.SetApplicationName("GwsBusinessSuite"));
        var protector = new DataProtectionSecretProtector(provider);
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={databasePath}").Options;
        await using (var db = new ApplicationDbContext(dbOptions))
        {
            await db.Database.MigrateAsync();
            db.AppUsers.Add(new AppUser { Username = "admin", Role = AppRoles.Admin, IsActive = true, MfaEnabled = true, PasswordHash = "hash" });
            db.WikiPages.Add(new WikiPage { Title = "Recovered page", Slug = "recovered-page" });
            db.NotionConnectorSettings.Add(new NotionConnectorSettings { Id = NotionConnectorSettings.WellKnownId, IntegrationToken = protector.Protect("test-token") });
            await db.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}" }).Build();
        var options = Options.Create(new DatabaseBackupOptions
        {
            Enabled = true, Path = backupPath, DataProtectionKeysPath = keysPath,
            LiveShowRecordingsPath = recordingsPath,
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
