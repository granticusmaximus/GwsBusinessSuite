using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Privacy;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class PrivacyOperationsServiceTests
{
    [Fact]
    public async Task AccessExport_ShouldRequireIdentityVerification_AndExcludeCredentials()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.AppUsers.Add(new AppUser
        {
            Username = "grant", PasswordHash = "secret-hash", MfaSecretProtected = "secret-seed",
            MfaRecoveryCodeHashesJson = "[\"secret-code\"]", Role = AppRoles.Admin
        });
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Access, "grant"));

        var beforeVerification = async () => await fixture.Service.ExportSubjectDataAsync(request.Id);
        await beforeVerification.Should().ThrowAsync<InvalidOperationException>();

        await fixture.Service.VerifyIdentityAsync(request.Id);
        var export = await fixture.Service.ExportSubjectDataAsync(request.Id);
        var json = Encoding.UTF8.GetString(export.Content);

        json.Should().Contain("grant").And.Contain("MfaEnabled");
        json.Should().NotContain("secret-hash").And.NotContain("secret-seed").And.NotContain("secret-code");
        (await fixture.Db.SecurityAuditEvents.CountAsync(x => x.Action == "SubjectDataExported")).Should().Be(1);
    }

    [Fact]
    public async Task CompleteRequestAsync_ShouldOnlyAllowErasureFulfillmentAfterDeletionExecutedAtIsSet()
    {
        // Regression guard: this used to be a boolean checkbox attestation with no real deletion
        // behind it. Fulfilled must now require DeleteSubjectDataAsync to have actually run and
        // succeeded for this exact request - not a human-checked box.
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Contacts.Add(new Contact { FullName = "Grant Example", Email = "grant@example.com" });
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "grant@example.com"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var beforeDeletion = async () => await fixture.Service.CompleteRequestAsync(
            request.Id, PrivacyRequestStatuses.Fulfilled, "Deleted everywhere.");
        await beforeDeletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DeleteSubjectDataAsync*");

        await fixture.Service.DeleteSubjectDataAsync(request.Id);
        await fixture.Service.CompleteRequestAsync(request.Id, PrivacyRequestStatuses.Fulfilled, "Deleted everywhere.");

        var dashboard = await fixture.Service.GetDashboardAsync();
        var view = dashboard.Requests.Single(x => x.Id == request.Id);
        view.Status.Should().Be(PrivacyRequestStatuses.Fulfilled);
        view.CompletedAt.Should().NotBeNull();
        view.DeletionExecutedAt.Should().NotBeNull();
        (await fixture.Db.SecurityAuditEvents.CountAsync(x =>
            x.Action == "PrivacyRequestStatusChanged" && x.TargetId == request.Id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task ErasureRequest_ShouldStillAllowDenialWithoutRunningDeletion()
    {
        // Denying an erasure request (nothing was deleted because the request was refused) must
        // not require DeletionExecutedAt - that gate is specific to asserting Fulfilled.
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "grant"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        await fixture.Service.CompleteRequestAsync(
            request.Id, PrivacyRequestStatuses.Denied, "Identity could not be fully verified.");

        var dashboard = await fixture.Service.GetDashboardAsync();
        dashboard.Requests.Single(x => x.Id == request.Id).Status.Should().Be(PrivacyRequestStatuses.Denied);
    }

    [Fact]
    public async Task DeleteSubjectDataAsync_ShouldCascadeAcrossContactRelatedTablesAndLeaveControlDataUntouched()
    {
        await using var fixture = await Fixture.CreateAsync();
        var subject = new Contact { FullName = "Subject Person", Email = "subject@example.com" };
        var control = new Contact { FullName = "Control Person", Email = "control@example.com" };
        var article = new Article { Slug = "test-article", Title = "Test Article" };
        fixture.Db.Contacts.AddRange(subject, control);
        fixture.Db.Articles.Add(article);
        await fixture.Db.SaveChangesAsync();

        async Task SeedGraphAsync(Contact contact, string suffix)
        {
            fixture.Db.ContactActivities.Add(new ContactActivity { ContactId = contact.Id, Note = $"Note {suffix}" });
            fixture.Db.Deals.Add(new Deal { ContactId = contact.Id, Title = $"Deal {suffix}" });
            fixture.Db.ClientPortalLoginTokens.Add(new ClientPortalLoginToken { ContactId = contact.Id, TokenHash = $"hash-{suffix}", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
            var campaign = new EmailCampaign { Name = $"Campaign {suffix}" };
            fixture.Db.EmailCampaigns.Add(campaign);
            await fixture.Db.SaveChangesAsync();
            var enrollment = new EmailCampaignEnrollment { CampaignId = campaign.Id, ContactId = contact.Id };
            fixture.Db.EmailCampaignEnrollments.Add(enrollment);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.EmailCampaignSendLogs.Add(new EmailCampaignSendLog { EnrollmentId = enrollment.Id, StepId = Guid.NewGuid(), Succeeded = true });
            var bookingType = new BookingType { Title = $"Type {suffix}", Slug = $"type-{suffix}" };
            fixture.Db.BookingTypes.Add(bookingType);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.Bookings.Add(new Booking
            {
                BookingTypeId = bookingType.Id, ContactId = contact.Id, StartsAt = DateTimeOffset.UtcNow, EndsAt = DateTimeOffset.UtcNow.AddHours(1),
                AttendeeName = contact.FullName, AttendeeEmail = contact.Email!, ManageTokenHash = $"manage-{suffix}"
            });
            var ticket = new SupportTicket { ContactId = contact.Id, Subject = $"Ticket {suffix}" };
            fixture.Db.SupportTickets.Add(ticket);
            await fixture.Db.SaveChangesAsync();
            var message = new SupportTicketMessage { TicketId = ticket.Id, AuthorName = contact.FullName, Body = $"Message {suffix}" };
            fixture.Db.SupportTicketMessages.Add(message);
            await fixture.Db.SaveChangesAsync();
            fixture.Db.SupportTicketAttachments.Add(new SupportTicketAttachment { MessageId = message.Id, FileName = "f.txt", ContentType = "text/plain", DataUri = "data:," });
            fixture.Db.Comments.Add(new Comment { ArticleId = article.Id, AuthorName = contact.FullName, AuthorEmail = contact.Email! });
            await fixture.Db.SaveChangesAsync();
        }
        await SeedGraphAsync(subject, "subject");
        await SeedGraphAsync(control, "control");

        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, subject.Email!));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var summary = await fixture.Service.DeleteSubjectDataAsync(request.Id);

        summary.BackupPath.Should().NotBeNullOrWhiteSpace();
        fixture.Backups.CallCount.Should().Be(1);

        (await fixture.Db.Contacts.CountAsync(x => x.Id == subject.Id)).Should().Be(0);
        (await fixture.Db.ContactActivities.CountAsync(x => x.ContactId == subject.Id)).Should().Be(0);
        (await fixture.Db.Deals.CountAsync(x => x.ContactId == subject.Id)).Should().Be(0);
        (await fixture.Db.ClientPortalLoginTokens.CountAsync(x => x.ContactId == subject.Id)).Should().Be(0);
        (await fixture.Db.EmailCampaignEnrollments.CountAsync(x => x.ContactId == subject.Id)).Should().Be(0);
        (await fixture.Db.EmailCampaignSendLogs.CountAsync()).Should().Be(1, "only the control contact's send log should remain");
        (await fixture.Db.Bookings.CountAsync(x => x.ContactId == subject.Id)).Should().Be(0);
        (await fixture.Db.SupportTickets.CountAsync(x => x.ContactId == subject.Id)).Should().Be(0);
        (await fixture.Db.SupportTicketMessages.CountAsync()).Should().Be(1, "only the control ticket's message should remain");
        (await fixture.Db.SupportTicketAttachments.CountAsync()).Should().Be(1, "only the control ticket's attachment should remain");
        (await fixture.Db.Comments.CountAsync(x => x.AuthorEmail == subject.Email)).Should().Be(0);

        (await fixture.Db.Contacts.CountAsync(x => x.Id == control.Id)).Should().Be(1);
        (await fixture.Db.ContactActivities.CountAsync(x => x.ContactId == control.Id)).Should().Be(1);
        (await fixture.Db.Deals.CountAsync(x => x.ContactId == control.Id)).Should().Be(1);
        (await fixture.Db.ClientPortalLoginTokens.CountAsync(x => x.ContactId == control.Id)).Should().Be(1);
        (await fixture.Db.EmailCampaignEnrollments.CountAsync(x => x.ContactId == control.Id)).Should().Be(1);
        (await fixture.Db.Bookings.CountAsync(x => x.ContactId == control.Id)).Should().Be(1);
        (await fixture.Db.SupportTickets.CountAsync(x => x.ContactId == control.Id)).Should().Be(1);
        (await fixture.Db.Comments.CountAsync(x => x.AuthorEmail == control.Email)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteSubjectDataAsync_ShouldDeleteAResolvedAppUserAndItsTextMatchedRows()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.AppUsers.Add(new AppUser { Username = "subject-user", Role = AppRoles.Author, IsActive = true });
        fixture.Db.AppUsers.Add(new AppUser { Username = "control-user", Role = AppRoles.Author, IsActive = true });
        fixture.Db.SentinelAiRuns.Add(new SentinelAiRun { ConversationId = Guid.NewGuid(), Action = SentinelAiActions.Ask, Instruction = "i", Output = "o", Model = "m", CreatedBy = "subject-user" });
        fixture.Db.SentinelAiRuns.Add(new SentinelAiRun { ConversationId = Guid.NewGuid(), Action = SentinelAiActions.Ask, Instruction = "i", Output = "o", Model = "m", CreatedBy = "control-user" });
        var show = new PodcastShow { Title = "Test Show" };
        fixture.Db.PodcastShows.Add(show);
        await fixture.Db.SaveChangesAsync();
        var episode = new PodcastEpisode { PodcastShowId = show.Id, Title = "Episode 1" };
        fixture.Db.PodcastEpisodes.Add(episode);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.PodcastListenProgresses.Add(new PodcastListenProgress { Username = "subject-user", EpisodeId = episode.Id });
        fixture.Db.PodcastListenProgresses.Add(new PodcastListenProgress { Username = "control-user", EpisodeId = episode.Id });
        await fixture.Db.SaveChangesAsync();

        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "subject-user"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var summary = await fixture.Service.DeleteSubjectDataAsync(request.Id);

        summary.Tables.Should().Contain(x => x.TableName == "AppUsers" && x.DeletedCount == 1);
        (await fixture.Db.AppUsers.CountAsync(x => x.Username == "subject-user")).Should().Be(0);
        (await fixture.Db.SentinelAiRuns.CountAsync(x => x.CreatedBy == "subject-user")).Should().Be(0);
        (await fixture.Db.PodcastListenProgresses.CountAsync(x => x.Username == "subject-user")).Should().Be(0);

        (await fixture.Db.AppUsers.CountAsync(x => x.Username == "control-user")).Should().Be(1);
        (await fixture.Db.SentinelAiRuns.CountAsync(x => x.CreatedBy == "control-user")).Should().Be(1);
        (await fixture.Db.PodcastListenProgresses.CountAsync(x => x.Username == "control-user")).Should().Be(1);
    }

    [Fact]
    public async Task DeleteSubjectDataAsync_ShouldRefuseToDeleteTheLastActiveAdmin()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.AppUsers.Add(new AppUser { Username = "sole-admin", Role = AppRoles.Admin, IsActive = true });
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "sole-admin"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var act = async () => await fixture.Service.DeleteSubjectDataAsync(request.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last active admin*");
        (await fixture.Db.AppUsers.CountAsync(x => x.Username == "sole-admin")).Should().Be(1);
        fixture.Backups.CallCount.Should().Be(0, "the guard must run before any backup/deletion is attempted");
    }

    [Fact]
    public async Task DeleteSubjectDataAsync_ShouldAbortWithoutDeletingWhenBackupFails()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Contacts.Add(new Contact { FullName = "Grant Example", Email = "grant@example.com" });
        await fixture.Db.SaveChangesAsync();
        fixture.Backups.ShouldThrow = true;
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "grant@example.com"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var act = async () => await fixture.Service.DeleteSubjectDataAsync(request.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*backup could not be created*");
        (await fixture.Db.Contacts.CountAsync(x => x.Email == "grant@example.com")).Should().Be(1);
        var dashboard = await fixture.Service.GetDashboardAsync();
        dashboard.Requests.Single(x => x.Id == request.Id).DeletionExecutedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeleteSubjectDataAsync_ShouldStillReturnASummary_WhenTheErasureAuditWriteFails()
    {
        // Regression test: the post-delete audit calls used to run unguarded after the deletion
        // transaction already committed. An audit-write failure there used to propagate out of
        // DeleteSubjectDataAsync entirely, which the UI (PrivacyOperations.razor) shows as a
        // generic "operation could not be completed" - misreporting an already-irreversible,
        // already-successful erasure as a failure, and leaving a retry to fail again with
        // "nothing to delete".
        await using var fixture = await Fixture.CreateAsync(auditOverride: new ThrowingSecurityAuditService());
        fixture.Db.Contacts.Add(new Contact { FullName = "Grant Example", Email = "grant@example.com" });
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "grant@example.com"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var summary = await fixture.Service.DeleteSubjectDataAsync(request.Id);

        summary.Should().NotBeNull();
        (await fixture.Db.Contacts.CountAsync(x => x.Email == "grant@example.com")).Should().Be(0);
        var dashboard = await fixture.Service.GetDashboardAsync();
        dashboard.Requests.Single(x => x.Id == request.Id).DeletionExecutedAt.Should().NotBeNull();
    }

    // Only the erasure-completion audit events fail - CreateRequestAsync/VerifyIdentityAsync
    // have their own, earlier audit calls that must keep succeeding so the test can actually
    // reach DeleteSubjectDataAsync; this isolates the one failure this test is about.
    private sealed class ThrowingSecurityAuditService : ISecurityAuditService
    {
        public Task<Guid> RecordAsync(SecurityAuditInput input, CancellationToken cancellationToken = default) =>
            input.Action.StartsWith("Erasure", StringComparison.Ordinal)
                ? throw new InvalidOperationException("Simulated audit write failure.")
                : Task.FromResult(Guid.NewGuid());
        public Task<SecurityAuditPage> QueryAsync(SecurityAuditQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SecurityAuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DeleteSubjectDataAsync_ShouldThrowWhenSubjectDoesNotResolveToAnyRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "nobody@example.com"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var act = async () => await fixture.Service.DeleteSubjectDataAsync(request.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not resolve*");
        fixture.Backups.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task PreviewErasureAsync_ShouldReportCountsWithoutDeletingAnything()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Preview Person", Email = "preview@example.com" };
        fixture.Db.Contacts.Add(contact);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.Deals.Add(new Deal { ContactId = contact.Id, Title = "A deal" });
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, contact.Email!));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var preview = await fixture.Service.PreviewErasureAsync(request.Id);

        preview.SubjectResolved.Should().BeTrue();
        preview.Tables.Should().Contain(x => x.TableName == "Contacts" && x.RowCount == 1);
        preview.Tables.Should().Contain(x => x.TableName == "Deals" && x.RowCount == 1);
        (await fixture.Db.Contacts.CountAsync()).Should().Be(1, "preview must never delete anything");
        (await fixture.Db.Deals.CountAsync()).Should().Be(1, "preview must never delete anything");
        fixture.Backups.CallCount.Should().Be(0, "preview is a live read-only query and must never touch backups");
    }

    [Fact]
    public async Task PreviewErasureAsync_ShouldFlagInvoicesAsExcludedRatherThanCounted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = new Contact { FullName = "Invoice Person", Email = "invoice@example.com" };
        fixture.Db.Contacts.Add(contact);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.Invoices.Add(new Invoice { ContactId = contact.Id, Title = "Invoice 1" });
        await fixture.Db.SaveChangesAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, contact.Email!));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var preview = await fixture.Service.PreviewErasureAsync(request.Id);

        preview.ExcludedInvoiceCount.Should().Be(1);
        preview.Tables.Should().NotContain(x => x.TableName.Contains("Invoice"));

        await fixture.Service.DeleteSubjectDataAsync(request.Id);
        (await fixture.Db.Invoices.CountAsync()).Should().Be(1, "Invoices are never deleted automatically");
        (await fixture.Db.Contacts.CountAsync(x => x.Id == contact.Id)).Should().Be(1,
            "the Contact row must survive too - deleting it would cascade-delete its Invoice at the database level");
        (await fixture.Db.SecurityAuditEvents.AnyAsync(x => x.Action == "ErasureTableDeleted" && x.DetailsJson.Contains("Invoice")))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(PrivacyRequestTypes.Correction)]
    [InlineData(PrivacyRequestTypes.Restriction)]
    public async Task CorrectionAndRestrictionRequests_ShouldCompleteThroughTheFullLifecycle(string requestType)
    {
        // Untested request types until now - unlike Erasure, these have no special completion
        // gate, but the full create -> verify -> fulfill lifecycle itself was never exercised
        // for anything other than Access.
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.Service.CreateRequestAsync(new(requestType, "grant"));
        request.Status.Should().Be(PrivacyRequestStatuses.Received);

        var beforeVerification = async () => await fixture.Service.CompleteRequestAsync(
            request.Id, PrivacyRequestStatuses.Fulfilled, "Handled.");
        await beforeVerification.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Identity must be verified*");

        await fixture.Service.VerifyIdentityAsync(request.Id);
        await fixture.Service.CompleteRequestAsync(request.Id, PrivacyRequestStatuses.Fulfilled, "Handled.");

        var dashboard = await fixture.Service.GetDashboardAsync();
        var view = dashboard.Requests.Single(x => x.Id == request.Id);
        view.RequestType.Should().Be(requestType);
        view.Status.Should().Be(PrivacyRequestStatuses.Fulfilled);
        view.IdentityVerifiedAt.Should().NotBeNull();
        view.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task PersonalDataIncident_ShouldStartSeventyTwoHourNotificationClock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var awareness = DateTimeOffset.UtcNow.AddHours(-2);

        var incident = await fixture.Service.CreateIncidentAsync(new(
            "Potential disclosure", "Reviewing access logs", "High", awareness,
            true, true, awareness, "grant"));

        incident.RegulatorNotificationDueAt.Should().Be(awareness.AddHours(72));
        incident.PersonalDataInvolved.Should().BeTrue();
        (await fixture.Db.SecurityIncidentUpdates.CountAsync(x => x.SecurityIncidentId == incident.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Dashboard_ShouldSeedConservativeRetentionPolicies_WithPreviewOnlyCounts()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.WebAnalyticsEvents.Add(new WebAnalyticsEvent
        {
            EventName = "pageview", VisitorKey = "visitor", SessionKey = "session", Path = "/",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-500)
        });
        await fixture.Db.SaveChangesAsync();

        var dashboard = await fixture.Service.GetDashboardAsync();

        dashboard.RetentionPolicies.Should().Contain(x => x.DataCategory == "Web analytics" && x.EligibleRecordCount == 1 && !x.AutomationApproved);
        dashboard.RetentionPolicies.Should().Contain(x => x.DataCategory == "Security audit" && x.IsEnabled && !x.AutomationApproved);
        (await fixture.Db.WebAnalyticsEvents.CountAsync()).Should().Be(1, "dashboard previews must never delete data");

        var auditPolicy = dashboard.RetentionPolicies.Single(x => x.DataCategory == "Security audit");
        var approveAuditDeletion = async () => await fixture.Service.UpdateRetentionPolicyAsync(
            auditPolicy.Id, auditPolicy.RetentionDays, auditPolicy.LegalBasis, true, true);
        await approveAuditDeletion.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot be approved*");
    }

    [Fact]
    public async Task PurgeEligibleRecordsAsync_ShouldOnlyDeleteCategoriesThatAreBothEnabledAndApproved()
    {
        // Regression guard: the Privacy dashboard's retention policy/eligible-count preview
        // used to be entirely display-only - nothing ever actually purged expired rows. Both
        // "Enabled" and "Approved" must be true (each defaults to false/false on every seeded
        // policy) before a category is touched; everything else must survive untouched.
        await using var fixture = await Fixture.CreateAsync();
        var site = new CmsSite { Name = "Site", Slug = "site" };
        fixture.Db.CmsSites.Add(site);
        var page = new CmsPage { SiteId = site.Id, Title = "Page", Slug = "page" };
        fixture.Db.CmsPages.Add(page);
        fixture.Db.WebAnalyticsEvents.Add(new WebAnalyticsEvent
        {
            EventName = "pageview", VisitorKey = "visitor", SessionKey = "session", Path = "/",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-500),
            OccurredAtUnixSeconds = DateTimeOffset.UtcNow.AddDays(-500).ToUnixTimeSeconds()
        });
        fixture.Db.FormSubmissions.Add(new FormSubmission
        {
            PageId = page.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1000)
        });
        await fixture.Db.SaveChangesAsync();

        var dashboardBefore = await fixture.Service.GetDashboardAsync();
        var webAnalyticsPolicy = dashboardBefore.RetentionPolicies.Single(x => x.DataCategory == "Web analytics");
        await fixture.Service.UpdateRetentionPolicyAsync(
            webAnalyticsPolicy.Id, webAnalyticsPolicy.RetentionDays, webAnalyticsPolicy.LegalBasis,
            enabled: true, automationApproved: true);
        // "Form submissions" is left at its seeded defaults (Enabled=true, Approved=false).

        var deleted = await fixture.Service.PurgeEligibleRecordsAsync();

        deleted.Should().Be(1);
        (await fixture.Db.WebAnalyticsEvents.CountAsync()).Should().Be(0);
        (await fixture.Db.FormSubmissions.CountAsync()).Should().Be(1, "Form submissions was never approved for automated deletion");
        (await fixture.Db.SecurityAuditEvents.CountAsync(x => x.Action == "RetentionPurgeExecuted")).Should().Be(1);
    }

    [Fact]
    public async Task PurgeEligibleRecordsAsync_ShouldLeaveRecentRecordsAlone()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.WebAnalyticsEvents.Add(new WebAnalyticsEvent
        {
            EventName = "pageview", VisitorKey = "visitor", SessionKey = "session", Path = "/",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            OccurredAtUnixSeconds = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()
        });
        await fixture.Db.SaveChangesAsync();
        var policy = (await fixture.Service.GetDashboardAsync()).RetentionPolicies.Single(x => x.DataCategory == "Web analytics");
        await fixture.Service.UpdateRetentionPolicyAsync(policy.Id, policy.RetentionDays, policy.LegalBasis, true, true);

        var deleted = await fixture.Service.PurgeEligibleRecordsAsync();

        deleted.Should().Be(0);
        (await fixture.Db.WebAnalyticsEvents.CountAsync()).Should().Be(1);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db, PrivacyOperationsService service, FakeBackupOperations backups)
        { _connection = connection; Db = db; Service = service; Backups = backups; }
        public ApplicationDbContext Db { get; }
        public PrivacyOperationsService Service { get; }
        public FakeBackupOperations Backups { get; }

        public static async Task<Fixture> CreateAsync(ISecurityAuditService? auditOverride = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options); await db.Database.EnsureCreatedAsync();
            var audit = auditOverride ?? new SecurityAuditService(new TestDbContextFactory(connection), new FixedCurrentUserAccessor("grant"), new PassthroughProtector(), TimeProvider.System);
            var backups = new FakeBackupOperations();
            return new(connection, db, new PrivacyOperationsService(db, new FixedCurrentUserAccessor("grant"), audit, backups, TimeProvider.System, NullLogger<PrivacyOperationsService>.Instance), backups);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await _connection.DisposeAsync(); }
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class PassthroughProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }
    private sealed class FakeBackupOperations : IBackupOperations
    {
        public int CallCount { get; private set; }
        public bool ShouldThrow { get; set; }
        public Task<string> CreateBackupAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ShouldThrow) throw new InvalidOperationException("Simulated backup failure.");
            return Task.FromResult("/fake/backup-path.gwsbackup");
        }
    }
}
