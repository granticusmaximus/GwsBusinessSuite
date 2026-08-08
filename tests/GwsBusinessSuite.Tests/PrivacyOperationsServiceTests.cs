using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Privacy;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
    public async Task ErasureRequest_ShouldRefuseFulfillmentUntilDataDeletionIsExplicitlyConfirmed()
    {
        // Regression guard: this codebase has no real cascading-deletion implementation for
        // Erasure requests - deletion happens manually, off-platform. Without this gate, a
        // status of Fulfilled was assertable from the same generic dropdown used for
        // Access/Correction/Restriction, letting the compliance record claim data was erased
        // when nothing had actually been deleted. Untested until now.
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "grant"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        var withoutConfirmation = async () => await fixture.Service.CompleteRequestAsync(
            request.Id, PrivacyRequestStatuses.Fulfilled, "Deleted everywhere.", erasureDataDeletionConfirmed: false);
        await withoutConfirmation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*actually been deleted*");

        await fixture.Service.CompleteRequestAsync(
            request.Id, PrivacyRequestStatuses.Fulfilled, "Deleted everywhere.", erasureDataDeletionConfirmed: true);

        var dashboard = await fixture.Service.GetDashboardAsync();
        var view = dashboard.Requests.Single(x => x.Id == request.Id);
        view.Status.Should().Be(PrivacyRequestStatuses.Fulfilled);
        view.CompletedAt.Should().NotBeNull();
        (await fixture.Db.SecurityAuditEvents.CountAsync(x =>
            x.Action == "PrivacyRequestStatusChanged" && x.TargetId == request.Id.ToString())).Should().Be(1);
    }

    [Fact]
    public async Task ErasureRequest_ShouldStillAllowDenialWithoutTheDeletionConfirmation()
    {
        // The confirmation gate is specific to asserting Fulfilled - denying an erasure request
        // (nothing was deleted because the request was refused) must not require it.
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.Service.CreateRequestAsync(new(PrivacyRequestTypes.Erasure, "grant"));
        await fixture.Service.VerifyIdentityAsync(request.Id);

        await fixture.Service.CompleteRequestAsync(
            request.Id, PrivacyRequestStatuses.Denied, "Identity could not be fully verified.");

        var dashboard = await fixture.Service.GetDashboardAsync();
        dashboard.Requests.Single(x => x.Id == request.Id).Status.Should().Be(PrivacyRequestStatuses.Denied);
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
        private Fixture(SqliteConnection connection, ApplicationDbContext db, PrivacyOperationsService service)
        { _connection = connection; Db = db; Service = service; }
        public ApplicationDbContext Db { get; }
        public PrivacyOperationsService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options); await db.Database.EnsureCreatedAsync();
            var audit = new SecurityAuditService(new TestDbContextFactory(connection), new FixedCurrentUserAccessor("grant"), new PassthroughProtector(), TimeProvider.System);
            return new(connection, db, new PrivacyOperationsService(db, new FixedCurrentUserAccessor("grant"), audit, TimeProvider.System));
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
}
