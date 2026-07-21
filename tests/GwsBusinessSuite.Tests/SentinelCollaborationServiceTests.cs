using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelCollaborationServiceTests
{
    [Fact]
    public void OpenBlockCounts_ShouldGroupOpenBlockThreadsOnly()
    {
        var firstBlockId = Guid.NewGuid();
        var secondBlockId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var discussions = new[]
        {
            new SentinelDiscussionView(Guid.NewGuid(), Guid.NewGuid(), firstBlockId, false, null, null, now, []),
            new SentinelDiscussionView(Guid.NewGuid(), Guid.NewGuid(), firstBlockId, false, null, null, now, []),
            new SentinelDiscussionView(Guid.NewGuid(), Guid.NewGuid(), firstBlockId, true, now, "owner", now, []),
            new SentinelDiscussionView(Guid.NewGuid(), Guid.NewGuid(), secondBlockId, false, null, null, now, []),
            new SentinelDiscussionView(Guid.NewGuid(), Guid.NewGuid(), null, false, null, null, now, [])
        };

        var counts = SentinelDiscussionSummary.OpenBlockCounts(discussions);

        counts.Should().BeEquivalentTo(new Dictionary<Guid, int>
        {
            [firstBlockId] = 2,
            [secondBlockId] = 1
        });
    }

    [Fact]
    public async Task CreateDiscussionAsync_ShouldAttachToBlockAndNotifyOwnerAndMentionedUser()
    {
        await using var fixture = await Fixture.CreateAsync();
        var blockId = Guid.NewGuid();
        var page = await fixture.CreatePageAsync(blockId);

        var discussion = await fixture.Service.CreateDiscussionAsync(
            page.Id, blockId, "Please review this, @Reviewer.", "Member");

        discussion.BlockId.Should().Be(blockId);
        discussion.Comments.Should().ContainSingle(comment => comment.Author == "member");
        fixture.Changes.Should().ContainSingle(change =>
            change.WikiPageId == page.Id && change.Kind == "discussion-created" && change.Actor == "member");
        (await fixture.Service.ListNotificationsAsync("Owner"))
            .Should().ContainSingle(notification => notification.Kind == "discussion-created");
        (await fixture.Service.ListNotificationsAsync("Reviewer"))
            .Should().ContainSingle(notification => notification.Message.Contains("review"));
        (await fixture.Service.ListNotificationsAsync("Member")).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateDiscussionAsync_ShouldRejectMissingBlock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreatePageAsync(Guid.NewGuid());

        var action = () => fixture.Service.CreateDiscussionAsync(
            page.Id, Guid.NewGuid(), "Comment", "Member");

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*block*this discussion targets*");
    }

    [Fact]
    public async Task ReplyResolveAndReaction_ShouldUpdateThreadAndCreateNotifications()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreatePageAsync(Guid.NewGuid());
        var discussion = await fixture.Service.CreateDiscussionAsync(page.Id, null, "Initial", "Member");
        var initialComment = discussion.Comments.Single();

        await fixture.Service.ReplyAsync(discussion.Id, initialComment.Id, "Owner reply", "Owner");
        await fixture.Service.ToggleReactionAsync(initialComment.Id, "👍", "Reviewer");
        await fixture.Service.SetResolvedAsync(discussion.Id, true, "Owner");

        var threads = await fixture.Service.ListDiscussionsAsync(page.Id, "Reviewer", includeResolved: true);
        threads.Should().ContainSingle();
        threads[0].IsResolved.Should().BeTrue();
        threads[0].Comments.Should().HaveCount(2);
        threads[0].Comments[0].Reactions.Should().ContainSingle(reaction =>
            reaction.Emoji == "👍" && reaction.Count == 1 && reaction.ReactedByCurrentUser);
        (await fixture.Service.ListDiscussionsAsync(page.Id, "Reviewer")).Should().BeEmpty();
        (await fixture.Service.ListNotificationsAsync("Member"))
            .Should().Contain(notification => notification.Kind == "discussion-reply");
    }

    [Fact]
    public async Task ReplyAsync_ShouldRejectResolvedDiscussionUntilReopened()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreatePageAsync(Guid.NewGuid());
        var discussion = await fixture.Service.CreateDiscussionAsync(page.Id, null, "Initial", "Member");
        await fixture.Service.SetResolvedAsync(discussion.Id, true, "Owner");

        var action = () => fixture.Service.ReplyAsync(discussion.Id, null, "Too late", "Reviewer");

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("Reopen*");
    }

    [Fact]
    public async Task Notifications_ShouldOnlyBeMarkableByTheirRecipient()
    {
        await using var fixture = await Fixture.CreateAsync();
        var page = await fixture.CreatePageAsync(Guid.NewGuid());
        await fixture.Service.CreateDiscussionAsync(page.Id, null, "Initial", "Member");
        var notification = (await fixture.Service.ListNotificationsAsync("Owner")).Single();

        await fixture.Service.MarkNotificationReadAsync(notification.Id, "Reviewer");
        (await fixture.Service.ListNotificationsAsync("Owner", unreadOnly: true)).Should().ContainSingle();

        await fixture.Service.MarkNotificationReadAsync(notification.Id, "Owner");
        (await fixture.Service.ListNotificationsAsync("Owner", unreadOnly: true)).Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ApplicationDbContext Db { get; }
        public SentinelCollaborationService Service { get; }
        public List<SentinelCollaborationChange> Changes { get; } = new();

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            var notifier = new SentinelCollaborationNotifier(TimeProvider.System);
            notifier.Changed += Changes.Add;
            Service = new SentinelCollaborationService(db, TimeProvider.System, notifier);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            db.AppUsers.AddRange(
                new AppUser { Username = "Owner", IsActive = true },
                new AppUser { Username = "Member", IsActive = true },
                new AppUser { Username = "Reviewer", IsActive = true });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public Task<WikiPage> CreatePageAsync(Guid blockId) => new WikiService(Db).SavePageAsync(
            new WikiPageEditorModel
            {
                Title = "Runbook",
                BlocksJson = WikiBlockJson.Serialize([
                    new WikiBlock(blockId, WikiBlockTypes.Paragraph, 0,
                        [new WikiRichTextSpan("Launch checklist")], new Dictionary<string, string>())])
            },
            "Owner");

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
