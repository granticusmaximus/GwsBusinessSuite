using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class FormSubmissionServiceTests
{
    private static Dictionary<string, string> AdaFields(string message = "Interested in a quote.") => new()
    {
        ["name"] = "Ada Lovelace",
        ["email"] = "ada@example.com",
        ["message"] = message
    };

    [Fact]
    public async Task SubmitAsync_ShouldStoreAndReturnTheSubmission()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);

        var submission = await service.SubmitAsync(page.Id, AdaFields());

        submission.FieldsJson.Should().Contain("Ada Lovelace");
        submission.FieldsJson.Should().Contain("ada@example.com");
        submission.FieldsJson.Should().Contain("Interested in a quote.");
        submission.IsRead.Should().BeFalse();

        var listed = await service.ListAsync(page.Id);
        listed.Should().ContainSingle(s => s.Id == submission.Id);
    }

    [Fact]
    public async Task SubmitAsync_ShouldRejectSubmissionsWithNoNonEmptyFields()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);

        var action = async () => await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "   " });

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmitAsync_ShouldIgnoreBlankFields_ButKeepNonBlankOnes()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);

        var submission = await service.SubmitAsync(page.Id, new Dictionary<string, string>
        {
            ["name"] = "Ada",
            ["optional-field"] = ""
        });

        submission.FieldsJson.Should().Contain("Ada");
        submission.FieldsJson.Should().NotContain("optional-field");
    }

    [Fact]
    public async Task SubmitAsync_ShouldThrow_WhenPageDoesNotExist()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        var action = async () => await service.SubmitAsync(Guid.NewGuid(), AdaFields());

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_ShouldReturnMostRecentSubmissionsFirst()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);

        await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "First" });
        await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "Second" });

        var listed = await service.ListAsync(page.Id);

        listed.Should().HaveCount(2);
        listed[0].FieldsJson.Should().Contain("Second");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheSubmission()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var submission = await service.SubmitAsync(page.Id, AdaFields());

        await service.DeleteAsync(submission.Id);

        (await service.ListAsync(page.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task MarkReadAsync_ShouldSetIsReadToTrue()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var submission = await service.SubmitAsync(page.Id, AdaFields());

        await service.MarkReadAsync(submission.Id);

        (await service.ListAsync(page.Id)).Single().IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingThePage_ShouldAlsoRemoveItsSubmissions()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        await service.SubmitAsync(page.Id, AdaFields());

        await cmsBuilder.TrashPageAsync(page.Id);
        await cmsBuilder.DeletePageAsync(page.Id);

        (await service.ListAsync(page.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_ShouldSendANotificationEmail_WhenTheSitesNotificationEmailIsSet()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder, notificationEmail: "grant@gwsapp.net");
        var sender = new CapturingSender { Configuration = new GrowthReportDeliveryConfiguration(true, "ready") };
        var service = CreateService(db, sender);

        var submission = await service.SubmitAsync(page.Id, AdaFields());

        sender.Messages.Should().ContainSingle();
        var email = sender.Messages.Single();
        email.RecipientEmail.Should().Be("grant@gwsapp.net");
        email.Subject.Should().Contain(page.Title);
        email.PlainTextBody.Should().Contain("Ada Lovelace");
        email.PlainTextBody.Should().Contain(submission.Id.ToString());
        email.HtmlBody.Should().Contain(submission.Id.ToString());
    }

    [Fact]
    public async Task SubmitAsync_ShouldNotSendAnEmail_WhenTheSiteHasNoNotificationEmailConfigured()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var sender = new CapturingSender { Configuration = new GrowthReportDeliveryConfiguration(true, "ready") };
        var service = CreateService(db, sender);

        await service.SubmitAsync(page.Id, AdaFields());

        sender.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_ShouldStillSaveTheSubmission_WhenSendingTheNotificationEmailFails()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder, notificationEmail: "grant@gwsapp.net");
        var sender = new CapturingSender
        {
            Configuration = new GrowthReportDeliveryConfiguration(true, "ready"),
            Failure = new InvalidOperationException("SMTP is down")
        };
        var service = CreateService(db, sender);

        var submission = await service.SubmitAsync(page.Id, AdaFields());

        submission.Should().NotBeNull();
        (await service.ListAsync(page.Id)).Should().ContainSingle(s => s.Id == submission.Id);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnTheSubmission_RegardlessOfWhichPageItBelongsTo()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var service = CreateService(db);
        var submission = await service.SubmitAsync(page.Id, AdaFields());

        var fetched = await service.GetAsync(submission.Id);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(submission.Id);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_ForAnUnknownSubmissionId()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);

        (await service.GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task SubmitAsync_ShouldPopulateStructuredIdentityColumns_FromIdentityFields()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var service = CreateService(db);
        var page = await CreatePageAsync(db, cmsBuilder);

        var submission = await service.SubmitAsync(page.Id, AdaFields(), new Dictionary<string, string>
        {
            ["email"] = "ada@example.com",
            ["name"] = "Ada Lovelace",
            ["company"] = "Analytical Engines Ltd"
        });

        submission.Email.Should().Be("ada@example.com");
        submission.FullName.Should().Be("Ada Lovelace");
        submission.Company.Should().Be("Analytical Engines Ltd");
        submission.ContactId.Should().BeNull("auto-create was not requested");
    }

    [Fact]
    public async Task SubmitAsync_ShouldAutoCreateAndLinkAContact_WhenOptedInWithAnEmail()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var service = CreateService(db, crmService: new CrmService(db));

        var submission = await service.SubmitAsync(page.Id, AdaFields(),
            new Dictionary<string, string> { ["email"] = "ada@example.com", ["name"] = "Ada Lovelace" },
            autoCreateContact: true);

        submission.ContactId.Should().NotBeNull();
        var contact = await db.Contacts.SingleAsync(c => c.Id == submission.ContactId);
        contact.Email.Should().Be("ada@example.com");
        contact.FullName.Should().Be("Ada Lovelace");
        (await db.ContactActivities.CountAsync(a => a.ContactId == contact.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SubmitAsync_ShouldMatchAnExistingContactByEmail_RatherThanCreatingADuplicate()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        db.Contacts.Add(new Contact { FullName = "Existing Ada", Email = "ada@example.com" });
        await db.SaveChangesAsync();
        var service = CreateService(db, crmService: new CrmService(db));

        await service.SubmitAsync(page.Id, AdaFields(),
            new Dictionary<string, string> { ["email"] = "ada@example.com" }, autoCreateContact: true);

        (await db.Contacts.CountAsync()).Should().Be(1, "should match the existing contact, not create a duplicate");
    }

    [Fact]
    public async Task SubmitAsync_ShouldNotCreateAContact_WhenAutoCreateIsOff()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var service = CreateService(db, crmService: new CrmService(db));

        var submission = await service.SubmitAsync(page.Id, AdaFields(),
            new Dictionary<string, string> { ["email"] = "ada@example.com" }, autoCreateContact: false);

        submission.ContactId.Should().BeNull();
        (await db.Contacts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SubmitAsync_ShouldFireTheCmsFormSubmittedTrigger()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var trigger = new RecordingAutomationTriggerService();
        var service = CreateService(db, automationTriggerService: trigger);

        await service.SubmitAsync(page.Id, AdaFields());

        trigger.Calls.Should().ContainSingle();
        trigger.Calls[0].SiteId.Should().Be(page.SiteId);
        trigger.Calls[0].InputJson.Should().Contain("ada@example.com");
    }

    [Fact]
    public async Task ListForContactAsync_ShouldReturnOnlySubmissionsLinkedToThatContact()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var service = CreateService(db);
        var linked = await service.SubmitAsync(page.Id, AdaFields());
        var unlinked = await service.SubmitAsync(page.Id, new Dictionary<string, string> { ["name"] = "Someone Else" });
        var contactId = Guid.NewGuid();
        await service.LinkToContactAsync(linked.Id, contactId);

        var result = await service.ListForContactAsync(contactId);

        result.Should().ContainSingle(s => s.Id == linked.Id);
        result.Should().NotContain(s => s.Id == unlinked.Id);
    }

    [Fact]
    public async Task LinkToContactAsync_ShouldSetTheContactId()
    {
        await using var db = await CreateDbAsync();
        var cmsBuilder = new CmsBuilderService(db);
        var page = await CreatePageAsync(db, cmsBuilder);
        var service = CreateService(db);
        var submission = await service.SubmitAsync(page.Id, AdaFields());
        var contactId = Guid.NewGuid();

        await service.LinkToContactAsync(submission.Id, contactId);

        (await service.GetAsync(submission.Id))!.ContactId.Should().Be(contactId);
    }

    private sealed class RecordingAutomationTriggerService : IAutomationTriggerService
    {
        public List<(Guid SiteId, string InputJson)> Calls { get; } = [];

        public Task<int> TriggerCmsFormSubmittedAsync(Guid siteId, string inputJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((siteId, inputJson));
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
        public Task<int> TriggerSupportTicketCreatedAsync(
            Guid ticketId, string subject, string contactName, string priority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerSupportTicketRepliedAsync(
            Guid ticketId, string authorType, string authorName, string body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerSupportTicketSlaBreachedAsync(
            Guid ticketId, string subject, string contactName, string priority, string breachType,
            DateTimeOffset dueAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static FormSubmissionService CreateService(
        ApplicationDbContext db,
        IGrowthReportEmailSender? sender = null,
        ICrmService? crmService = null,
        IAutomationTriggerService? automationTriggerService = null) => new(
        db,
        sender ?? new CapturingSender(),
        Options.Create(new FormNotificationOptions { AdminBaseUrl = "https://admin.example.test" }),
        NullLogger<FormSubmissionService>.Instance,
        crmService,
        automationTriggerService);

    // CmsSiteEditorModel/SaveSiteAsync deliberately doesn't expose FormNotificationEmail (a
    // full-replace editor model with ~9 call sites across the admin UI - adding a new field
    // there risks the same "save silently wipes a field nobody remembered to populate" bug
    // class this app has hit before). Tests set it directly on the tracked entity instead, same
    // as the real seed step (EnsureGrantWatsonFormNotificationEmailAsync in Program.cs) does.
    private static async Task<GwsBusinessSuite.Domain.Entities.CmsPage> CreatePageAsync(
        ApplicationDbContext db, CmsBuilderService cmsBuilder, string notificationEmail = "")
    {
        var site = await cmsBuilder.SaveSiteAsync(new CmsSiteEditorModel { Name = "Test Site" });
        if (!string.IsNullOrWhiteSpace(notificationEmail))
        {
            var siteEntity = await db.CmsSites.FirstAsync(s => s.Id == site.Id);
            siteEntity.FormNotificationEmail = notificationEmail;
            await db.SaveChangesAsync();
        }
        return await cmsBuilder.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = "Contact",
            BlocksJson = "{\"sections\":[]}"
        });
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class CapturingSender : IGrowthReportEmailSender
    {
        public GrowthReportDeliveryConfiguration Configuration { get; set; } = new(false, "Not configured.");
        public List<GrowthReportEmail> Messages { get; } = [];
        public Exception? Failure { get; set; }

        public Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            Messages.Add(email);
            return Task.CompletedTask;
        }
    }
}
