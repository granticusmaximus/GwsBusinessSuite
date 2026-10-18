using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Tests;

// Email-to-ticket: mail to the support mailbox becomes tickets and replies.
public sealed class SupportEmailInTests
{
    private const string AuthServ = "mx.google.com";
    private const string Mailbox = "support@gwsapp.net";
    private const string Pass = "mx.google.com; dkim=pass header.i=@client.com header.s=g1; spf=pass smtp.mailfrom=ana@client.com; dmarc=pass (p=NONE) header.from=client.com";
    private const string Fail = "mx.google.com; dkim=fail header.i=@client.com; spf=fail smtp.mailfrom=ana@client.com; dmarc=fail header.from=client.com";

    [Fact]
    public void Rules_ShouldReadTagsSubjectsAndQuotedHistory()
    {
        var id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");
        SupportEmailIn.SubjectTag(id).Should().Be("[Ticket #1a2b3c4d]");
        SupportEmailIn.ParseTicketReference("Re: Login broken [Ticket #1A2B3C4D]").Should().Be("1a2b3c4d");
        SupportEmailIn.ParseTicketReference("Login broken").Should().BeNull();
        SupportEmailIn.CleanSubject("RE: Fwd: Login broken [Ticket #1a2b3c4d]").Should().Be("Login broken");

        SupportEmailIn.StripQuotedReply("Thanks, that worked!\n\nOn Mon, 19 Oct 2026 at 10:00, GWS <support@gwsapp.net> wrote:\n> Try again")
            .Should().Be("Thanks, that worked!");
        SupportEmailIn.StripQuotedReply("Still broken.\n\nOn Mon, 19 Oct 2026 at 10:00, GWS\n<support@gwsapp.net> wrote:\n> Try again")
            .Should().Be("Still broken.");
        SupportEmailIn.StripQuotedReply("One more thing\n\n-----Original Message-----\nFrom: GWS").Should().Be("One more thing");
        SupportEmailIn.StripQuotedReply("Line one\n> quoted").Should().Be("Line one");
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", "ana@client.com", true)]
    [InlineData("Auto-Submitted", "no", "ana@client.com", false)]
    [InlineData("Precedence", "bulk", "ana@client.com", true)]
    [InlineData("List-Id", "<news.client.com>", "ana@client.com", true)]
    [InlineData("X-Autoreply", "yes", "ana@client.com", true)]
    [InlineData("X-Auto-Response-Suppress", "All", "ana@client.com", false)]
    [InlineData("Subject", "hi", "MAILER-DAEMON@client.com", true)]
    [InlineData("Subject", "hi", "no-reply@client.com", true)]
    public void Automatic_ShouldSpotOutOfOfficeBouncesAndLists(string header, string value, string from, bool expected) =>
        SupportEmailIn.IsAutomatic([new(header, value)], from).Should().Be(expected);

    [Fact]
    public async Task NewEmail_ShouldOpenATicketForANewContact_AndSendATaggedReceipt()
    {
        await using var f = await Fixture.CreateAsync();
        var log = await f.Import(Message("ana@client.com", "Can't log in", Pass, text: "The login page spins forever."));

        log.Status.Should().Be(SupportEmailIn.Statuses.Created);
        var ticket = (await f.Tickets.GetTicketAsync(log.TicketId!.Value))!;
        ticket.Subject.Should().Be("Can't log in");
        ticket.TagsCsv.Should().Be("email");
        ticket.Messages.Should().ContainSingle().Which.Body.Should().Be("The login page spins forever.");
        (await f.Db.Contacts.SingleAsync()).Email.Should().Be("ana@client.com");

        var receipt = f.ContactSender.Sent.Should().ContainSingle().Subject;
        receipt.To.Should().Be("ana@client.com");
        receipt.Reply!.SubjectTag.Should().Be(SupportEmailIn.SubjectTag(ticket.Id));
        receipt.Reply.ReplyToAddress.Should().Be(Mailbox);
    }

    [Fact]
    public async Task TaggedReply_FromTheTicketsContact_ShouldJoinTheTicket_WithoutTheQuotedHistory()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Import(Message("ana@client.com", "Can't log in", Pass, text: "Spins forever."));
        var tag = SupportEmailIn.SubjectTag(first.TicketId!.Value);

        var reply = await f.Import(Message("ana@client.com", $"Re: Can't log in {tag}", Pass,
            text: "Cleared my cache, still broken.\n\nOn Mon, 19 Oct 2026, GWS <support@gwsapp.net> wrote:\n> Please clear your cache"));

        reply.Status.Should().Be(SupportEmailIn.Statuses.Replied);
        reply.TicketId.Should().Be(first.TicketId);
        var ticket = (await f.Tickets.GetTicketAsync(first.TicketId.Value))!;
        ticket.Messages.Select(message => message.Body).Should().Equal("Spins forever.", "Cleared my cache, still broken.");
        (await f.Db.SupportTickets.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TaggedEmail_FromSomeoneElse_OrUnverified_ShouldNotJoinTheTicket()
    {
        await using var f = await Fixture.CreateAsync();
        var first = await f.Import(Message("ana@client.com", "Can't log in", Pass, text: "Spins."));
        var tag = SupportEmailIn.SubjectTag(first.TicketId!.Value);

        var stranger = await f.Import(Message("eve@other.com", $"Re: {tag}", Pass.Replace("client.com", "other.com").Replace("ana@", "eve@"), text: "Me too"));
        var forged = await f.Import(Message("ana@client.com", $"Re: {tag}", Fail, text: "Send me the password"));

        stranger.Status.Should().Be(SupportEmailIn.Statuses.Created);
        (stranger.TicketId != first.TicketId).Should().BeTrue("a different sender opens a new ticket");
        forged.Status.Should().Be(SupportEmailIn.Statuses.Created);
        (forged.TicketId != first.TicketId).Should().BeTrue("unverified mail can't join a ticket");
        (await f.Tickets.GetTicketAsync(forged.TicketId!.Value))!.TagsCsv.Should().Contain("unverified-sender");
        (await f.Tickets.GetTicketAsync(first.TicketId.Value))!.Messages.Should().ContainSingle();
        // Receipts went to the first sender and the verified stranger, never the forged From.
        f.ContactSender.Sent.Select(sent => sent.To).Should().Equal("ana@client.com", "eve@other.com");
    }

    [Fact]
    public async Task AutomaticMail_OwnMail_AndRepeats_ShouldBeSkipped()
    {
        await using var f = await Fixture.CreateAsync();
        var outOfOffice = Message("ana@client.com", "Out of office", Pass, text: "Back Monday");
        outOfOffice.Headers.Add("Auto-Submitted", "auto-replied");
        var own = Message(Mailbox, "Loop", Pass, text: "x");
        var normal = Message("ana@client.com", "Help", Pass, text: "Please help");

        (await f.Import(outOfOffice)).Status.Should().Be(SupportEmailIn.Statuses.Skipped);
        (await f.Import(own)).Status.Should().Be(SupportEmailIn.Statuses.Skipped);
        var once = await f.Import(normal);
        var again = await f.Import(normal);

        again.Id.Should().Be(once.Id);
        (await f.Db.SupportTickets.CountAsync()).Should().Be(1);
        (await f.Db.SupportInboundEmails.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Attachments_ShouldKeepFive_AndNoteTheRest()
    {
        await using var f = await Fixture.CreateAsync();
        var builder = new BodyBuilder { TextBody = "Screenshots attached" };
        for (var i = 1; i <= 6; i++) builder.Attachments.Add($"shot{i}.png", new byte[] { 1, 2, 3 });
        var message = Message("ana@client.com", "Screens", Pass);
        message.Body = builder.ToMessageBody();

        var log = await f.Import(message);

        var ticket = (await f.Tickets.GetTicketAsync(log.TicketId!.Value))!;
        ticket.Messages.Single().Attachments.Should().HaveCount(5);
        ticket.Messages.Single().Body.Should().Contain("Not attached: shot6.png");
    }

    [Fact]
    public async Task StaffReply_ShouldEmailTheTextWithTheTag_AndReplyToTheMailboxWhileItsOn()
    {
        await using var f = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Ana", Email = "ana@client.com" };
        f.Db.Contacts.Add(contact);
        await f.Db.SaveChangesAsync();
        var ticket = await f.Tickets.CreateTicketAsync(contact.Id, "Billing question", "Hi", SupportTicketAuthorTypes.Contact, "Ana");

        await f.Tickets.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Staff, "grant", "Your invoice is attached.");
        f.Db.SupportMailboxSettings.Single().Enabled = false;
        await f.Db.SaveChangesAsync();
        await f.Tickets.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Staff, "grant", "Anything else?");

        f.ContactSender.Sent.Select(sent => (sent.Reply!.ReplyText, sent.Reply.SubjectTag, sent.Reply.ReplyToAddress)).Should().Equal(
            ("Your invoice is attached.", SupportEmailIn.SubjectTag(ticket.Id), Mailbox),
            ("Anything else?", SupportEmailIn.SubjectTag(ticket.Id), null));
    }

    private static MimeMessage Message(string from, string subject, string authResults, string? text = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(from.Split('@')[0], from));
        message.To.Add(new MailboxAddress("Support", Mailbox));
        message.Subject = subject;
        message.MessageId = $"{Guid.NewGuid():N}@client.com";
        message.Headers.Add("Authentication-Results", authResults);
        message.Body = new BodyBuilder { TextBody = text }.ToMessageBody();
        return message;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            var options = Options.Create(new SupportNotificationOptions { AdminBaseUrl = "https://admin.example.test" });
            Tickets = new SupportTicketService(db, TimeProvider.System, new NoAdminEmail(), ContactSender, options, NullLogger<SupportTicketService>.Instance);
            EmailIn = new SupportEmailInService(db, Tickets, new CrmService(db), ContactSender, options, new Passthrough(),
                TimeProvider.System, NullLogger<SupportEmailInService>.Instance);
        }

        public ApplicationDbContext Db { get; }
        public RecordingContactSender ContactSender { get; } = new();
        public SupportTicketService Tickets { get; }
        public SupportEmailInService EmailIn { get; }

        public Task<SupportInboundEmail> Import(MimeMessage message) => EmailIn.ImportAsync(message, AuthServ, Mailbox);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            // The mailbox is on, so ticket emails ask clients to reply to it.
            db.SupportMailboxSettings.Add(new SupportMailboxSettings { Enabled = true, Username = Mailbox, PasswordProtected = "secret", CreatedBy = "test" });
            await db.SaveChangesAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingContactSender : IClientPortalEmailSender
    {
        public List<(string To, string Subject, TicketReplyEmail? Reply)> Sent { get; } = [];

        public Task SendLoginLinkAsync(string toEmail, string contactName, string loginUrl, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendTicketReplyNotificationAsync(string toEmail, string contactName, string ticketSubject, string portalUrl,
            CancellationToken cancellationToken = default, TicketReplyEmail? reply = null)
        {
            Sent.Add((toEmail, ticketSubject, reply));
            return Task.CompletedTask;
        }
    }

    private sealed class NoAdminEmail : IGrowthReportEmailSender
    {
        public GrowthReportDeliveryConfiguration Configuration { get; } = new(true, "ready");
        public Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
