using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace GwsBusinessSuite.Tests;

// Mail (31-10-26): your own mailbox in the app. The service picks the provider, builds what's
// sent (threading for replies, attachments for forwards) and keeps the last problem on the account.
public sealed class MailServiceTests
{
    [Fact]
    public async Task ConnectingAnImapMailbox_ShouldSignInFirst_AndKeepThePasswordProtected()
    {
        var f = await CreateAsync();

        var view = await f.Service.SaveImapAccountAsync("grant", new MailImapAccountInput(
            "grant@icloud.com", "Grant Watson", "imap.mail.me.com", 993, "smtp.mail.me.com", 587, MailServerSecurity.StartTls, "", "abcd-efgh"));

        view.Should().BeEquivalentTo(new { Provider = MailProviders.Imap, Address = "grant@icloud.com", DisplayName = "Grant Watson" });
        f.Imap.Verified.Should().Equal(("grant@icloud.com", "abcd-efgh"));
        var row = await f.Db.MailAccounts.SingleAsync();
        row.PasswordProtected.Should().NotContain("abcd");
        row.LoginUsername.Should().Be("grant@icloud.com", "the address is the sign-in unless another is given");

        f.Imap.FailVerify = "Authentication failed.";
        await f.Service.Invoking(s => s.SaveImapAccountAsync("sam", new MailImapAccountInput(
            "sam@icloud.com", "", "imap.mail.me.com", 993, "smtp.mail.me.com", 587, MailServerSecurity.StartTls, "", "wrong")))
            .Should().ThrowAsync<MailException>().WithMessage("Authentication failed.");
        (await f.Db.MailAccounts.CountAsync()).Should().Be(1, "a mailbox that can't sign in isn't saved");
    }

    [Fact]
    public async Task AReply_ShouldThreadOnTheOriginal_AndAForward_ShouldCarryItsAttachments()
    {
        var f = await CreateAsync();
        await f.UseImapAsync();
        var original = new MimeMessage { MessageId = "orig@example.com", Subject = "Plans" };
        original.References.Add("first@example.com");
        original.From.Add(MailboxAddress.Parse("sam@example.com"));
        var builder = new BodyBuilder { TextBody = "Here" };
        builder.Attachments.Add("plan.pdf", new byte[] { 1, 2, 3 });
        original.Body = builder.ToMessageBody();
        f.Imap.Messages["m1"] = original;

        await f.Service.SendAsync("grant", new MailDraft(["sam@example.com"], [], [], "Re: Plans", "Sounds good", "m1"));
        var reply = f.Imap.Sent.Single();
        reply.From.Mailboxes.Single().Should().BeEquivalentTo(new { Name = "Grant Watson", Address = "grant@icloud.com" });
        reply.InReplyTo.Should().Be("orig@example.com");
        reply.References.Should().Equal("first@example.com", "orig@example.com");
        reply.Attachments.Should().BeEmpty();

        await f.Service.SendAsync("grant", new MailDraft(["kim@example.com; lee@example.com"], [], [], "Fwd: Plans", "FYI", "m1", Forward: true));
        var forward = f.Imap.Sent.Last();
        forward.To.Mailboxes.Select(m => m.Address).Should().Equal("kim@example.com", "lee@example.com");
        forward.InReplyTo.Should().BeNull();
        forward.Attachments.OfType<MimePart>().Single().FileName.Should().Be("plan.pdf");

        await f.Service.Invoking(s => s.SendAsync("grant", new MailDraft(["not an address"], [], [], "x", "y")))
            .Should().ThrowAsync<MailException>().WithMessage("*isn't an email address*");
        await f.Service.Invoking(s => s.SendAsync("grant", new MailDraft([], [], [], "x", "y")))
            .Should().ThrowAsync<MailException>().WithMessage("Add who it's to.");
    }

    [Fact]
    public async Task OpeningAMessage_ShouldMarkItRead_AndAProblem_ShouldBeKeptUntilTheNextSuccess()
    {
        var f = await CreateAsync();
        await f.UseImapAsync();
        var message = new MimeMessage { Subject = "Hi" };
        message.From.Add(MailboxAddress.Parse("sam@example.com"));
        message.Body = new TextPart("plain") { Text = "Hello" };
        f.Imap.Messages["m1"] = message;

        (await f.Service.GetAsync("grant", "m1", showRemoteImages: false)).Subject.Should().Be("Hi");
        f.Imap.MarkedRead.Should().Equal("m1");

        f.Imap.FailList = "The server is busy.";
        await f.Service.Invoking(s => s.ListAsync("grant", MailFolders.Inbox, null, null)).Should().ThrowAsync<MailException>();
        (await f.Service.GetAccountAsync("grant"))!.LastError.Should().Be("The server is busy.");

        f.Imap.FailList = null;
        await f.Service.ListAsync("grant", MailFolders.Inbox, null, null);
        (await f.Service.GetAccountAsync("grant"))!.LastError.Should().BeNull();

        await f.Service.Invoking(s => s.ListAsync("grant", "secret-folder", null, null)).Should().ThrowAsync<MailException>();
        await f.Service.Invoking(s => s.ListAsync("sam", MailFolders.Inbox, null, null)).Should().ThrowAsync<MailException>().WithMessage("Connect a mailbox first.");
    }

