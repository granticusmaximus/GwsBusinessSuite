using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Part 4.9: publish/activate/approval-resolution governance actions record into the same unified
// security audit stream (ISecurityAuditService) already used for login/MFA/privacy events -
// routine execution runs are deliberately not audited here, since AutomationExecution/
// AutomationNodeExecution already durably capture every run's own full evidence trail.
public sealed class AutomationAuditTrailTests
{
    [Fact]
    public async Task PublishAndSetActive_ShouldRecordGovernanceEventsIntoTheAuditStream()
    {
        await using var fixture = await Fixture.CreateAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(fixture.Db, registry, TimeProvider.System, securityAudit: fixture.SecurityAudit);
        var workflow = await workflowService.CreateAsync("Audited workflow");

        await workflowService.PublishAsync(workflow.Id, "v1");
        await workflowService.SetActiveAsync(workflow.Id, true);

        var page = await fixture.SecurityAudit.QueryAsync(new SecurityAuditQuery(Category: SecurityAuditCategories.DataLifecycle));
        page.Events.Should().Contain(item => item.Action == "AutomationWorkflowPublished" && item.TargetId == workflow.Id.ToString() && item.ActorUsername == "grant");
        page.Events.Should().Contain(item => item.Action == "AutomationWorkflowActivated" && item.TargetId == workflow.Id.ToString());
    }

    [Fact]
    public async Task ResolveApprovalAsync_ShouldRecordTheDecisionIntoTheAuditStream()
    {
        await using var fixture = await Fixture.CreateAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(fixture.Db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(fixture.Db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(fixture.Db, workflowService, registry, credentials, TimeProvider.System, securityAudit: fixture.SecurityAudit);

        var workflow = await workflowService.CreateAsync("Approval workflow");
        var approvalNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Approve", TypeKey = "core.approval", PositionX = 350, PositionY = 100,
            ParametersJson = "{\"message\":\"Approve?\",\"timeoutHours\":0}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", approvalNode.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id);
        execution.Status.Should().Be(AutomationExecutionStatuses.Waiting);

        await executionService.ResolveApprovalAsync(execution.Id, approved: true, "looks good");

        var page = await fixture.SecurityAudit.QueryAsync(new SecurityAuditQuery(Category: SecurityAuditCategories.SecurityOperations));
        page.Events.Should().Contain(item => item.Action == "AutomationApprovalResolved" && item.TargetId == execution.Id.ToString());
    }

    [Fact]
    public async Task SavePageAsync_ShouldAuditOnlyCheckpointWorthySaves_NotSilentAutosaves()
    {
        await using var fixture = await Fixture.CreateAsync();
        var wiki = new WikiService(fixture.Db, securityAudit: fixture.SecurityAudit);

        var created = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook" }, "grant");
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = created.Id, Title = "Runbook", ExpectedContentVersion = created.ContentVersion, BlocksJson = "[]"
        }, "grant", createRevisionCheckpoint: false);

        var page = await fixture.SecurityAudit.QueryAsync(new SecurityAuditQuery(Category: SecurityAuditCategories.DataLifecycle));
        page.Events.Should().ContainSingle(item => item.TargetId == created.Id.ToString(), "only the checkpoint save should be audited, not the silent autosave");
        page.Events.Single(item => item.TargetId == created.Id.ToString()).Action.Should().Be("SentinelPageCreated");
    }

    private sealed class FakeHttpClient : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext))}";
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[11..]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private Fixture(SqliteConnection connection, ApplicationDbContext db, SecurityAuditService securityAudit)
        {
            _connection = connection;
            Db = db;
            SecurityAudit = securityAudit;
        }

        public ApplicationDbContext Db { get; }
        public SecurityAuditService SecurityAudit { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var securityAudit = new SecurityAuditService(
                new TestDbContextFactory(connection), new FixedCurrentUserAccessor("grant"), new PassthroughSecretProtector(), TimeProvider.System);
            return new Fixture(connection, db, securityAudit);
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
