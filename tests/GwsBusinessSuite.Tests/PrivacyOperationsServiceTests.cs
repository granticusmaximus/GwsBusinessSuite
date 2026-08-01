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
