using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.ClientPortal;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class SupportTicketServiceTests
{
    [Fact]
    public async Task CreateTicketAsync_ShouldCreateTheTicketWithItsFirstMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");

        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Can't log in", "I keep getting an error", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        ticket.Status.Should().Be(SupportTicketStatuses.Open);
        ticket.Messages.Should().ContainSingle();
        ticket.Messages[0].Body.Should().Be("I keep getting an error");
        ticket.ContactName.Should().Be("Jamie Rivera");
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldReject_WhenTheContactDoesNotExist()
    {
        await using var fixture = await Fixture.CreateAsync();

        var act = () => fixture.Service.CreateTicketAsync(
            Guid.NewGuid(), "Subject", "Body", SupportTicketAuthorTypes.Contact, "Nobody");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddReplyAsync_ShouldReopenATerminalTicket_WhenTheContactReplies_ButNotWhenStaffReplies()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        await fixture.Service.SetStatusAsync(ticket.Id, SupportTicketStatuses.Resolved, "staff");

        var afterStaffReply = await fixture.Service.AddReplyAsync(
            ticket.Id, SupportTicketAuthorTypes.Staff, "staff", "Following up");
        afterStaffReply.Status.Should().Be(SupportTicketStatuses.Resolved, "a staff reply alone shouldn't reopen a resolved ticket");

        var afterContactReply = await fixture.Service.AddReplyAsync(
            ticket.Id, SupportTicketAuthorTypes.Contact, "Jamie Rivera", "Actually, still broken");
        afterContactReply.Status.Should().Be(SupportTicketStatuses.Open);
        afterContactReply.ResolvedAt.Should().BeNull();
        afterContactReply.Messages.Should().HaveCount(3);
    }

    [Fact]
    public async Task SetStatusAsync_ShouldStampResolvedAt_OnlyWhileResolved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var resolved = await fixture.Service.SetStatusAsync(ticket.Id, SupportTicketStatuses.Resolved, "staff");
        resolved.ResolvedAt.Should().NotBeNull();

        var reopened = await fixture.Service.SetStatusAsync(ticket.Id, SupportTicketStatuses.Open, "staff");
        reopened.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task SetStatusAsync_ShouldReject_AnInvalidStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var act = () => fixture.Service.SetStatusAsync(ticket.Id, "NotARealStatus", "staff");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListTicketsForContactAsync_ShouldOnlyReturnThatContactsTickets()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contactA = await fixture.AddContactAsync("Jamie Rivera");
        var contactB = await fixture.AddContactAsync("Alex Chen");
        await fixture.Service.CreateTicketAsync(contactA.Id, "A1", "Body", SupportTicketAuthorTypes.Contact, "Jamie");
        await fixture.Service.CreateTicketAsync(contactB.Id, "B1", "Body", SupportTicketAuthorTypes.Contact, "Alex");

        var ticketsForA = await fixture.Service.ListTicketsForContactAsync(contactA.Id);

        ticketsForA.Should().ContainSingle(ticket => ticket.Subject == "A1");
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldEmailTheAdmin_WhenTheContactRaisesItThemselves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");

        await fixture.Service.CreateTicketAsync(
            contact.Id, "Can't log in", "I keep getting an error", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        fixture.AdminSender.Messages.Should().ContainSingle();
        var email = fixture.AdminSender.Messages.Single();
        email.RecipientEmail.Should().Be("grant@gwsapp.net");
        email.Subject.Should().Contain("Can't log in");
        email.PlainTextBody.Should().Contain("Jamie Rivera");
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldNotEmailTheAdmin_WhenStaffOpenItOnTheContactsBehalf()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");

        await fixture.Service.CreateTicketAsync(
            contact.Id, "Logged for them", "Called in about billing", SupportTicketAuthorTypes.Staff, "staff");

        fixture.AdminSender.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task AddReplyAsync_ShouldEmailTheContact_WhenStaffReplies_AndTheContactHasAnEmail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera", email: "jamie@example.com");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        fixture.AdminSender.Messages.Clear();

        await fixture.Service.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Staff, "staff", "Here's the fix");

        fixture.ContactSender.Messages.Should().ContainSingle();
        var email = fixture.ContactSender.Messages.Single();
        email.ToEmail.Should().Be("jamie@example.com");
        email.TicketSubject.Should().Be("Question");
        fixture.AdminSender.Messages.Should().BeEmpty("staff replying shouldn't notify the admin about their own reply");
    }

    [Fact]
    public async Task AddReplyAsync_ShouldSkipTheContactEmail_WhenTheContactHasNoEmailOnFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var afterReply = await fixture.Service.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Staff, "staff", "Here's the fix");

        fixture.ContactSender.Messages.Should().BeEmpty();
        afterReply.Messages.Should().HaveCount(2, "the reply itself must still be saved even though no email could be sent");
    }

    [Fact]
    public async Task AddReplyAsync_ShouldEmailTheAdmin_WhenTheContactReplies()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        fixture.AdminSender.Messages.Clear();

        await fixture.Service.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Contact, "Jamie Rivera", "Still broken");

        fixture.AdminSender.Messages.Should().ContainSingle();
        fixture.AdminSender.Messages.Single().PlainTextBody.Should().Contain("Still broken");
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldPersistAttachments_AndServeThemBackByContentAndOwner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var upload = new SupportTicketAttachmentUpload("screenshot.png", [0x89, 0x50, 0x4E, 0x47]);

        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Broken layout", "See attached", SupportTicketAuthorTypes.Contact, "Jamie Rivera",
            [upload]);

        var attachment = ticket.Messages.Single().Attachments.Should().ContainSingle().Subject;
        attachment.FileName.Should().Be("screenshot.png");
        attachment.ContentType.Should().Be("image/png");
        attachment.SizeBytes.Should().Be(4);

        var content = await fixture.Service.GetAttachmentContentAsync(attachment.Id);
        content.Should().NotBeNull();
        content!.Value.FileName.Should().Be("screenshot.png");
        content.Value.Content.Should().Equal(upload.Content);

        var ownerContactId = await fixture.Service.GetAttachmentOwnerContactIdAsync(attachment.Id);
        ownerContactId.Should().Be(contact.Id);
    }

    [Fact]
    public async Task AddReplyAsync_ShouldPersistAttachmentsOnTheNewMessageOnly()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var replied = await fixture.Service.AddReplyAsync(
            ticket.Id, SupportTicketAuthorTypes.Staff, "staff", "Here's a log file",
            [new SupportTicketAttachmentUpload("trace.log", "boom"u8.ToArray())]);

        replied.Messages.Single(m => m.Body == "Body").Attachments.Should().BeEmpty("the original message had no attachments");
        replied.Messages.Single(m => m.Body == "Here's a log file").Attachments
            .Should().ContainSingle(a => a.FileName == "trace.log" && a.ContentType == "text/plain");
    }

    [Fact]
    public async Task AddReplyAsync_ShouldRejectAnOversizedAttachment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        var tooBig = new SupportTicketAttachmentUpload("huge.bin", new byte[11 * 1024 * 1024]);

        var act = () => fixture.Service.AddReplyAsync(
            ticket.Id, SupportTicketAuthorTypes.Staff, "staff", "Body", [tooBig]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetAttachmentOwnerContactIdAsync_ShouldReturnNull_ForAnUnknownAttachment()
    {
        await using var fixture = await Fixture.CreateAsync();

        var ownerContactId = await fixture.Service.GetAttachmentOwnerContactIdAsync(Guid.NewGuid());

        ownerContactId.Should().BeNull();
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldComputeSlaTargetsFromDefaultPriority()
    {
        var now = DateTimeOffset.Parse("2026-08-21T12:00:00Z");
        await using var fixture = await Fixture.CreateAsync(now);
        var contact = await fixture.AddContactAsync("Jamie Rivera");

        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        ticket.Priority.Should().Be(SupportTicketPriorities.Normal);
        ticket.FirstResponseDueAt.Should().Be(now.AddHours(8));
        ticket.ResolutionDueAt.Should().Be(now.AddHours(72));
    }

    [Fact]
    public async Task CreateTicketAsync_ShouldFireTheAutomationTrigger()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");

        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Can't log in", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        fixture.AutomationTriggers.CreatedCalls.Should().ContainSingle(call => call.TicketId == ticket.Id && call.Subject == "Can't log in");
    }

    [Fact]
    public async Task AddReplyAsync_ShouldFireTheAutomationTrigger()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        fixture.AutomationTriggers.CreatedCalls.Clear();

        await fixture.Service.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Staff, "staff", "Here's the fix");

        fixture.AutomationTriggers.RepliedCalls.Should().ContainSingle(call => call.TicketId == ticket.Id && call.Body == "Here's the fix");
    }

    [Fact]
    public async Task ProcessSlaBreachesAsync_ShouldFireEachBreachOnlyOnce()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var created = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        var ticket = await fixture.Db.SupportTickets.SingleAsync(item => item.Id == created.Id);
        ticket.FirstResponseDueAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        ticket.ResolutionDueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        (await fixture.Service.ProcessSlaBreachesAsync()).Should().Be(2);
        (await fixture.Service.ProcessSlaBreachesAsync()).Should().Be(0);

        fixture.AutomationTriggers.SlaCalls.Should().HaveCount(2);
        fixture.AutomationTriggers.SlaCalls.Select(call => call.BreachType)
            .Should().BeEquivalentTo("FirstResponse", "Resolution");
    }

    [Fact]
    public async Task SubmitSatisfactionRatingAsync_ShouldRecordTheRating_OnlyOnceForAResolvedTicket()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        await fixture.Service.SetStatusAsync(ticket.Id, SupportTicketStatuses.Resolved, "staff");

        var rated = await fixture.Service.SubmitSatisfactionRatingAsync(ticket.Id, 5, "Great support!");
        rated.SatisfactionRating.Should().Be(5);
        rated.SatisfactionComment.Should().Be("Great support!");

        var act = () => fixture.Service.SubmitSatisfactionRatingAsync(ticket.Id, 3, null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SubmitSatisfactionRatingAsync_ShouldReject_AnUnresolvedTicket()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var act = () => fixture.Service.SubmitSatisfactionRatingAsync(ticket.Id, 5, null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task SubmitSatisfactionRatingAsync_ShouldReject_AnOutOfRangeRating(int rating)
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");
        await fixture.Service.SetStatusAsync(ticket.Id, SupportTicketStatuses.Resolved, "staff");

        var act = () => fixture.Service.SubmitSatisfactionRatingAsync(ticket.Id, rating, null);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SetTagsAsync_ShouldNormalizeAndTrimTheCsvList()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var tagged = await fixture.Service.SetTagsAsync(ticket.Id, " billing ,  urgent,billing", "staff");

        tagged.TagsCsv.Should().Be("billing, urgent, billing");
    }

    [Fact]
    public async Task AssignAsync_ShouldClearAssignment_WhenGivenAnEmptyValue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var ticket = await fixture.Service.CreateTicketAsync(
            contact.Id, "Question", "Body", SupportTicketAuthorTypes.Contact, "Jamie Rivera");

        var assigned = await fixture.Service.AssignAsync(ticket.Id, "alex", "staff");
        assigned.AssignedToUsername.Should().Be("alex");

        var cleared = await fixture.Service.AssignAsync(ticket.Id, "  ", "staff");
        cleared.AssignedToUsername.Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, DateTimeOffset now)
        {
            _connection = connection;
            Db = db;
            AdminSender = new CapturingAdminSender();
            ContactSender = new CapturingContactSender();
            AutomationTriggers = new RecordingAutomationTriggerService();
            Service = new SupportTicketService(
                db,
                new FixedTimeProvider(now),
                AdminSender,
                ContactSender,
                Options.Create(new SupportNotificationOptions { NotifyEmail = "grant@gwsapp.net", AdminBaseUrl = "https://admin.example.test" }),
                NullLogger<SupportTicketService>.Instance,
                automationTriggerService: AutomationTriggers);
        }

        public ApplicationDbContext Db { get; }
        public SupportTicketService Service { get; }
        public CapturingAdminSender AdminSender { get; }
        public CapturingContactSender ContactSender { get; }
        public RecordingAutomationTriggerService AutomationTriggers { get; }

        public static async Task<Fixture> CreateAsync(DateTimeOffset? now = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, now ?? DateTimeOffset.UtcNow);
        }

        public async Task<Contact> AddContactAsync(string fullName, string? email = null)
        {
            var contact = new Contact { FullName = fullName, Email = email };
            Db.Contacts.Add(contact);
            await Db.SaveChangesAsync();
            return contact;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingAdminSender : IGrowthReportEmailSender
    {
        public GrowthReportDeliveryConfiguration Configuration { get; set; } = new(true, "ready");
        public List<GrowthReportEmail> Messages { get; } = [];

        public Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default)
        {
            Messages.Add(email);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingContactSender : IClientPortalEmailSender
    {
        public List<(string ToEmail, string ContactName, string TicketSubject, string PortalUrl)> Messages { get; } = [];

        public Task SendLoginLinkAsync(string toEmail, string contactName, string loginUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SendTicketReplyNotificationAsync(
            string toEmail, string contactName, string ticketSubject, string portalUrl, CancellationToken cancellationToken = default)
        {
            Messages.Add((toEmail, contactName, ticketSubject, portalUrl));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAutomationTriggerService : IAutomationTriggerService
    {
        public List<(Guid TicketId, string Subject, string ContactName, string Priority)> CreatedCalls { get; } = [];
        public List<(Guid TicketId, string AuthorType, string AuthorName, string Body)> RepliedCalls { get; } = [];
        public List<(Guid TicketId, string BreachType)> SlaCalls { get; } = [];

        public Task<int> TriggerSupportTicketCreatedAsync(
            Guid ticketId, string subject, string contactName, string priority, CancellationToken cancellationToken = default)
        {
            CreatedCalls.Add((ticketId, subject, contactName, priority));
            return Task.FromResult(1);
        }

        public Task<int> TriggerSupportTicketRepliedAsync(
            Guid ticketId, string authorType, string authorName, string body, CancellationToken cancellationToken = default)
        {
            RepliedCalls.Add((ticketId, authorType, authorName, body));
            return Task.FromResult(1);
        }

        public Task<int> TriggerSupportTicketSlaBreachedAsync(
            Guid ticketId, string subject, string contactName, string priority, string breachType,
            DateTimeOffset dueAt, CancellationToken cancellationToken = default)
        {
            SlaCalls.Add((ticketId, breachType));
            return Task.FromResult(1);
        }

        public Task<AutomationExecutionView?> TriggerWebhookAsync(
            string path, string inputJson, string? providedSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> RunDueSchedulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ResumeDueWaitsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationExecutionView?> ResumeViaWebhookAsync(
            string token, string bodyJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerDatabaseRowChangedAsync(
            Guid wikiDatabaseId, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerCrmDealStageChangedAsync(
            string stage, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerCmsPagePublishedAsync(
            Guid siteId, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerSentinelChatPromptSubmittedAsync(
            string prompt, Guid? conversationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerCmsFormSubmittedAsync(
            Guid siteId, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
