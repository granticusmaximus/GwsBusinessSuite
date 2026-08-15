using FluentAssertions;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class DeveloperApiKeyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IssueAsync_ShouldReturnPlaintextOnceAndPersistOnlyItsHash()
    {
        await using var fixture = await Fixture.CreateAsync();

        var issued = await fixture.Service.IssueAsync(
            "Reporting", [DeveloperApiScopes.DealsRead, DeveloperApiScopes.ContactsRead], 75, Now.AddDays(30), "grant");
        var stored = await fixture.Db.DeveloperApiKeys.SingleAsync();

        issued.PlaintextKey.Should().StartWith(stored.KeyPrefix + "_");
        issued.PlaintextKey.Should().NotBe(stored.KeyHash);
        stored.KeyHash.Should().HaveLength(64);
        stored.ScopesCsv.Should().Be("contacts:read,deals:read");
        stored.GetType().GetProperties().Where(property => property.PropertyType == typeof(string))
            .Select(property => property.GetValue(stored) as string)
            .Should().NotContain(issued.PlaintextKey);
    }

    [Fact]
    public async Task AuthenticateAsync_ShouldValidateTheSecretAndRecordUsage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issued = await fixture.Service.IssueAsync("Sync", [DeveloperApiScopes.ContactsRead], 60, null, "grant");

        var authenticated = await fixture.Service.AuthenticateAsync(issued.PlaintextKey);
        var replacement = issued.PlaintextKey[^1] == '0' ? "1" : "0";
        var rejected = await fixture.Service.AuthenticateAsync(issued.PlaintextKey[..^1] + replacement);
        var stored = await fixture.Db.DeveloperApiKeys.AsNoTracking().SingleAsync();

        authenticated.Should().NotBeNull();
        authenticated!.Scopes.Should().ContainSingle(DeveloperApiScopes.ContactsRead);
        rejected.Should().BeNull();
        stored.RequestCount.Should().Be(1);
        stored.LastUsedAt.Should().Be(Now);
    }

    [Fact]
    public async Task RevokeAsync_ShouldImmediatelyRejectTheKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issued = await fixture.Service.IssueAsync("Sync", [DeveloperApiScopes.ContactsRead], 60, null, "grant");

        await fixture.Service.RevokeAsync(issued.Key.Id, "grant");

        (await fixture.Service.AuthenticateAsync(issued.PlaintextKey)).Should().BeNull();
        (await fixture.Service.ListAsync()).Single().RevokedAt.Should().Be(Now);
    }

    [Fact]
    public async Task AuthenticateAsync_ShouldRejectAnExpiredKey()
    {
        await using var fixture = await Fixture.CreateAsync();
        var issued = await fixture.Service.IssueAsync(
            "Temporary", [DeveloperApiScopes.CmsPagesRead], 60, Now.AddMinutes(1), "grant");
        fixture.Clock.UtcNow = Now.AddMinutes(2);

        (await fixture.Service.AuthenticateAsync(issued.PlaintextKey)).Should().BeNull();
    }

    [Theory]
    [InlineData("unknown:scope", 60)]
    [InlineData("contacts:read", 0)]
    [InlineData("contacts:read", 601)]
    public async Task IssueAsync_ShouldRejectInvalidScopeOrRateLimit(string scope, int rateLimit)
    {
        await using var fixture = await Fixture.CreateAsync();
        var act = () => fixture.Service.IssueAsync("Invalid", [scope], rateLimit, null, "grant");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db, MutableTimeProvider clock)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            Service = new DeveloperApiKeyService(db, clock);
        }
        public ApplicationDbContext Db { get; }
        public MutableTimeProvider Clock { get; }
        public DeveloperApiKeyService Service { get; }
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, new MutableTimeProvider { UtcNow = Now });
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
