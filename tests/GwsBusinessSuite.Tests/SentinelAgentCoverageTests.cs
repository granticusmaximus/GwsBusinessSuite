using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

namespace GwsBusinessSuite.Tests;

// The role coverage pass (05-11-26): SentinelGPT can moderate comments, work in the person's own
// mailbox, and read Growth and server health - each area to the same roles as its page.
public sealed class SentinelAgentCoverageTests
{
    private static readonly SentinelAgentUser Admin = new("grant", [AppRoles.Admin]);
    private static readonly SentinelAgentUser Author = new("writer", [AppRoles.Author]);

    private static ISentinelAgentTool Tool(ISentinelAgentToolset toolset, string name) => toolset.Tools.Single(tool => tool.Name == name);

    private static SentinelAgentToolContext As(SentinelAgentUser user) => new(user, Guid.NewGuid());

    [Fact]
    public async Task Comments_ShouldBeModeratedWithUndo_AndDeletingShouldWaitForConfirm()
    {
        var service = new FakeComments();
        var comment = service.Add("Ada", "Lovely post", CommentStatuses.Pending);
        var tools = new CommentAgentTools(service);

        var approve = Tool(tools, "set_comment_status");
        var result = await approve.RunAsync(new JsonObject { ["commentId"] = comment.ToString(), ["status"] = "approve" }, As(Author), default);
        service.StatusOf(comment).Should().Be(CommentStatuses.Approved);
        result.Change!.Summary.Should().Be("Marked Ada's comment on 'The post' Approved");

        await approve.UndoAsync(result.Change.UndoJson!, As(Author), default);
        service.StatusOf(comment).Should().Be(CommentStatuses.Pending, "Undo puts the previous status back");

        Tool(tools, "delete_comment").Risk.Should().Be(SentinelAgentRisk.Confirm);
        (await Tool(tools, "delete_comment").PreviewAsync(new JsonObject { ["commentId"] = comment.ToString() }, As(Author), default))
            .Should().Contain("Ada's comment on 'The post' for good");
        await approve.Invoking(t => t.RunAsync(new JsonObject { ["commentId"] = comment.ToString(), ["status"] = "Hidden" }, As(Author), default))
            .Should().ThrowAsync<SentinelAgentToolException>().WithMessage("*isn't a comment status*");
    }

    [Fact]
    public async Task Mail_ShouldOnlyEverOpenTheSignedInPersonsOwnMailbox_AndSendingShouldWaitForConfirm()
    {
        var mail = new FakeMail();
        var tools = new MailAgentTools(mail);

        var list = await Tool(tools, "list_mail").RunAsync(new JsonObject(), As(Author), default);
        list.ForModel.Should().Contain("writer@example.com", "the first of the person's own mailboxes");
        mail.Usernames.Should().OnlyContain(username => username == "writer", "nobody else's mailbox is touched");

        var named = await Tool(tools, "list_mail").RunAsync(new JsonObject { ["mailbox"] = "WORK@example.com", ["folder"] = "sent" }, As(Author), default);
        named.ForModel.Should().Contain("work@example.com");
        mail.LastFolder.Should().Be(MailFolders.Sent);

        var draft = await Tool(tools, "draft_mail").RunAsync(new JsonObject
        {
            ["to"] = new JsonArray("dana@example.com"), ["subject"] = "Quote", ["body"] = "Here it is", ["replyToMessageId"] = "m1"
        }, As(Author), default);
        mail.Drafts.Should().ContainSingle().Which.ReplyToMessageId.Should().Be("m1");
        await Tool(tools, "draft_mail").UndoAsync(draft.Change!.UndoJson!, As(Author), default);
        mail.Trashed.Should().Equal(["draft-1"], "Undo moves the draft to the trash");

        Tool(tools, "send_mail").Risk.Should().Be(SentinelAgentRisk.Confirm);
        (await Tool(tools, "send_mail").PreviewAsync(new JsonObject { ["to"] = new JsonArray("dana@example.com"), ["subject"] = "Quote", ["body"] = "Hi" }, As(Author), default))
            .Should().StartWith("Send 'Quote' from writer@example.com to dana@example.com");
        mail.Sent.Should().BeEmpty("a preview sends nothing");

        await Tool(tools, "list_mail").Invoking(t => t.RunAsync(new JsonObject(), As(new SentinelAgentUser("nobody", [AppRoles.Author])), default))
            .Should().ThrowAsync<SentinelAgentToolException>().WithMessage("*no mailbox connected*");
    }

