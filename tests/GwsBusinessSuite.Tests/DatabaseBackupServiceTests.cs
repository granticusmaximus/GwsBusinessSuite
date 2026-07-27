using System.IO.Compression;
using FluentAssertions;
using GwsBusinessSuite.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"gws-backup-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateBackupAsync_ShouldCaptureConsistentDatabaseAndProtectionKeys()
    {
        var databasePath = Path.Combine(_root, "data", "source.db");
        var backupPath = Path.Combine(_root, "backups");
        var keysPath = Path.Combine(_root, "keys");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Directory.CreateDirectory(keysPath);
        await File.WriteAllTextAsync(Path.Combine(keysPath, "key.xml"), "<key />");

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Pages (Title TEXT NOT NULL); INSERT INTO Pages VALUES ('Recovered page');";
            await command.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={databasePath}"
            })
            .Build();
        var options = Options.Create(new DatabaseBackupOptions
        {
            Enabled = true,
            Path = backupPath,
            DataProtectionKeysPath = keysPath
        });
        var service = new DatabaseBackupService(
            configuration,
            options,
            NullLogger<DatabaseBackupService>.Instance);

        var archivePath = await service.CreateBackupAsync();

        File.Exists(archivePath).Should().BeTrue();
        service.GetLatestBackupTime().Should().NotBeNull();
        using var archive = ZipFile.OpenRead(archivePath);
        archive.Entries.Select(entry => entry.FullName).Should().Contain("gws-suite.db");
        archive.Entries.Select(entry => entry.FullName).Should().Contain("data-protection-keys/key.xml");

        var restoredDatabasePath = Path.Combine(_root, "restored.db");
        archive.GetEntry("gws-suite.db")!.ExtractToFile(restoredDatabasePath);
        await using var restored = new SqliteConnection($"Data Source={restoredDatabasePath}");
        await restored.OpenAsync();
        await using var query = restored.CreateCommand();
        query.CommandText = "SELECT Title FROM Pages";
        (await query.ExecuteScalarAsync()).Should().Be("Recovered page");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
