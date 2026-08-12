using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Mobile;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Part 4.8: server-side readiness for mobile push approvals (device registration + a
// poll-based pending-approvals list) - no real push delivery, see NoOpPushNotificationSender.
public sealed class MobilePushTests
{
    [Fact]
    public async Task RegisterDeviceAsync_ShouldUpsertByUsernameAndToken_NotAccumulateDuplicates()
    {
        await using var db = await CreateDbAsync();
        var service = new MobilePushRegistrationService(db, TimeProvider.System);

        var first = await service.RegisterDeviceAsync("grant", MobileDevicePlatforms.Ios, "token-abc", "iPhone");
        var second = await service.RegisterDeviceAsync("grant", MobileDevicePlatforms.Ios, "token-abc", "iPhone (renamed)");

        first.Id.Should().Be(second.Id);
        var devices = await service.ListDevicesForUserAsync("grant");
        devices.Should().ContainSingle();
        devices[0].DeviceName.Should().Be("iPhone (renamed)");
    }

    [Fact]
    public async Task UnregisterDeviceAsync_ShouldRemoveOnlyTheMatchingDevice()
    {
        await using var db = await CreateDbAsync();
        var service = new MobilePushRegistrationService(db, TimeProvider.System);
        await service.RegisterDeviceAsync("grant", MobileDevicePlatforms.Ios, "token-a", "Phone A");
        await service.RegisterDeviceAsync("grant", MobileDevicePlatforms.Android, "token-b", "Phone B");

        await service.UnregisterDeviceAsync("grant", "token-a");

        var devices = await service.ListDevicesForUserAsync("grant");
        devices.Should().ContainSingle(item => item.Platform == MobileDevicePlatforms.Android);
    }

    [Fact]
    public async Task RegisterDeviceAsync_ShouldRejectAnUnknownPlatform()
    {
        await using var db = await CreateDbAsync();
        var service = new MobilePushRegistrationService(db, TimeProvider.System);

        var act = () => service.RegisterDeviceAsync("grant", "blackberry", "token", "Old phone");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListPendingApprovalsAsync_ShouldReturnOnlyWaitingApprovalNodes()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);

        var workflow = await workflowService.CreateAsync("Needs approval");
        var approvalNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Approve spend", TypeKey = "core.approval", PositionX = 350, PositionY = 100,
            ParametersJson = "{\"message\":\"Approve?\",\"timeoutHours\":0}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", approvalNode.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");
        var execution = await executionService.ExecuteAsync(workflow.Id);
        execution.Status.Should().Be(AutomationExecutionStatuses.Waiting);

        var approvalService = new MobileApprovalService(db);
        var pending = await approvalService.ListPendingApprovalsAsync();

        pending.Should().ContainSingle(item => item.ExecutionId == execution.Id && item.WorkflowName == "Needs approval" && item.NodeName == "Approve spend");

        await executionService.ResolveApprovalAsync(execution.Id, approved: true);
        (await approvalService.ListPendingApprovalsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NoOpPushNotificationSender_ShouldCompleteWithoutThrowing()
    {
        var sender = new NoOpPushNotificationSender(Microsoft.Extensions.Logging.Abstractions.NullLogger<NoOpPushNotificationSender>.Instance);
        await sender.SendAsync("grant", "Approval needed", "A workflow is waiting on you.");
    }

    private sealed class FakeHttpClient : GwsBusinessSuite.Application.Automation.IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSecretProtector : GwsBusinessSuite.Application.Abstractions.ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext))}";
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[11..]));
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
