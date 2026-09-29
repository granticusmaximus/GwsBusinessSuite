using FluentAssertions;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class ActivityFeedServiceTests
{
    [Fact]
    public async Task RecordAsync_ThenGetRecentAsync_ShouldReturnNewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("jdoe");
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.RecordAsync("jdoe", "edited the wiki page", "Onboarding Guide", "/admin/wiki?page=1");
        await fixture.Service.RecordAsync("jdoe", "created a new CRM contact", "Acme Corp", "/admin/crm/contacts/1");

        var recent = await fixture.Service.GetRecentAsync();

        recent.Should().HaveCount(2);
        recent[0].TargetLabel.Should().Be("Acme Corp", "the most recently recorded event should come first");
        recent[1].TargetLabel.Should().Be("Onboarding Guide");
    }

    [Fact]
    public async Task GetRecentAsync_ShouldResolveTheActorsDisplayName_WhenAProfileExists()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("jdoe");
        await fixture.Db.SaveChangesAsync();
        await fixture.DirectoryService.SaveProfileAsync(new MemberProfileEditorModel { Username = "jdoe", DisplayName = "Jane Doe" }, "jdoe");

        await fixture.Service.RecordAsync("jdoe", "edited the wiki page", "Onboarding Guide", "/admin/wiki?page=1");

        var recent = await fixture.Service.GetRecentAsync();

        recent.Single().ActorDisplayName.Should().Be("Jane Doe");
        recent.Single().ActorUsername.Should().Be("jdoe");
    }

    [Fact]
    public async Task GetRecentAsync_ShouldFallBackToUsername_WhenNoProfileExists()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("jdoe");
        await fixture.Db.SaveChangesAsync();

        await fixture.Service.RecordAsync("jdoe", "edited the wiki page", "Onboarding Guide", "/admin/wiki?page=1");

        var recent = await fixture.Service.GetRecentAsync();

        recent.Single().ActorDisplayName.Should().Be("jdoe");
    }

    [Fact]
    public async Task GetRecentAsync_ShouldClampTakeToAReasonableRange()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("jdoe");
        await fixture.Db.SaveChangesAsync();
        for (var i = 0; i < 5; i++)
        {
            await fixture.Service.RecordAsync("jdoe", "did something", $"Item {i}", "/admin");
        }

        var recent = await fixture.Service.GetRecentAsync(take: 2);

        recent.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRecentAsync_ShouldReturnEmpty_WhenNoActivityHasBeenRecorded()
    {
        await using var fixture = await Fixture.CreateAsync();

        var recent = await fixture.Service.GetRecentAsync();

        recent.Should().BeEmpty();
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db) : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; } = db;
        public ActivityFeedService Service { get; } = new(db);
        public CommunityDirectoryService DirectoryService { get; } = new(db);

        public AppUser AddUser(string username)
        {
            var user = new AppUser { Username = username };
            Db.AppUsers.Add(user);
            return user;
        }

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
            await connection.DisposeAsync();
        }
    }
}
