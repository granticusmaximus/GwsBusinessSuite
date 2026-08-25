using FluentAssertions;
using GwsBusinessSuite.Application.MindMaps;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class MindMapServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_ShouldSeedASingleRootNodeNamedAfterTheTitle()
    {
        await using var fixture = await Fixture.CreateAsync();

        var id = await fixture.Service.CreateAsync("alice", "Roadmap");
        var detail = await fixture.Service.GetByIdAsync("alice", id);

        detail.Should().NotBeNull();
        detail!.Title.Should().Be("Roadmap");
        detail.Root.Topic.Should().Be("Roadmap");
        detail.Root.Children.Should().BeEmpty();
    }

    [Fact]
    public async Task ListForOwnerAsync_ShouldOnlyReturnThatOwnersMindMapsInCreationOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync("alice", "First");
        await fixture.Service.CreateAsync("alice", "Second");
        await fixture.Service.CreateAsync("BOB", "Bob's map");

        var alice = await fixture.Service.ListForOwnerAsync(" Alice ");

        alice.Select(item => item.Title).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task SaveTreeAsync_ShouldRoundTripAddedNodesThroughJson()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Service.CreateAsync("alice", "Roadmap");
        var detail = (await fixture.Service.GetByIdAsync("alice", id))!;
        var child = new MindMapNode(Guid.NewGuid(), "Fundamentals", []);
        var updatedRoot = detail.Root with { Children = [child] };

        await fixture.Service.SaveTreeAsync("alice", id, updatedRoot);
        var reloaded = await fixture.Service.GetByIdAsync("alice", id);

        reloaded!.Root.Children.Should().ContainSingle().Which.Topic.Should().Be("Fundamentals");
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNullForAnotherOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Service.CreateAsync("alice", "Private map");

        (await fixture.Service.GetByIdAsync("bob", id)).Should().BeNull();
    }

    [Fact]
    public async Task RenameAsync_ShouldFailClosedForAnotherOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Service.CreateAsync("alice", "Roadmap");

        var act = () => fixture.Service.RenameAsync("bob", id, "Hijacked");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [Fact]
    public async Task DeleteAsync_ShouldFailClosedForAnotherOwnerAndRemoveForTheRealOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Service.CreateAsync("alice", "Roadmap");

        var act = () => fixture.Service.DeleteAsync("bob", id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");

        await fixture.Service.DeleteAsync("alice", id);
        (await fixture.Service.ListForOwnerAsync("alice")).Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new MindMapService(db, new FixedTimeProvider(Now));
        }

        public ApplicationDbContext Db { get; }
        public MindMapService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
