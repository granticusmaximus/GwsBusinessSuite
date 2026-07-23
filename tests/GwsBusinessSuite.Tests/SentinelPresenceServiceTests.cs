using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelPresenceServiceTests
{
    [Fact]
    public async Task ListAsync_ShouldFilterAndDeleteExpiredSqliteLeasesWithoutServerComparison()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var pageId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
            db.SentinelPresenceLeases.AddRange(
                new SentinelPresenceLease
                {
                    WikiPageId = pageId,
                    Username = "Grant",
                    LastSeenAt = now.AddMinutes(-1)
                },
                new SentinelPresenceLease
                {
                    WikiPageId = pageId,
                    Username = "Expired",
                    LastSeenAt = now.Subtract(SentinelPresenceTracker.SessionTimeout).AddSeconds(-1)
                });
            await db.SaveChangesAsync();
        }

        var service = new SentinelPresenceService(
            new TestDbContextFactory(options),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)));
        Guid selectedPageId;
        await using (var db = new ApplicationDbContext(options))
        {
            selectedPageId = (await db.SentinelPresenceLeases.SingleAsync(item => item.Username == "Grant")).WikiPageId;
        }

        var presence = await service.ListAsync(selectedPageId);

        presence.Should().ContainSingle(item => item.Username == "Grant");
        await using var verificationDb = new ApplicationDbContext(options);
        (await verificationDb.SentinelPresenceLeases.Select(item => item.Username).ToListAsync())
            .Should().Equal("Grant");
    }

    private sealed class TestDbContextFactory(DbContextOptions<ApplicationDbContext> options)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);

        public Task<ApplicationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
