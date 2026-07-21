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

        var created = await fixture.Service.CreatePublicShareAsync(targetId, false, null, false, "owner");

        created.PublicToken.Should().NotBeNullOrWhiteSpace();
        var entity = await fixture.Db.SentinelPublicShares.SingleAsync();
        entity.TokenHash.Should().NotBe(created.PublicToken);
        (await fixture.Service.ResolvePublicShareAsync(created.PublicToken!))!.TargetId.Should().Be(targetId);

        await fixture.Service.RevokePublicShareAsync(created.Id, "owner");
        (await fixture.Service.ResolvePublicShareAsync(created.PublicToken!)).Should().BeNull();
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
