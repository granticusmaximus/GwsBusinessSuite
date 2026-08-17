using FluentAssertions;
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

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            AdminSender = new CapturingAdminSender();
            ContactSender = new CapturingContactSender();
            Service = new SupportTicketService(
                db,
                new FixedTimeProvider(DateTimeOffset.UtcNow),
                AdminSender,
                ContactSender,
                Options.Create(new SupportNotificationOptions { NotifyEmail = "grant@gwsapp.net", AdminBaseUrl = "https://admin.example.test" }),
                NullLogger<SupportTicketService>.Instance);
        }

        public ApplicationDbContext Db { get; }
        public SupportTicketService Service { get; }
        public CapturingAdminSender AdminSender { get; }
        public CapturingContactSender ContactSender { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
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
}
