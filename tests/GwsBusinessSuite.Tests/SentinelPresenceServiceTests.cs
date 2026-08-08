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
    public async Task ListAsync_ShouldFilterAndDeleteExpiredLeasesViaTheUnixSecondsShadowColumn()
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
            var recentSeen = now.AddMinutes(-1);
            var expiredSeen = now.Subtract(SentinelPresenceTracker.SessionTimeout).AddSeconds(-1);
            db.SentinelPresenceLeases.AddRange(
                new SentinelPresenceLease
                {
                    WikiPageId = pageId,
                    Username = "Grant",
                    LastSeenAt = recentSeen,
                    LastSeenAtUnixSeconds = recentSeen.ToUnixTimeSeconds()
                },
                new SentinelPresenceLease
                {
                    WikiPageId = pageId,
                    Username = "Expired",
                    LastSeenAt = expiredSeen,
                    LastSeenAtUnixSeconds = expiredSeen.ToUnixTimeSeconds()
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

    [Fact]
    public async Task ListAsync_ShouldOnlyReturnLeasesForTheRequestedPage()
    {
        // Regression guard: ListAsync used to load every presence lease across the entire
        // workspace on every poll of every page, filtering to the requested page only after
        // materializing everything. The WikiPageId filter must be pushed down as a real SQL
        // WHERE so another page's active presence never has to be fetched to answer this one.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var pageA = Guid.NewGuid();
        var pageB = Guid.NewGuid();
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.SentinelPresenceLeases.AddRange(
                new SentinelPresenceLease { WikiPageId = pageA, Username = "Grant", LastSeenAt = now, LastSeenAtUnixSeconds = now.ToUnixTimeSeconds() },
                new SentinelPresenceLease { WikiPageId = pageB, Username = "Someone Else", LastSeenAt = now, LastSeenAtUnixSeconds = now.ToUnixTimeSeconds() });
            await db.SaveChangesAsync();
        }

        var service = new SentinelPresenceService(new TestDbContextFactory(options), new FixedTimeProvider(now));
        var presence = await service.ListAsync(pageA);

        presence.Should().ContainSingle(item => item.Username == "Grant");
        await using var verificationDb = new ApplicationDbContext(options);
        (await verificationDb.SentinelPresenceLeases.CountAsync()).Should().Be(2, "leases on other pages must survive untouched");
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