    [Fact]
    public void EachNewArea_ShouldReachTheSameRolesAsItsPage()
    {
        var comments = new CommentAgentTools(new FakeComments());
        var mail = new MailAgentTools(new FakeMail());
        var insights = new InsightsAgentTools(null!, null!, TimeProvider.System);

        comments.Tools.Should().OnlyContain(tool => Author.MayUse(tool), "Comments is ContentAccess");
        mail.Tools.Should().OnlyContain(tool => Author.MayUse(tool), "Mail is every role's, one mailbox each");
        insights.Tools.Should().OnlyContain(tool => !Author.MayUse(tool) && Admin.MayUse(tool), "Growth and Docker are AdminOnly");
        insights.Tools.Should().OnlyContain(tool => tool.Risk == SentinelAgentRisk.Read);
    }

    private sealed class FakeComments : ICommentService
    {
        private readonly Dictionary<Guid, string> _status = [];
        private readonly Dictionary<Guid, string> _author = [];

        public Guid Add(string author, string body, string status)
        {
            var id = Guid.NewGuid();
            (_status[id], _author[id]) = (status, author);
            return id;
        }

        public string StatusOf(Guid id) => _status[id];

        public Task<IReadOnlyList<CommentView>> ListForModerationAsync(string? statusFilter, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CommentView>>(_status.Where(pair => statusFilter is null || pair.Value == statusFilter)
                .Select(pair => new CommentView { Id = pair.Key, AuthorName = _author[pair.Key], ArticleTitle = "The post", Body = "Lovely post", Status = pair.Value }).ToList());

        public Task ApproveAsync(Guid commentId, CancellationToken cancellationToken = default) => Set(commentId, CommentStatuses.Approved);
        public Task MarkSpamAsync(Guid commentId, CancellationToken cancellationToken = default) => Set(commentId, CommentStatuses.Spam);
        public Task MarkPendingAsync(Guid commentId, CancellationToken cancellationToken = default) => Set(commentId, CommentStatuses.Pending);
        public Task DeleteAsync(Guid commentId, CancellationToken cancellationToken = default) => Task.FromResult(_status.Remove(commentId));
        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult(_status.Count(pair => pair.Value == CommentStatuses.Pending));
        public Task<CommentView> SubmitAsync(Guid articleId, string authorName, string authorEmail, string body, Guid? parentCommentId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CommentView>> ListApprovedForArticleAsync(Guid articleId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Set(Guid id, string status)
        {
            _status[id] = status;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMail : IMailService
    {
        private static readonly Guid Personal = Guid.NewGuid();
        private static readonly Guid Work = Guid.NewGuid();
        public List<string> Usernames { get; } = [];
        public List<MailDraft> Drafts { get; } = [];
        public List<MailDraft> Sent { get; } = [];
        public List<string> Trashed { get; } = [];
        public string? LastFolder { get; private set; }

        public Task<IReadOnlyList<MailAccountView>> ListAccountsAsync(string username, CancellationToken cancellationToken = default)
        {
            Usernames.Add(username);
            return Task.FromResult<IReadOnlyList<MailAccountView>>(username == "writer"
                ? [new(Personal, "imap", "writer@example.com", "Writer", null, null), new(Work, "imap", "work@example.com", "Work", null, null)]
                : []);
        }

        public Task<MailPage> ListAsync(string username, Guid accountId, string folder, string? search, string? pageToken, CancellationToken cancellationToken = default)
        {
            Usernames.Add(username);
            LastFolder = folder;
            return Task.FromResult(new MailPage([new("m1", null, new MailAddress("Dana", "dana@example.com"), [], "Quote?", "Can you send a quote", DateTimeOffset.UtcNow, true, false, false)], null));
        }

        public Task<string> SaveDraftAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default)
        {
            Usernames.Add(username);
            Drafts.Add(draft);
            return Task.FromResult($"draft-{Drafts.Count}");
        }

        public Task SendAsync(string username, Guid accountId, MailDraft draft, CancellationToken cancellationToken = default)
        {
            Usernames.Add(username);
            Sent.Add(draft);
            return Task.CompletedTask;
        }

        public Task TrashAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default)
        {
            Usernames.Add(username);
            Trashed.Add(messageId);
            return Task.CompletedTask;
        }

        public Task<MailActionResult> ApplyAsync(string username, Guid accountId, IReadOnlyList<string> messageIds, string action,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UndoAsync(string username, Guid accountId, MailUndo undo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, int>> GetUnreadByAccountAsync(string username, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MailAccountView> SaveImapAccountAsync(string username, Guid? accountId, MailImapAccountInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAccountView> SaveGoogleAccountAsync(string username, string tokenJson, string address, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAccountAsync(string username, Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailFolderView>> ListFoldersAsync(string username, Guid accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailMessageDetail> GetAsync(string username, Guid accountId, string messageId, bool showRemoteImages, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailAttachmentContent> GetAttachmentAsync(string username, Guid accountId, string messageId, int index, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetReadAsync(string username, Guid accountId, string messageId, bool read, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetStarredAsync(string username, Guid accountId, string messageId, bool starred, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ArchiveAsync(string username, Guid accountId, string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int?> GetUnreadCountAsync(string username, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
