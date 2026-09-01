using FluentAssertions;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class EmailCampaignServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EnrollContactAsync_ShouldOnlySucceed_ForAnActiveCampaignAndASubscribedContact()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draftCampaign = await fixture.CreateCampaignAsync("Draft campaign", activate: false);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");

        (await fixture.Service.EnrollContactAsync(draftCampaign.Id, contact.Id)).Should().BeFalse("the campaign is still a Draft");

        var activeCampaign = await fixture.CreateCampaignAsync("Active campaign", activate: true);
        (await fixture.Service.EnrollContactAsync(activeCampaign.Id, contact.Id)).Should().BeTrue();
        (await fixture.Service.EnrollContactAsync(activeCampaign.Id, contact.Id)).Should().BeFalse("already enrolled");
    }

    [Fact]
    public async Task EnrollContactAsync_ShouldRefuseAnUnsubscribedContact()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("Campaign", activate: true);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        fixture.Db.Contacts.Find(contact.Id)!.UnsubscribedFromCampaignsAt = Now;
        await fixture.Db.SaveChangesAsync();

        (await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessDueSendsAsync_ShouldSendTheFirstStepImmediately_AndScheduleTheNextByItsDelay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("Welcome series", activate: true, steps:
        [
            ("Welcome!", "Hi {{FirstName}}, welcome aboard.", 0),
            ("Checking in", "How's it going, {{FirstName}}?", 3)
        ]);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id);

        var attempted = await fixture.Service.ProcessDueSendsAsync();

        attempted.Should().Be(1);
        fixture.EmailSender.Sent.Should().ContainSingle();
        fixture.EmailSender.Sent[0].Subject.Should().Be("Welcome!");
        fixture.EmailSender.Sent[0].Body.Should().Contain("Hi Jamie, welcome aboard.");

        var enrollment = await fixture.Db.EmailCampaignEnrollments.SingleAsync();
        enrollment.NextStepIndex.Should().Be(1);
        enrollment.Status.Should().Be(EmailCampaignEnrollmentStatuses.Active);
        enrollment.NextSendAt.Should().Be(Now.AddDays(3));
    }

    [Fact]
    public async Task ProcessDueSendsAsync_ShouldNotSendBeforeTheScheduledTime()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("Welcome series", activate: true, steps: [("Welcome!", "Hi", 0), ("Follow up", "Hi again", 5)]);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id);
        await fixture.Service.ProcessDueSendsAsync();
        fixture.EmailSender.Sent.Clear();

        (await fixture.Service.ProcessDueSendsAsync()).Should().Be(0, "the second step isn't due for 5 more days");
        fixture.EmailSender.Sent.Should().BeEmpty();

        fixture.TimeProvider.Advance(TimeSpan.FromDays(5));
        (await fixture.Service.ProcessDueSendsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ProcessDueSendsAsync_ShouldCompleteTheEnrollment_AfterTheLastStep()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("One-shot", activate: true, steps: [("Only step", "Body", 0)]);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id);

        await fixture.Service.ProcessDueSendsAsync();

        var enrollment = await fixture.Db.EmailCampaignEnrollments.SingleAsync();
        enrollment.Status.Should().Be(EmailCampaignEnrollmentStatuses.Completed);
        enrollment.CompletedAt.Should().Be(Now);
        enrollment.NextSendAt.Should().BeNull();
    }

    [Fact]
    public async Task UnsubscribeByTokenAsync_ShouldSuppressFutureSends_AndCancelActiveEnrollments()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("Welcome series", activate: true, steps: [("Step 1", "Body", 0), ("Step 2", "Body", 1)]);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id);
        await fixture.Service.ProcessDueSendsAsync();
        var unsubscribeUrl = fixture.EmailSender.Sent.Single().UnsubscribeUrl;
        var token = unsubscribeUrl.Split('/').Last();

        (await fixture.Service.UnsubscribeByTokenAsync(token)).Should().BeTrue();

        (await fixture.Db.Contacts.SingleAsync()).UnsubscribedFromCampaignsAt.Should().NotBeNull();
        (await fixture.Db.EmailCampaignEnrollments.SingleAsync()).Status.Should().Be(EmailCampaignEnrollmentStatuses.Cancelled);

        fixture.TimeProvider.Advance(TimeSpan.FromDays(1));
        (await fixture.Service.ProcessDueSendsAsync()).Should().Be(0, "the enrollment is cancelled, not due");
    }

    [Fact]
    public async Task UnsubscribeByTokenAsync_ShouldReturnFalse_ForAGarbageToken()
    {
        await using var fixture = await Fixture.CreateAsync();

        (await fixture.Service.UnsubscribeByTokenAsync("not-a-real-token")).Should().BeFalse();
    }

    [Fact]
    public async Task ResubscribeContactAsync_ShouldClearSuppression_AndKeepCancelledEnrollmentHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("Welcome", activate: true, steps: [("Hello", "Body", 0)]);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id);
        contact.UnsubscribedFromCampaignsAt = Now;
        var enrollment = await fixture.Db.EmailCampaignEnrollments.SingleAsync();
        enrollment.Status = EmailCampaignEnrollmentStatuses.Cancelled;
        await fixture.Db.SaveChangesAsync();

        (await fixture.Service.ResubscribeContactAsync(contact.Id, "admin@example.test")).Should().BeTrue();

        var reloaded = await fixture.Db.Contacts.SingleAsync();
        reloaded.UnsubscribedFromCampaignsAt.Should().BeNull();
        reloaded.UpdatedBy.Should().Be("admin@example.test");
        (await fixture.Db.EmailCampaignEnrollments.SingleAsync()).Status.Should().Be(EmailCampaignEnrollmentStatuses.Cancelled);
    }

    [Fact]
    public async Task ListEnrollmentsForContactAsync_ShouldReturnEmpty_ForAContactWithNoEnrollments()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");

        var enrollments = await fixture.Service.ListEnrollmentsForContactAsync(contact.Id);

        enrollments.Should().BeEmpty();
    }

    [Fact]
    public async Task ListEnrollmentsForContactAsync_ShouldReturnEveryCampaignTheContactIsEnrolledIn_NewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        var welcome = await fixture.CreateCampaignAsync("Welcome series", activate: true, steps: [("Step 1", "Body", 0), ("Step 2", "Body", 3)]);
        var reengage = await fixture.CreateCampaignAsync("Re-engagement", activate: true, steps: [("Only step", "Body", 0)]);
        await fixture.Service.EnrollContactAsync(welcome.Id, contact.Id);
        await fixture.Service.EnrollContactAsync(reengage.Id, contact.Id);
        await fixture.Service.ProcessDueSendsAsync();

        var enrollments = await fixture.Service.ListEnrollmentsForContactAsync(contact.Id);

        enrollments.Should().HaveCount(2);
        enrollments.Select(item => item.CampaignName).Should().Contain(["Welcome series", "Re-engagement"]);
        var welcomeEnrollment = enrollments.Single(item => item.CampaignId == welcome.Id);
        welcomeEnrollment.TotalSteps.Should().Be(2);
        welcomeEnrollment.NextStepIndex.Should().Be(1, "the first step was sent immediately");
        welcomeEnrollment.Status.Should().Be(EmailCampaignEnrollmentStatuses.Active);
        var reengageEnrollment = enrollments.Single(item => item.CampaignId == reengage.Id);
        reengageEnrollment.TotalSteps.Should().Be(1);
        reengageEnrollment.Status.Should().Be(EmailCampaignEnrollmentStatuses.Completed, "its only step already sent");
    }

    [Fact]
    public async Task ProcessDueSendsAsync_ShouldSkipAPausedCampaign()
    {
        await using var fixture = await Fixture.CreateAsync();
        var campaign = await fixture.CreateCampaignAsync("Pausable", activate: true, steps: [("Step 1", "Body", 0)]);
        var contact = await fixture.AddContactAsync("Jamie Rivera", "jamie@example.test");
        await fixture.Service.EnrollContactAsync(campaign.Id, contact.Id);
        await fixture.Service.SetCampaignStatusAsync(campaign.Id, EmailCampaignStatuses.Paused, "owner");

        (await fixture.Service.ProcessDueSendsAsync()).Should().Be(0);
        fixture.EmailSender.Sent.Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, CapturingEmailSender emailSender, FixedTimeProvider timeProvider, EmailCampaignService service)
        {
            _connection = connection;
            Db = db;
            EmailSender = emailSender;
            TimeProvider = timeProvider;
            Service = service;
        }

        public ApplicationDbContext Db { get; }
        public CapturingEmailSender EmailSender { get; }
        public FixedTimeProvider TimeProvider { get; }
        public EmailCampaignService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var emailSender = new CapturingEmailSender();
            var timeProvider = new FixedTimeProvider(Now);
            var options = Options.Create(new EmailCampaignEmailOptions { PublicBaseUrl = "https://app.example.test" });
            var service = new EmailCampaignService(
                db, emailSender, new EphemeralDataProtectionProvider(), options, timeProvider, NullLogger<EmailCampaignService>.Instance);
            return new Fixture(connection, db, emailSender, timeProvider, service);
        }

        public async Task<Contact> AddContactAsync(string fullName, string email)
        {
            var contact = new Contact { FullName = fullName, Email = email };
            Db.Contacts.Add(contact);
            await Db.SaveChangesAsync();
            return contact;
        }

        public async Task<EmailCampaignView> CreateCampaignAsync(
            string name, bool activate, IReadOnlyList<(string Subject, string Body, int DelayDays)>? steps = null)
        {
            var editor = new EmailCampaignEditorModel
            {
                Name = name,
                Steps = (steps ?? [("Step 1", "Body", 0)])
                    .Select(step => new EmailCampaignStepEditorModel { Subject = step.Subject, Body = step.Body, DelayDays = step.DelayDays })
                    .ToList()
            };
            var saved = await Service.SaveCampaignAsync(editor, "owner");
            if (activate)
            {
                saved = await Service.SetCampaignStatusAsync(saved.Id, EmailCampaignStatuses.Active, "owner");
            }
            return saved;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CapturingEmailSender : IEmailCampaignEmailSender
    {
        public List<(string ToEmail, string Subject, string Body, string UnsubscribeUrl)> Sent { get; } = [];

        public Task SendStepAsync(string toEmail, string subject, string body, string unsubscribeUrl, CancellationToken cancellationToken = default)
        {
            Sent.Add((toEmail, subject, body, unsubscribeUrl));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
