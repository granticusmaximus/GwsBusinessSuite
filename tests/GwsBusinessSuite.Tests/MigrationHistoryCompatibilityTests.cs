using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class MigrationHistoryCompatibilityTests
{
    [Fact]
    public async Task NormalizeAsync_ShouldMapReplacementMigrationIdToCanonicalId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL);
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260721154652_AddSentinelParityRelease', '10.0.8');
            """);

        await MigrationHistoryCompatibility.NormalizeAsync(db);

        var ids = await ReadMigrationIdsAsync(connection);
        ids.Should().Contain(MigrationHistoryCompatibility.SentinelParityCanonicalId);
        ids.Should().Contain(MigrationHistoryCompatibility.SentinelParityReplacementId);
    }

    [Fact]
    public async Task NormalizeAsync_ShouldIgnoreANewDatabaseWithoutMigrationHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var action = () => MigrationHistoryCompatibility.NormalizeAsync(db);

        await action.Should().NotThrowAsync();
    }

    [Fact]
    public async Task MigrationAssembly_ShouldExposeOnlyTheCanonicalSentinelParityMigration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var migrations = db.Database.GetMigrations();

        migrations.Should().Contain(MigrationHistoryCompatibility.SentinelParityCanonicalId);
        migrations.Should().NotContain(MigrationHistoryCompatibility.SentinelParityReplacementId);
    }

    [Fact]
    public async Task CanonicalSentinelParityMigration_ShouldApplyToAFreshDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        await db.Database.MigrateAsync(MigrationHistoryCompatibility.SentinelParityCanonicalId);

        (await db.Database.GetAppliedMigrationsAsync())
            .Should().Contain(MigrationHistoryCompatibility.SentinelParityCanonicalId);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('WikiDatabaseViews') WHERE name = 'NotionId';";
        Convert.ToInt64(await command.ExecuteScalarAsync()).Should().Be(1);
    }

    private static ApplicationDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);

    private static async Task<IReadOnlyList<string>> ReadMigrationIdsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