    [Fact]
    public async Task DisconnectingGmail_ShouldDeleteItsTokens()
    {
        var f = await CreateAsync();
        var credential = new AutomationCredential { Name = "Mail - grant", TypeKey = "oauth2", ProtectedData = "x" };
        f.Db.AutomationCredentials.Add(credential);
        await f.Db.SaveChangesAsync();
        await f.Service.SaveGoogleAccountAsync("grant", credential.Id, "grant@gwsapp.net");

        await f.Service.RemoveAccountAsync("grant");

        (await f.Db.MailAccounts.CountAsync()).Should().Be(0);
        (await f.Db.AutomationCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Gmail_ShouldListWithLabels_RefreshAnExpiredToken_AndSendRawInTheThread()
    {
        var credentials = new FakeCredentials();
        var requests = new List<(string Method, string Url, string Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            var token = request.Headers.Authorization?.Parameter;
            if (token == "old") return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"expired"}}""");
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/messages") && request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, """{"messages":[{"id":"a1"}],"nextPageToken":"p2"}""");
            if (path.EndsWith("/messages/a1"))
                return Json(HttpStatusCode.OK, """
                    {"id":"a1","threadId":"t1","labelIds":["INBOX","UNREAD","STARRED"],"snippet":"It&#39;s ready","internalDate":"1793437500000",
                     "payload":{"headers":[{"name":"From","value":"Sam Sender <sam@example.com>"},{"name":"Subject","value":"Report"},
                                           {"name":"Content-Type","value":"multipart/mixed; boundary=x"}]}}
                    """);
            return Json(HttpStatusCode.OK, """{"id":"sent1"}""");
        });
        var gmail = new GmailMailboxBackend(credentials, new Factory(handler));
        var account = new MailAccount { Username = "grant", Provider = MailProviders.Google, CredentialId = credentials.Id, Address = "grant@gwsapp.net" };

        var page = await gmail.ListAsync(account, MailFolders.Inbox, "from:sam", null, CancellationToken.None);

        credentials.Refreshed.Should().BeTrue("the first call got a 401");
        requests.First(r => r.Url.StartsWith("/gmail/v1/users/me/messages?") && r.Url.Contains("labelIds=INBOX")).Url.Should().Contain("q=from%3Asam");
        page.NextPageToken.Should().Be("p2");
        page.Messages.Single().Should().BeEquivalentTo(new
        {
            Id = "a1", ThreadId = "t1", Subject = "Report", Snippet = "It's ready", Unread = true, Starred = true, HasAttachments = true,
            From = new { Name = "Sam Sender", Address = "sam@example.com" }
        });

        var message = new MimeMessage { Subject = "Re: Report" };
        message.From.Add(MailboxAddress.Parse("grant@gwsapp.net"));
        message.To.Add(MailboxAddress.Parse("sam@example.com"));
        message.Body = new TextPart("plain") { Text = "Thanks" };
        await gmail.SendAsync(account, message, "t1", CancellationToken.None);

        var sent = JsonNode.Parse(requests.Last().Body)!;
        sent["threadId"]!.GetValue<string>().Should().Be("t1");
        var raw = MimeMessage.Load(new MemoryStream(GmailMailboxBackend.FromBase64Url(sent["raw"]!.GetValue<string>())));
        raw.Subject.Should().Be("Re: Report");
    }

    [Fact]
    public async Task WhatsSent_ShouldBeTextAndFormattedHtml_WithTheFilesAttached()
    {
        var f = await CreateAsync();
        await f.UseImapAsync();
        var files = new[] { new MailAttachmentUpload("../../quote.pdf", "application/pdf", new byte[] { 1, 2, 3 }) };

        await f.Service.SendAsync("grant", new MailDraft(["sam@example.com"], [], [], "Quote", "Here's **the quote**.\n\n> your question", Attachments: files));

        var sent = f.Imap.Sent.Single();
        sent.TextBody.Should().Be("Here's **the quote**.\n\n> your question");
        sent.HtmlBody.Should().Contain("<strong>the quote</strong>").And.Contain("<blockquote style=");
        sent.Attachments.OfType<MimePart>().Single().Should().BeEquivalentTo(new { FileName = "quote.pdf" }, "a path in the name is dropped");
        (await f.Service.GetUnreadCountAsync("grant")).Should().Be(7);
        (await f.Service.GetUnreadCountAsync("sam")).Should().BeNull("no mailbox");

        var tooBig = new MailAttachmentUpload("big.bin", "application/octet-stream", new byte[MailLimits.MaxAttachmentBytes + 1]);
        await f.Service.Invoking(s => s.SendAsync("grant", new MailDraft(["sam@example.com"], [], [], "x", "y", Attachments: [tooBig])))
            .Should().ThrowAsync<MailException>().WithMessage("*25 MB*");
    }

    [Fact]
    public void Formatting_ShouldShowTypedHtmlAsText()
    {
        var html = MailFormatting.ToHtml("Hi <script>alert(1)</script> _there_\nnext line");

        html.Should().NotContain("<script>").And.Contain("&lt;script&gt;").And.Contain("<em>there</em>").And.Contain("<br");
    }

    [Fact]
    public async Task TheMenusUnreadCount_ShouldNeverWait_AndRefreshInTheBackgroundWhenStale()
    {
        var clock = new ManualClock();
        var asked = 0;
        var answer = new TaskCompletionSource<int?>();
        var counter = MailUnreadCounter.Create((_, _) => { asked++; return answer.Task; }, clock);

        counter.Peek("grant").Should().BeNull("nothing known yet - and it doesn't wait for the mailbox");
        counter.Peek("grant").Should().BeNull();
        asked.Should().Be(1, "one check at a time");
        answer.SetResult(4);
        await Task.Yield();
        counter.Peek("grant").Should().Be(4);

        answer = new TaskCompletionSource<int?>();
        answer.SetResult(2);
        counter.Peek("grant");
        asked.Should().Be(1, "fresh for two minutes");
        counter.Invalidate("grant");
        counter.Peek("grant").Should().Be(4, "the last count shows while the new one comes in");
        await Task.Yield();
        counter.Peek("grant").Should().Be(2);
        asked.Should().Be(2);

        clock.Now += MailUnreadCounter.FreshFor;
        answer = new TaskCompletionSource<int?>();
        answer.SetException(new MailException("The server is busy."));
        counter.Peek("grant");
        await Task.Yield();
        counter.Peek("grant").Should().Be(2, "a failed check keeps the last count");
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ImapIds_ShouldRoundTrip_AndRejectGarbage()
    {
        var id = ImapMessageId.Encode("INBOX/Clients: 2026", 77, 4012);
        ImapMessageId.Decode(id).Should().Be(("INBOX/Clients: 2026", 77u, 4012u));
        FluentActions.Invoking(() => ImapMessageId.Decode("not-an-id")).Should().Throw<MailException>();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Fixture(ApplicationDbContext Db, MailService Service, FakeImap Imap)
    {
        public Task UseImapAsync() => Service.SaveImapAccountAsync("grant", new MailImapAccountInput(
            "grant@icloud.com", "Grant Watson", "imap.mail.me.com", 993, "smtp.mail.me.com", 587, MailServerSecurity.StartTls, "", "pw"));
    }

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var imap = new FakeImap();
        var service = new MailService(db, new DataProtectionSecretProtector(new EphemeralDataProtectionProvider()), [imap], TimeProvider.System);
        return new Fixture(db, service, imap);
    }

    private sealed class FakeImap : IMailboxBackend
    {
        public string Provider => MailProviders.Imap;
        public List<(string Login, string Password)> Verified { get; } = [];
        public Dictionary<string, MimeMessage> Messages { get; } = [];
        public List<MimeMessage> Sent { get; } = [];
        public List<string> MarkedRead { get; } = [];
        public string? FailVerify { get; set; }
        public string? FailList { get; set; }

        public Task VerifyAsync(MailAccount account, string password, CancellationToken cancellationToken)
        {
            if (FailVerify is { } failure) throw new MailException(failure);
            Verified.Add((account.LoginUsername, password));
            return Task.CompletedTask;
        }

        public Task<MailPage> ListAsync(MailAccount account, string folder, string? search, string? pageToken, CancellationToken cancellationToken) =>
            FailList is { } failure ? throw new MailException(failure) : Task.FromResult(new MailPage([], null));

        public Task<MailFetched> GetAsync(MailAccount account, string id, bool markRead, CancellationToken cancellationToken)
        {
            if (markRead) MarkedRead.Add(id);
            return Task.FromResult(new MailFetched(Messages[id], "thread-" + id, !markRead, false));
        }

        public Task SendAsync(MailAccount account, MimeMessage message, string? threadId, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task SetReadAsync(MailAccount account, string id, bool read, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetStarredAsync(MailAccount account, string id, bool starred, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ArchiveAsync(MailAccount account, string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task TrashAsync(MailAccount account, string id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> UnreadCountAsync(MailAccount account, CancellationToken cancellationToken) => Task.FromResult(7);
    }

    private sealed class FakeCredentials : IAutomationCredentialService
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool Refreshed { get; private set; }
        private string _token = "old";

        public Task<string?> GetDecryptedDataAsync(Guid credentialId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(credentialId == Id ? $$"""{"accessToken":"{{_token}}"}""" : null);

        public Task<bool> RefreshOAuthCredentialAsync(Guid credentialId, CancellationToken cancellationToken = default)
        {
            _token = "fresh";
            Refreshed = true;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<AutomationCredentialSummary>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> SaveAsync(Guid? id, string name, string typeKey, string credentialJson, string description = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RefreshExpiringOAuthCredentialsAsync(TimeSpan within, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => responder(request);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
