using FluentAssertions;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task GetOrCreateDirectThreadAsync_ShouldReturnTheSameThread_OnRepeatedCalls()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");
        var second = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");
        var reversed = await fixture.Service.GetOrCreateDirectThreadAsync("bob", "alice");

        second.Should().Be(first, "asking for the same pair twice should reuse the existing thread");
        reversed.Should().Be(first, "the pair order shouldn't matter");
        (await fixture.Db.ChatThreads.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateDirectThreadAsync_ShouldThrow_ForTheSameUserTwice()
    {
        await using var fixture = await Fixture.CreateAsync();

        var action = async () => await fixture.Service.GetOrCreateDirectThreadAsync("alice", "alice");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SendMessageAsync_ThenGetMessagesAsync_ShouldReturnMessagesInOrder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");

        await fixture.Service.SendMessageAsync(threadId, "alice", "Hey, got a minute?");
        await fixture.Service.SendMessageAsync(threadId, "bob", "Sure, what's up?");

        var messages = await fixture.Service.GetMessagesAsync(threadId, "alice");

        messages.Select(m => m.Body).Should().Equal("Hey, got a minute?", "Sure, what's up?");
    }

    [Fact]
    public async Task GetMessagesAsync_ShouldThrow_ForANonParticipant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");
        await fixture.Service.SendMessageAsync(threadId, "alice", "Private conversation");

        var action = async () => await fixture.Service.GetMessagesAsync(threadId, "eve");

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldThrow_ForANonParticipant()
    {
        await using var fixture = await Fixture.CreateAsync();
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");

        var action = async () => await fixture.Service.SendMessageAsync(threadId, "eve", "Can I join?");

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SendMessageAsync_ShouldThrow_ForAnEmptyBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");

        var action = async () => await fixture.Service.SendMessageAsync(threadId, "alice", "   ");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListThreadsForUserAsync_ShouldShowUnreadCount_ThatDropsToZeroAfterMarkingRead()
    {
        await using var fixture = await Fixture.CreateAsync();
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");
        await fixture.Service.SendMessageAsync(threadId, "bob", "First message");
        await fixture.Service.SendMessageAsync(threadId, "bob", "Second message");

        var beforeRead = await fixture.Service.ListThreadsForUserAsync("alice");
        beforeRead.Single().UnreadCount.Should().Be(2);

        await fixture.Service.MarkThreadReadAsync(threadId, "alice");

        var afterRead = await fixture.Service.ListThreadsForUserAsync("alice");
        afterRead.Single().UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task ListThreadsForUserAsync_ShouldNotCountOwnMessagesAsUnread()
    {
        await using var fixture = await Fixture.CreateAsync();
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");
        await fixture.Service.SendMessageAsync(threadId, "alice", "My own message");

        var threads = await fixture.Service.ListThreadsForUserAsync("alice");

        threads.Single().UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task ListThreadsForUserAsync_ShouldShowTheOtherParticipantsDisplayNameAsTheTitle()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddUser("bob");
        await fixture.Db.SaveChangesAsync();
        await fixture.DirectoryService.SaveProfileAsync(new MemberProfileEditorModel { Username = "bob", DisplayName = "Bob Smith" }, "bob");
        var threadId = await fixture.Service.GetOrCreateDirectThreadAsync("alice", "bob");
        await fixture.Service.SendMessageAsync(threadId, "bob", "Hi");

        var threads = await fixture.Service.ListThreadsForUserAsync("alice");

        threads.Single().Title.Should().Be("Bob Smith");
    }

    [Fact]
    public async Task ListThreadsForUserAsync_ShouldReturnEmpty_ForAUserWithNoThreads()
    {
        await using var fixture = await Fixture.CreateAsync();

        var threads = await fixture.Service.ListThreadsForUserAsync("nobody");

        threads.Should().BeEmpty();
    }

    private sealed class Fixture(SqliteConnection connection, ApplicationDbContext db) : IAsyncDisposable
    {
        public ApplicationDbContext Db { get; } = db;
        public ChatService Service { get; } = new(db);
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
