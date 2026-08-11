using FluentAssertions;
using GwsBusinessSuite.Web.HealthChecks;
using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class DiskSpaceHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthy_WhenNoDataPathsAreConfigured()
    {
        var check = CreateCheck(backupPath: "", connectionString: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No data paths configured");
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReportHealthy_ForARealDirectoryWithPlentyOfFreeSpace()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gws-disk-space-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var check = CreateCheck(backupPath: directory, connectionString: null);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            result.Status.Should().Be(HealthStatus.Healthy);
            result.Description.Should().Contain(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldWalkUpToTheNearestExistingAncestor_WhenTheConfiguredPathDoesNotExistYet()
    {
        // A fresh deploy hasn't created the backup directory yet (DatabaseBackupService only
        // creates it on first backup) - the health check must still succeed by measuring the
        // nearest real ancestor, not throw because the exact leaf directory is missing.
        var parent = Path.Combine(Path.GetTempPath(), $"gws-disk-space-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        var notYetCreated = Path.Combine(parent, "backups", "nested", "path");
        try
        {
            var check = CreateCheck(backupPath: notYetCreated, connectionString: null);

            var act = async () => await check.CheckHealthAsync(new HealthCheckContext());

            var result = await act.Should().NotThrowAsync();
            result.Which.Status.Should().Be(HealthStatus.Healthy);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldDeriveTheDatabaseDirectory_FromTheSqliteConnectionString_AndDeduplicateAgainstTheBackupPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gws-disk-space-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var dbFile = Path.Combine(directory, "gws.db");
            var check = CreateCheck(backupPath: directory, connectionString: $"Data Source={dbFile}");

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            result.Status.Should().Be(HealthStatus.Healthy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldIgnoreAnInMemorySqliteConnectionString()
    {
        var check = CreateCheck(backupPath: "", connectionString: "Data Source=:memory:");

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Contain("No data paths configured");
    }

    private static DiskSpaceHealthCheck CreateCheck(string backupPath, string? connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(connectionString is null
                ? []
                : new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connectionString })
            .Build();
        var backupOptions = Options.Create(new DatabaseBackupOptions { Path = backupPath });
        return new DiskSpaceHealthCheck(configuration, backupOptions);
    }
}
