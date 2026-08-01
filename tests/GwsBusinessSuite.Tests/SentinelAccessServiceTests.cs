using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelAccessServiceTests
{
    [Fact]
    public async Task PublicShare_ShouldStoreOnlyHashAndStopResolvingAfterRevocation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var targetId = Guid.NewGuid();

        var created = await fixture.Service.CreatePublicShareAsync(targetId, false, null, false, null, "owner");

        created.PublicToken.Should().NotBeNullOrWhiteSpace();
        var entity = await fixture.Db.SentinelPublicShares.SingleAsync();
        entity.TokenHash.Should().NotBe(created.PublicToken);
        (await fixture.Service.ResolvePublicShareAsync(created.PublicToken!))!.TargetId.Should().Be(targetId);

        await fixture.Service.RevokePublicShareAsync(created.Id, "owner");
        (await fixture.Service.ResolvePublicShareAsync(created.PublicToken!)).Should().BeNull();
    }

    [Fact]
    public async Task PublicShare_WithPassword_ShouldRequireTheCorrectPasswordAndNeverStoreItInPlaintext()
    {
        await using var fixture = await Fixture.CreateAsync();
        var targetId = Guid.NewGuid();

        var created = await fixture.Service.CreatePublicShareAsync(targetId, false, null, false, "hunter2", "owner");

        created.RequiresPassword.Should().BeTrue();
        var entity = await fixture.Db.SentinelPublicShares.SingleAsync();
        entity.PasswordHash.Should().NotBeNullOrWhiteSpace();
        entity.PasswordHash.Should().NotBe("hunter2");

        var resolved = await fixture.Service.ResolvePublicShareAsync(created.PublicToken!);
        resolved!.RequiresPassword.Should().BeTrue();

        (await fixture.Service.VerifySharePasswordAsync(created.Id, "wrong")).Should().BeFalse();
        (await fixture.Service.VerifySharePasswordAsync(created.Id, "hunter2")).Should().BeTrue();
    }

    [Fact]
    public async Task GetAccessAsync_ShouldOrderPublicSharesNewestFirstOnSqlite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var targetId = Guid.NewGuid();
        var older = await fixture.Service.CreatePublicShareAsync(targetId, false, null, false, null, "owner");
        var newer = await fixture.Service.CreatePublicShareAsync(targetId, false, DateTimeOffset.UtcNow.AddDays(1), true, null, "owner");
        var rows = await fixture.Db.SentinelPublicShares.ToListAsync();
        rows.Single(row => row.Id == older.Id).CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        rows.Single(row => row.Id == newer.Id).CreatedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        var snapshot = await fixture.Service.GetAccessAsync(targetId, false);

        snapshot.Shares.Select(share => share.Id).Should().Equal(newer.Id, older.Id);
        snapshot.Shares[0].AllowSearchIndexing.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccess_ShouldApplyPermissionRanksAndOwnerOverride()
    {
        await using var fixture = await Fixture.CreateAsync();
        var targetId = Guid.NewGuid();
        await fixture.Service.SetPermissionAsync(targetId, false, "editor", SentinelAccessLevels.Edit, "owner");
        fixture.Db.SentinelWorkspaceMembers.Add(new SentinelWorkspaceMember
        {
            Username = "owner", Role = SentinelWorkspaceRoles.Owner, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "system"
        });
        await fixture.Db.SaveChangesAsync();

        (await fixture.Service.CanAccessAsync(targetId, false, "editor", SentinelAccessLevels.Comment)).Should().BeTrue();
        (await fixture.Service.CanAccessAsync(targetId, false, "editor", SentinelAccessLevels.FullAccess)).Should().BeFalse();
        (await fixture.Service.CanAccessAsync(Guid.NewGuid(), true, "owner", SentinelAccessLevels.FullAccess)).Should().BeTrue();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new SentinelAccessService(db);
        }

        public ApplicationDbContext Db { get; }
        public SentinelAccessService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
