using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SecurityAuditServiceTests
{
    [Fact]
    public async Task RecordAndQuery_ShouldBuildValidChain_AndNeverReturnProtectedNetworkValue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstId = await fixture.Service.RecordAsync(new SecurityAuditInput(
            SecurityAuditCategories.Authentication, "PasswordLogin", SecurityAuditOutcomes.Failed,
            SecurityAuditSeverities.Warning, "AppUser", "grant",
            new Dictionary<string, string?> { ["reason"] = "InvalidCredentials" }, "203.0.113.8"));
        await fixture.Service.RecordAsync(new SecurityAuditInput(
            SecurityAuditCategories.Authentication, "MfaChallenge", SecurityAuditOutcomes.Succeeded,
            TargetType: "AppUser", TargetId: "grant"));

        var integrity = await fixture.Service.VerifyIntegrityAsync();
        var page = await fixture.Service.QueryAsync(new SecurityAuditQuery(Category: SecurityAuditCategories.Authentication));

        integrity.IsValid.Should().BeTrue();
        integrity.EventsChecked.Should().Be(2);
        page.TotalCount.Should().Be(2);
        page.Events.Should().Contain(item => item.Id == firstId && item.HasProtectedNetworkContext);
        (await fixture.Db.SecurityAuditEvents.SingleAsync(item => item.Id == firstId))
            .NetworkAddressProtected.Should().Be("protected::203.0.113.8");
    }

    [Fact]
    public async Task VerifyIntegrity_ShouldDetectChangedStoredFields()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = await fixture.Service.RecordAsync(new SecurityAuditInput(
            SecurityAuditCategories.AccountAdministration, "UserCreated", SecurityAuditOutcomes.Succeeded,
            TargetType: "AppUser", TargetId: "user-1"));
        var row = await fixture.Db.SecurityAuditEvents.SingleAsync(item => item.Id == id);
        row.Action = "UserDeleted";
        await fixture.Db.SaveChangesAsync();

        var integrity = await fixture.Service.VerifyIntegrityAsync();

        integrity.IsValid.Should().BeFalse();
        integrity.FirstInvalidEventId.Should().Be(id);
    }

    [Fact]
    public async Task Record_ShouldRejectMetadataKeysThatCouldCarrySecretsOrContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var action = async () => await fixture.Service.RecordAsync(new SecurityAuditInput(
            SecurityAuditCategories.Integration, "ConnectorSaved", SecurityAuditOutcomes.Succeeded,
            Details: new Dictionary<string, string?> { ["accessToken"] = "must-never-be-stored" }));

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*sensitive data*");
        (await fixture.Db.SecurityAuditEvents.CountAsync()).Should().Be(0);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db, SecurityAuditService service)
        {
            _connection = connection;
            Db = db;
            Service = service;
        }

        public ApplicationDbContext Db { get; }
        public SecurityAuditService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var service = new SecurityAuditService(
                new TestDbContextFactory(connection),
                new FixedCurrentUserAccessor("grantwatson"),
                new PassthroughSecretProtector(),
                TimeProvider.System);
            return new Fixture(connection, db, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["protected::".Length..];
    }
}
