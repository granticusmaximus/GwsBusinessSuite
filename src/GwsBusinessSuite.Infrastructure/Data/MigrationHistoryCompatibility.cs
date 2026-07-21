using System.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Data;

public static class MigrationHistoryCompatibility
{
    public const string SentinelParityCanonicalId = "20260721154110_AddSentinelParityRelease";
    public const string SentinelParityReplacementId = "20260721154652_AddSentinelParityRelease";

    public static async Task NormalizeAsync(
        ApplicationDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        if (!dbContext.Database.IsSqlite())
        {
            return;
        }

        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var tableCheck = connection.CreateCommand();
            tableCheck.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory');";
            var historyExists = Convert.ToInt64(await tableCheck.ExecuteScalarAsync(cancellationToken)) == 1;
            if (!historyExists)
            {
                return;
            }

            await using var normalize = connection.CreateCommand();
            normalize.CommandText = """
                INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                SELECT $canonicalId, "ProductVersion"
                FROM "__EFMigrationsHistory"
                WHERE "MigrationId" = $replacementId
                  AND NOT EXISTS (
                      SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = $canonicalId)
                LIMIT 1;
                """;
            var canonical = normalize.CreateParameter();
            canonical.ParameterName = "$canonicalId";
            canonical.Value = SentinelParityCanonicalId;
            normalize.Parameters.Add(canonical);
            var replacement = normalize.CreateParameter();
            replacement.ParameterName = "$replacementId";
            replacement.Value = SentinelParityReplacementId;
            normalize.Parameters.Add(replacement);
            await normalize.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
