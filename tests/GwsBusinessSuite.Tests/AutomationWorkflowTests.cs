using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class AutomationWorkflowTests
{
    [Fact]
    public async Task CreateAndPublish_ShouldStoreImmutableGraphVersion()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var service = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var workflow = await service.CreateAsync("Customer follow-up");
        var trigger = workflow.Nodes.Single();
        var setNode = await service.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Prepare message",
            TypeKey = "core.set",
            PositionX = 400,
            PositionY = 180,
            ParametersJson = "{\"values\":{\"message\":\"Hello\"}}"
        });
        await service.AddConnectionAsync(workflow.Id, trigger.Id, "main", setNode.Id);

        var version = await service.PublishAsync(workflow.Id, "Initial version");
        await service.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Id = setNode.Id,
            Name = setNode.Name,
            TypeKey = setNode.TypeKey,
            PositionX = setNode.PositionX,
            PositionY = setNode.PositionY,
            ParametersJson = "{\"values\":{\"message\":\"Changed draft\"}}"
        });
        var snapshot = await service.GetPublishedSnapshotAsync(workflow.Id);

        version.Should().Be(1);
        snapshot.Should().NotBeNull();
        snapshot!.Version.Should().Be(1);
        snapshot.Nodes.Single(node => node.Id == setNode.Id).ParametersJson.Should().Contain("Hello");
        snapshot.Nodes.Single(node => node.Id == setNode.Id).ParametersJson.Should().NotContain("Changed draft");
    }

    [Fact]
    public async Task AddConnection_ShouldRejectCycles()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var service = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var workflow = await service.CreateAsync("Cycle protection");
        var first = await service.SaveNodeAsync(workflow.Id, NewSetNode("First", 350));
        var second = await service.SaveNodeAsync(workflow.Id, NewSetNode("Second", 600));
        await service.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", first.Id);
        await service.AddConnectionAsync(workflow.Id, first.Id, "main", second.Id);

        var act = () => service.AddConnectionAsync(workflow.Id, second.Id, "main", first.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cycle*");
        (await db.AutomationConnections.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMapInputAndPersistPerNodeEvidence()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentialService = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentialService, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Expression test");
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Map customer",
            TypeKey = "core.set",
            PositionX = 400,
            PositionY = 180,
            ParametersJson = "{\"values\":{\"email\":\"{{ $json.customer.email }}\",\"state\":\"ready\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", setNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Executable version");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{\"customer\":{\"email\":\"grant@example.com\"}}");

        execution.Status.Should().Be("Succeeded");
        execution.OutputJson.Should().Contain("grant@example.com");
        execution.OutputJson.Should().Contain("ready");
        execution.Nodes.Should().HaveCount(2);
        execution.Nodes.Should().OnlyContain(node => node.Status == "Succeeded");
    }

    [Fact]
    public async Task CredentialService_ShouldPersistProtectedDataAndReturnDecryptedJson()
    {
        await using var db = await CreateDbAsync();
        var service = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);

        var id = await service.SaveAsync(null, "API headers", "httpHeader", "{\"headers\":{\"Authorization\":\"Bearer secret\"}}");
        var row = await db.AutomationCredentials.AsNoTracking().SingleAsync();
        var decrypted = await service.GetDecryptedDataAsync(id);

        row.ProtectedData.Should().StartWith("protected::");
        row.ProtectedData.Should().NotContain("Bearer secret");
        decrypted.Should().Contain("Bearer secret");
    }

    [Fact]
    public async Task WebhookTrigger_ShouldRunOnlyAfterPublishedWorkflowIsActive()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, NullLogger<AutomationTriggerService>.Instance);
        var workflow = await workflowService.CreateAsync("Public webhook");
        var webhook = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Incoming order",
            TypeKey = "core.webhookTrigger",
            PositionX = 120,
            PositionY = 420,
            ParametersJson = "{\"path\":\"incoming-order\"}"
        });
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Mark received",
            TypeKey = "core.set",
            PositionX = 400,
            PositionY = 420,
            ParametersJson = "{\"values\":{\"received\":\"yes\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, webhook.Id, "main", setNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Webhook version");

        (await triggers.TriggerWebhookAsync("incoming-order", "{}", null)).Should().BeNull();
        await workflowService.SetActiveAsync(workflow.Id, true);
        var execution = await triggers.TriggerWebhookAsync("incoming-order", "{\"orderId\":42}", null);

        execution.Should().NotBeNull();
        execution!.Status.Should().Be("Succeeded");
        execution.OutputJson.Should().Contain("received");
        execution.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public async Task WebhookTrigger_ShouldWarnWhenFiredWithNoSecretCredentialAttached()
    {
        // Regression guard: echoing the full execution output back to the anonymous caller is
        // intentional (n8n-style), but an author who forgets to attach a secret credential
        // previously got no signal their workflow was running wide open. This doesn't change
        // that it still runs - only that it's now discoverable.
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var recordingLogger = new RecordingLogger<AutomationTriggerService>();
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, recordingLogger);
        var workflow = await workflowService.CreateAsync("Open webhook");
        var webhook = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Incoming order",
            TypeKey = "core.webhookTrigger",
            PositionX = 120,
            PositionY = 420,
            ParametersJson = "{\"path\":\"open-order\"}"
        });
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Mark received",
            TypeKey = "core.set",
            PositionX = 400,
            PositionY = 420,
            ParametersJson = "{\"values\":{\"received\":\"yes\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, webhook.Id, "main", setNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Webhook version");
        await workflowService.SetActiveAsync(workflow.Id, true);

        await triggers.TriggerWebhookAsync("open-order", "{}", null);

        recordingLogger.Warnings.Should().ContainSingle(w => w.Contains("open-order") && w.Contains("no secret"));
    }

    [Fact]
    public async Task PublishAsync_ShouldSyncTriggerWikiDatabaseIdFromAnEnabledDatabaseTriggerNode()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var wikiDatabaseId = Guid.NewGuid();
        var workflow = await workflowService.CreateAsync("Row watcher");
        var dbTrigger = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Row changed",
            TypeKey = "database.rowChangedTrigger",
            PositionX = 120,
            PositionY = 420,
            ParametersJson = $"{{\"wikiDatabaseId\":\"{wikiDatabaseId}\"}}"
        });
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, NewSetNode("Note", 400));
        await workflowService.AddConnectionAsync(workflow.Id, dbTrigger.Id, "main", setNode.Id);

        await workflowService.PublishAsync(workflow.Id, "v1");
        var afterPublish = await workflowService.GetAsync(workflow.Id);
        (await db.AutomationWorkflows.AsNoTracking().SingleAsync(item => item.Id == workflow.Id)).TriggerWikiDatabaseId
            .Should().Be(wikiDatabaseId);

        // Disabling the trigger node and republishing must clear the synced id, not leave it
        // stale - a workflow with no enabled database trigger node has no subscription.
        await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Id = dbTrigger.Id,
            Name = dbTrigger.Name,
            TypeKey = dbTrigger.TypeKey,
            PositionX = dbTrigger.PositionX,
            PositionY = dbTrigger.PositionY,
            ParametersJson = dbTrigger.ParametersJson,
            IsDisabled = true
        });
        await workflowService.PublishAsync(workflow.Id, "v2");
        (await db.AutomationWorkflows.AsNoTracking().SingleAsync(item => item.Id == workflow.Id)).TriggerWikiDatabaseId
            .Should().BeNull();
        afterPublish.Should().NotBeNull();
    }

    [Fact]
    public async Task DatabaseRowChangedTrigger_ShouldRunOnlyActiveWorkflowsSubscribedToThatDatabase()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executions = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System, NullLogger<AutomationTriggerService>.Instance);

        var watchedDatabaseId = Guid.NewGuid();
        var otherDatabaseId = Guid.NewGuid();

        async Task<Guid> CreateSubscribedWorkflowAsync(string name, Guid wikiDatabaseId)
        {
            var workflow = await workflowService.CreateAsync(name);
            var trigger = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
            {
                Name = "Row changed",
                TypeKey = "database.rowChangedTrigger",
                PositionX = 120,
                PositionY = 420,
                ParametersJson = $"{{\"wikiDatabaseId\":\"{wikiDatabaseId}\"}}"
            });
            var setNode = await workflowService.SaveNodeAsync(workflow.Id, NewSetNode("Note", 400));
            await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", setNode.Id);
            await workflowService.PublishAsync(workflow.Id, "v1");
            return workflow.Id;
        }

        var matchingActive = await CreateSubscribedWorkflowAsync("Matching + active", watchedDatabaseId);
        var matchingInactive = await CreateSubscribedWorkflowAsync("Matching + inactive", watchedDatabaseId);
        var otherDatabaseActive = await CreateSubscribedWorkflowAsync("Different database", otherDatabaseId);
        await workflowService.SetActiveAsync(matchingActive, true);
        await workflowService.SetActiveAsync(otherDatabaseActive, true);
        // matchingInactive stays Inactive.

        var triggeredCount = await triggers.TriggerDatabaseRowChangedAsync(watchedDatabaseId, "{\"rowId\":\"" + Guid.NewGuid() + "\"}");

        triggeredCount.Should().Be(1, "only the active workflow subscribed to that exact database should run");
        var matchingExecutions = await db.AutomationExecutions.AsNoTracking().Where(item => item.WorkflowId == matchingActive).ToListAsync();
        matchingExecutions.Should().ContainSingle();
        matchingExecutions[0].Mode.Should().Be(AutomationExecutionModes.DatabaseTrigger);
        (await db.AutomationExecutions.AsNoTracking().AnyAsync(item => item.WorkflowId == matchingInactive)).Should().BeFalse();
        (await db.AutomationExecutions.AsNoTracking().AnyAsync(item => item.WorkflowId == otherDatabaseActive)).Should().BeFalse();
    }

    [Fact]
    public async Task SplitOut_ShouldFanOutEachItemToDownstreamNodes()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Item fan-out");
        var split = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Each customer",
            TypeKey = "core.splitOut",
            PositionX = 350,
            PositionY = 180,
            ParametersJson = "{\"field\":\"customers\"}"
        });
        var template = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Greeting",
            TypeKey = "core.template",
            PositionX = 600,
            PositionY = 180,
            ParametersJson = "{\"outputField\":\"message\",\"template\":\"Hello {{ $json.name }}\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", split.Id);
        await workflowService.AddConnectionAsync(workflow.Id, split.Id, "main", template.Id);
        await workflowService.PublishAsync(workflow.Id, "Fan-out version");

        var execution = await executionService.ExecuteAsync(workflow.Id,
            "{\"customers\":[{\"name\":\"Ada\"},{\"name\":\"Grace\"},{\"name\":\"Katherine\"}]}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        execution.Nodes.Count(node => node.NodeTypeKey == "core.template").Should().Be(3);
        execution.OutputJson.Should().Contain("Hello Katherine");
    }

    [Fact]
    public async Task SplitOut_ShouldFailRatherThanFanOutBeyondTheSafetyCap()
    {
        // Regression guard for a real finding: unlike core.batch/core.limit (which already
        // clamp their size parameters), splitOut had no ceiling at all - each output item
        // becomes its own queued execution step, and every step re-serialized the entire
        // remaining frontier to the DB, an O(n^2) blowup for a large array. Failing loudly here
        // (rather than silently truncating, which would quietly drop rows from a "process every
        // item" workflow) is the fix.
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Oversized fan-out");
        var split = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Each row",
            TypeKey = "core.splitOut",
            PositionX = 350,
            PositionY = 180,
            ParametersJson = "{\"field\":\"rows\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", split.Id);
        await workflowService.PublishAsync(workflow.Id, "Oversized version");

        var oversizedArray = string.Join(',', Enumerable.Range(0, 2_001).Select(i => $"{{\"n\":{i}}}"));
        var execution = await executionService.ExecuteAsync(workflow.Id, $"{{\"rows\":[{oversizedArray}]}}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        execution.ErrorMessage.Should().Contain("2001").And.Contain("core.batch");
    }

    [Fact]
    public async Task DataNodes_ShouldSortDeduplicateAndLimitArrayValues()
    {
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var input = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new { id = 2, name = "Beta" },
                new { id = 1, name = "Alpha" },
                new { id = 2, name = "Duplicate" },
                new { id = 3, name = "Gamma" }
            }
        });

        var distinct = await registry.ExecuteAsync(Node("core.removeDuplicates", "{\"field\":\"items\",\"compareBy\":\"id\"}"), input, null);
        var sorted = await registry.ExecuteAsync(Node("core.sort", "{\"field\":\"items\",\"sortBy\":\"id\",\"direction\":\"descending\"}"), distinct.Outputs["main"].Single(), null);
        var limited = await registry.ExecuteAsync(Node("core.limit", "{\"field\":\"items\",\"maxItems\":2,\"keep\":\"first\"}"), sorted.Outputs["main"].Single(), null);

        using var document = System.Text.Json.JsonDocument.Parse(limited.DisplayOutputJson);
        var items = document.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        items[0].GetProperty("id").GetInt32().Should().Be(3);
        items[1].GetProperty("id").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task StopAndError_ShouldFailExecutionWithConfiguredMessage()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Guarded workflow");
        var stop = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Reject order",
            TypeKey = "core.stopError",
            PositionX = 400,
            PositionY = 180,
            ParametersJson = "{\"message\":\"Order {{ $json.orderId }} was rejected\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", stop.Id);
        await workflowService.PublishAsync(workflow.Id, "Guard version");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{\"orderId\":42}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        execution.ErrorMessage.Should().Be("Order 42 was rejected");
    }

    [Fact]
    public async Task Merge_ShouldWaitForAndCombineLabeledInputs()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Merge branches");
        var trigger = workflow.Nodes.Single();
        var customer = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Customer branch", TypeKey = "core.set", PositionX = 350, PositionY = 100,
            ParametersJson = "{\"values\":{\"branch\":\"customer\"}}"
        });
        var order = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Order branch", TypeKey = "core.set", PositionX = 350, PositionY = 300,
            ParametersJson = "{\"values\":{\"branch\":\"order\"}}"
        });
        var merge = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Join data", TypeKey = "core.merge", PositionX = 650, PositionY = 200, ParametersJson = "{}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", customer.Id);
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", order.Id);
        await workflowService.AddConnectionAsync(workflow.Id, customer.Id, "main", merge.Id, "customer");
        await workflowService.AddConnectionAsync(workflow.Id, order.Id, "main", merge.Id, "order");
        await workflowService.PublishAsync(workflow.Id, "Merge version");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{\"id\":7}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        execution.OutputJson.Should().Contain("customer");
        execution.OutputJson.Should().Contain("order");
        execution.Nodes.Count(node => node.NodeTypeKey == "core.merge").Should().Be(1);
    }

    [Fact]
    public async Task Wait_ShouldPauseThenResumeAfterDurationElapses()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var timeProvider = new FakeTimeProvider();
        var workflowService = new AutomationWorkflowService(db, registry, timeProvider);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), timeProvider);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, timeProvider);
        var workflow = await workflowService.CreateAsync("Wait then greet");
        var trigger = workflow.Nodes.Single();
        var wait = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Pause", TypeKey = "core.wait", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"mode\":\"duration\",\"durationMs\":60000}"
        });
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "After wait", TypeKey = "core.set", PositionX = 600, PositionY = 180,
            ParametersJson = "{\"values\":{\"resumed\":true}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", wait.Id);
        await workflowService.AddConnectionAsync(workflow.Id, wait.Id, "main", setNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Wait version");

        var paused = await executionService.ExecuteAsync(workflow.Id, "{\"id\":1}");

        paused.Status.Should().Be(AutomationExecutionStatuses.Waiting);
        paused.Wait.Should().NotBeNull();
        paused.Wait!.WaitingNodeTypeKey.Should().Be("core.wait");
        paused.Wait.ResumeAt.Should().NotBeNull();
        paused.Nodes.Should().NotContain(node => node.NodeTypeKey == "core.set");

        timeProvider.UtcNow = paused.Wait.ResumeAt!.Value.AddSeconds(1);
        var resumed = await executionService.ResumeAsync(paused.Id);

        resumed.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        resumed.Nodes.Should().Contain(node => node.NodeTypeKey == "core.set");
        resumed.OutputJson.Should().Contain("resumed");
    }

    [Fact]
    public async Task Approval_ShouldRouteToApprovedOutputWhenApproved()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Approval approved path");
        var trigger = workflow.Nodes.Single();
        var approval = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Manager approval", TypeKey = "core.approval", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"message\":\"Approve?\"}"
        });
        var approvedNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Approved path", TypeKey = "core.set", PositionX = 600, PositionY = 100,
            ParametersJson = "{\"values\":{\"outcome\":\"approved\"}}"
        });
        var rejectedNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Rejected path", TypeKey = "core.set", PositionX = 600, PositionY = 260,
            ParametersJson = "{\"values\":{\"outcome\":\"rejected\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", approval.Id);
        await workflowService.AddConnectionAsync(workflow.Id, approval.Id, "approved", approvedNode.Id);
        await workflowService.AddConnectionAsync(workflow.Id, approval.Id, "rejected", rejectedNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Approval version");

        var paused = await executionService.ExecuteAsync(workflow.Id, "{\"id\":1}");
        paused.Status.Should().Be(AutomationExecutionStatuses.Waiting);
        paused.Wait!.WaitingNodeTypeKey.Should().Be("core.approval");

        var resumed = await executionService.ResolveApprovalAsync(paused.Id, approved: true);

        resumed.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        resumed.OutputJson.Should().Contain("approved");
        resumed.Nodes.Should().NotContain(node => node.NodeName == "Rejected path");
    }

    [Fact]
    public async Task Approval_ShouldRouteToRejectedOutputWhenRejected()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Approval rejected path");
        var trigger = workflow.Nodes.Single();
        var approval = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Manager approval", TypeKey = "core.approval", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"message\":\"Approve?\"}"
        });
        var approvedNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Approved path", TypeKey = "core.set", PositionX = 600, PositionY = 100,
            ParametersJson = "{\"values\":{\"outcome\":\"approved\"}}"
        });
        var rejectedNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Rejected path", TypeKey = "core.set", PositionX = 600, PositionY = 260,
            ParametersJson = "{\"values\":{\"outcome\":\"rejected\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", approval.Id);
        await workflowService.AddConnectionAsync(workflow.Id, approval.Id, "approved", approvedNode.Id);
        await workflowService.AddConnectionAsync(workflow.Id, approval.Id, "rejected", rejectedNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Approval version");

        var paused = await executionService.ExecuteAsync(workflow.Id, "{\"id\":1}");
        var resumed = await executionService.ResolveApprovalAsync(paused.Id, approved: false);

        resumed.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        resumed.OutputJson.Should().Contain("rejected");
        resumed.Nodes.Should().NotContain(node => node.NodeName == "Approved path");
    }

    [Fact]
    public async Task Cancel_ShouldStopAPausedExecutionAndBlockFurtherResume()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Cancelable wait");
        var trigger = workflow.Nodes.Single();
        var wait = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Pause", TypeKey = "core.wait", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"mode\":\"duration\",\"durationMs\":3600000}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", wait.Id);
        await workflowService.PublishAsync(workflow.Id, "Cancel version");

        var paused = await executionService.ExecuteAsync(workflow.Id, "{}");
        paused.Status.Should().Be(AutomationExecutionStatuses.Waiting);

        var canceled = await executionService.CancelAsync(paused.Id);

        canceled.Status.Should().Be(AutomationExecutionStatuses.Canceled);
        var act = () => executionService.ResumeAsync(paused.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NodeTimeout_ShouldFailEachAttemptAndStopTheExecution()
    {
        await using var db = await CreateDbAsync();
        var httpClient = new FakeHttpClient { Delay = TimeSpan.FromMilliseconds(300) };
        var registry = new AutomationNodeRegistry(httpClient);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Slow call");
        var trigger = workflow.Nodes.Single();
        var httpNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Slow request", TypeKey = "core.httpRequest", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"method\":\"GET\",\"url\":\"https://example.com\"}",
            RetryOnFail = true, MaxTries = 2, WaitBetweenTriesMs = 0, TimeoutMs = 100
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", httpNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Timeout version");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        var httpAttempts = execution.Nodes.Where(node => node.NodeTypeKey == "core.httpRequest").ToList();
        httpAttempts.Should().HaveCount(2);
        httpAttempts.Should().OnlyContain(node => node.ErrorMessage.Contains("timed out"));
    }

    [Fact]
    public async Task HttpNode_ShouldRedactCredentialValuesFromStoredEvidence_ButNotFromLiveOutput()
    {
        // Regression guard for a real finding: node evidence (AutomationNodeExecution
        // .OutputJson) stores the HTTP node's response verbatim, at rest unencrypted, visible
        // in the Automation UI's execution history. An endpoint that reflects request headers
        // back (an echo/debug endpoint, or just a misconfigured API) previously leaked a
        // decrypted credential straight into that plaintext history, undoing the point of
        // encrypting it at rest at all. The fix redacts credential values from what gets
        // stored as evidence, but must NOT redact them from the live Outputs a downstream node
        // actually consumes (execution.OutputJson here) - a legitimate workflow could need an
        // unredacted value from the response, e.g. a refreshed token a later node re-uses.
        await using var db = await CreateDbAsync();
        var httpClient = new EchoingHttpClient();
        var registry = new AutomationNodeRegistry(httpClient);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var credentialId = await credentials.SaveAsync(
            null, "Echo API", "generic",
            "{\"headers\":{\"Authorization\":\"Bearer super-secret-token\"}}");
        var workflow = await workflowService.CreateAsync("Echo call");
        var trigger = workflow.Nodes.Single();
        var httpNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Call echo endpoint", TypeKey = "core.httpRequest", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"method\":\"GET\",\"url\":\"https://example.com/echo\"}",
            CredentialId = credentialId
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", httpNode.Id);
        await workflowService.PublishAsync(workflow.Id, "Echo version");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        var storedEvidence = execution.Nodes.Single(node => node.NodeTypeKey == "core.httpRequest").OutputJson;
        storedEvidence.Should().NotContain("super-secret-token");
        storedEvidence.Should().Contain("[redacted]");
        execution.OutputJson.Should().Contain("super-secret-token",
            "downstream nodes must still see the real response value - only stored evidence is redacted");
    }

    [Fact]
    public async Task ResumeDueWaitsAsync_ShouldRecoverAnOrphanedRunningExecution()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var timeProvider = new FakeTimeProvider();
        var workflowService = new AutomationWorkflowService(db, registry, timeProvider);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), timeProvider);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, timeProvider);
        var triggerService = new AutomationTriggerService(db, workflowService, executionService, credentials, timeProvider, NullLogger<AutomationTriggerService>.Instance);
        var workflow = await workflowService.CreateAsync("Orphan recovery");
        var trigger = workflow.Nodes.Single();
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Finish", TypeKey = "core.set", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"values\":{\"done\":true}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", setNode.Id);
        var version = await workflowService.PublishAsync(workflow.Id, "Orphan version");

        // Simulate a process crash mid-run: an execution row left in Running status with a stale
        // heartbeat and a checkpointed frontier still pointing at the un-run Set node - the same
        // shape AutomationExecutionService's internal frontier serializer produces.
        var frontierJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Queue = new[] { new { NodeId = setNode.Id, Input = System.Text.Json.JsonSerializer.SerializeToElement(new { }), TargetInput = "main" } },
            MergeBuffers = Array.Empty<object>(),
            LastOutput = System.Text.Json.JsonSerializer.SerializeToElement(new { })
        });
        var staleExecution = new AutomationExecution
        {
            WorkflowId = workflow.Id,
            WorkflowVersion = version,
            Mode = AutomationExecutionModes.Manual,
            Status = AutomationExecutionStatuses.Running,
            InputJson = "{}",
            StartedAt = timeProvider.GetUtcNow(),
            StartedAtUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            HeartbeatAtUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            PendingStateJson = frontierJson,
            CreatedBy = "test"
        };
        db.AutomationExecutions.Add(staleExecution);
        await db.SaveChangesAsync();

        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(20);
        var resumedCount = await triggerService.ResumeDueWaitsAsync();

        resumedCount.Should().Be(1);
        var finished = await workflowService.GetExecutionAsync(staleExecution.Id);
        finished!.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        finished.Nodes.Should().Contain(node => node.NodeTypeKey == "core.set");
    }

    [Fact]
    public async Task ResumeDueWaitsAsync_ShouldWarnWhenReplayingANonIdempotentNode()
    {
        // Regression guard for a real finding: orphan-recovery resumes a "Running" execution
        // from its last checkpointed frontier, which can point at a node that already ran once
        // for real before the crash (see AutomationExecutionService.RunLoopAsync's checkpoint-
        // cadence comment). Replaying a pure data node like core.set is harmless; replaying a
        // real side effect (HTTP call, DB write - tagged IsIdempotent: false on their
        // AutomationNodeDefinition) is not. This asserts the warning fires for the latter,
        // distinguishing it from the harmless case the existing orphan-recovery test above
        // already covers with a core.set node.
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var timeProvider = new FakeTimeProvider();
        var workflowService = new AutomationWorkflowService(db, registry, timeProvider);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), timeProvider);
        var recordingLogger = new RecordingLogger<AutomationExecutionService>();
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, timeProvider, recordingLogger);
        var triggerService = new AutomationTriggerService(db, workflowService, executionService, credentials, timeProvider, NullLogger<AutomationTriggerService>.Instance);
        var workflow = await workflowService.CreateAsync("Orphan replay of a real side effect");
        var trigger = workflow.Nodes.Single();
        var writeNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Write row property", TypeKey = "database.setRowProperty", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"wikiDatabaseId\":\"" + Guid.NewGuid() + "\",\"rowId\":\"" + Guid.NewGuid() + "\",\"propertyId\":\"" + Guid.NewGuid() + "\",\"value\":\"x\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", writeNode.Id);
        var version = await workflowService.PublishAsync(workflow.Id, "Orphan replay version");

        var frontierJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Queue = new[] { new { NodeId = writeNode.Id, Input = System.Text.Json.JsonSerializer.SerializeToElement(new { }), TargetInput = "main" } },
            MergeBuffers = Array.Empty<object>(),
            LastOutput = System.Text.Json.JsonSerializer.SerializeToElement(new { })
        });
        var staleExecution = new AutomationExecution
        {
            WorkflowId = workflow.Id,
            WorkflowVersion = version,
            Mode = AutomationExecutionModes.Manual,
            Status = AutomationExecutionStatuses.Running,
            InputJson = "{}",
            StartedAt = timeProvider.GetUtcNow(),
            StartedAtUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            HeartbeatAtUnixSeconds = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            PendingStateJson = frontierJson,
            CreatedBy = "test"
        };
        db.AutomationExecutions.Add(staleExecution);
        await db.SaveChangesAsync();

        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(20);
        await triggerService.ResumeDueWaitsAsync();

        recordingLogger.Warnings.Should().ContainSingle(warning =>
            warning.Contains("Write row property") && warning.Contains(staleExecution.Id.ToString()));
    }

    [Fact]
    public async Task Resume_ShouldUseTheWorkflowVersionTheExecutionStartedOn()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var timeProvider = new FakeTimeProvider();
        var workflowService = new AutomationWorkflowService(db, registry, timeProvider);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), timeProvider);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, timeProvider);
        var workflow = await workflowService.CreateAsync("Version pinned wait");
        var trigger = workflow.Nodes.Single();
        var wait = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Pause", TypeKey = "core.wait", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"mode\":\"duration\",\"durationMs\":60000}"
        });
        var v1Node = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Version 1 path", TypeKey = "core.set", PositionX = 600, PositionY = 180,
            ParametersJson = "{\"values\":{\"version\":\"v1\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", wait.Id);
        await workflowService.AddConnectionAsync(workflow.Id, wait.Id, "main", v1Node.Id);
        await workflowService.PublishAsync(workflow.Id, "Version 1");

        var paused = await executionService.ExecuteAsync(workflow.Id, "{}");
        paused.Status.Should().Be(AutomationExecutionStatuses.Waiting);
        paused.WorkflowVersion.Should().Be(1);

        // Republish a structurally different graph as version 2 while the execution is still paused.
        await workflowService.DeleteNodeAsync(workflow.Id, v1Node.Id);
        var v2Node = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Version 2 path", TypeKey = "core.set", PositionX = 600, PositionY = 260,
            ParametersJson = "{\"values\":{\"version\":\"v2\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, wait.Id, "main", v2Node.Id);
        await workflowService.PublishAsync(workflow.Id, "Version 2");

        timeProvider.UtcNow = timeProvider.UtcNow.AddMinutes(5);
        var resumed = await executionService.ResumeAsync(paused.Id);

        resumed.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        resumed.WorkflowVersion.Should().Be(1);
        resumed.OutputJson.Should().Contain("v1");
        resumed.OutputJson.Should().NotContain("v2");
    }

    [Fact]
    public async Task AiTeacherNodes_ShouldConsultSpecialistsSynthesizeAndSaveOnlyApprovedMemory()
    {
        await using var db = await CreateDbAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(db.Database.GetDbConnection())
            .Options;
        var ollama = new FakeOllamaService();
        var registry = new AutomationNodeRegistry(
            new FakeHttpClient(),
            ollama,
            new FakeAppDbContextFactory(options));
        var input = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            prompt = "Review this Blazor workflow architecture."
        });
        var qwen = await registry.ExecuteAsync(
            Node("ai.modelAdvisor", "{\"model\":\"qwen2.5-coder\",\"role\":\"Review .NET code.\",\"promptPath\":\"prompt\",\"outputField\":\"qwenAdvice\"}"),
            input,
            null);
        var deepSeek = await registry.ExecuteAsync(
            Node("ai.modelAdvisor", "{\"model\":\"deepseek-r1\",\"role\":\"Challenge assumptions.\",\"promptPath\":\"prompt\",\"outputField\":\"deepseekAdvice\"}"),
            input,
            null);
        var merged = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            qwen = qwen.Outputs["main"].Single(),
            deepseek = deepSeek.Outputs["main"].Single()
        });
        var synthesis = await registry.ExecuteAsync(
            Node("ai.sentinelSynthesize", "{\"model\":\"sentinelgpt\",\"promptPath\":\"prompt\",\"answerField\":\"sentinelAnswer\"}"),
            merged,
            null);
        var approved = System.Text.Json.Nodes.JsonNode.Parse(
            synthesis.Outputs["main"].Single().GetRawText())!.AsObject();
        approved["_approval"] = new System.Text.Json.Nodes.JsonObject { ["approved"] = true };
        var saved = await registry.ExecuteAsync(
            Node("ai.saveApprovedLesson", "{\"promptPath\":\"prompt\",\"answerPath\":\"sentinelAnswer\"}"),
            System.Text.Json.JsonSerializer.SerializeToElement(approved),
            null);

        ollama.RequestedModels.Should().Equal("qwen2.5-coder", "deepseek-r1", "sentinelgpt");
        saved.DisplayOutputJson.Should().Contain("\"saved\":true");
        await using var verificationDb = new ApplicationDbContext(options);
        var lesson = await verificationDb.SentinelAiRuns.AsNoTracking().SingleAsync();
        lesson.Status.Should().Be(SentinelAiRunStatuses.Approved);
        lesson.Action.Should().Be("teacherWorkflow");
        lesson.Output.Should().Contain("Sentinel synthesis");
    }

    // --- Part 1: sub-workflows ---

    [Fact]
    public async Task SubWorkflow_ShouldRunChildToCompletionAndWrapItsOutput()
    {
        await using var db = await CreateDbAsync();
        var serviceProvider = new FakeServiceProvider();
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        serviceProvider.Register<IAutomationExecutionService>(executionService);
        serviceProvider.Register<IAutomationWorkflowService>(workflowService);

        var child = await workflowService.CreateAsync("Child workflow");
        var childSet = await workflowService.SaveNodeAsync(child.Id, new AutomationNodeEditor
        {
            Name = "Child output", TypeKey = "core.set", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"values\":{\"childRan\":true}}"
        });
        await workflowService.AddConnectionAsync(child.Id, child.Nodes.Single().Id, "main", childSet.Id);
        await workflowService.PublishAsync(child.Id, "child v1");

        var parent = await workflowService.CreateAsync("Parent workflow");
        var subWorkflowNode = await workflowService.SaveNodeAsync(parent.Id, new AutomationNodeEditor
        {
            Name = "Call child", TypeKey = "automation.subWorkflow", PositionX = 350, PositionY = 180,
            ParametersJson = $"{{\"workflowId\":\"{child.Id}\"}}"
        });
        await workflowService.AddConnectionAsync(parent.Id, parent.Nodes.Single().Id, "main", subWorkflowNode.Id);
        await workflowService.PublishAsync(parent.Id, "parent v1");

        var execution = await executionService.ExecuteAsync(parent.Id);

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        execution.OutputJson.Should().Contain("childRan");
        execution.OutputJson.Should().Contain("subWorkflowId");
        (await db.AutomationExecutions.CountAsync(item => item.WorkflowId == child.Id && item.Mode == AutomationExecutionModes.SubWorkflow))
            .Should().Be(1);
    }

    [Fact]
    public async Task SubWorkflow_ShouldFailTheParentWhenTheChildFails()
    {
        await using var db = await CreateDbAsync();
        var serviceProvider = new FakeServiceProvider();
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        serviceProvider.Register<IAutomationExecutionService>(executionService);
        serviceProvider.Register<IAutomationWorkflowService>(workflowService);

        var child = await workflowService.CreateAsync("Failing child");
        var stopNode = await workflowService.SaveNodeAsync(child.Id, new AutomationNodeEditor
        {
            Name = "Stop", TypeKey = "core.stopError", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"message\":\"child exploded\"}"
        });
        await workflowService.AddConnectionAsync(child.Id, child.Nodes.Single().Id, "main", stopNode.Id);
        await workflowService.PublishAsync(child.Id, "child v1");

        var parent = await workflowService.CreateAsync("Parent of failing child");
        var subWorkflowNode = await workflowService.SaveNodeAsync(parent.Id, new AutomationNodeEditor
        {
            Name = "Call child", TypeKey = "automation.subWorkflow", PositionX = 350, PositionY = 180,
            ParametersJson = $"{{\"workflowId\":\"{child.Id}\"}}"
        });
        await workflowService.AddConnectionAsync(parent.Id, parent.Nodes.Single().Id, "main", subWorkflowNode.Id);
        await workflowService.PublishAsync(parent.Id, "parent v1");

        var execution = await executionService.ExecuteAsync(parent.Id);

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        execution.ErrorMessage.Should().Contain("child exploded");
    }

    [Fact]
    public async Task SubWorkflow_ShouldFailTheParentWhenTheChildPauses()
    {
        await using var db = await CreateDbAsync();
        var serviceProvider = new FakeServiceProvider();
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        serviceProvider.Register<IAutomationExecutionService>(executionService);
        serviceProvider.Register<IAutomationWorkflowService>(workflowService);

        var child = await workflowService.CreateAsync("Pausing child");
        var waitNode = await workflowService.SaveNodeAsync(child.Id, new AutomationNodeEditor
        {
            Name = "Wait", TypeKey = "core.wait", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"mode\":\"duration\",\"durationMs\":60000}"
        });
        await workflowService.AddConnectionAsync(child.Id, child.Nodes.Single().Id, "main", waitNode.Id);
        await workflowService.PublishAsync(child.Id, "child v1");

        var parent = await workflowService.CreateAsync("Parent of pausing child");
        var subWorkflowNode = await workflowService.SaveNodeAsync(parent.Id, new AutomationNodeEditor
        {
            Name = "Call child", TypeKey = "automation.subWorkflow", PositionX = 350, PositionY = 180,
            ParametersJson = $"{{\"workflowId\":\"{child.Id}\"}}"
        });
        await workflowService.AddConnectionAsync(parent.Id, parent.Nodes.Single().Id, "main", subWorkflowNode.Id);
        await workflowService.PublishAsync(parent.Id, "parent v1");

        var execution = await executionService.ExecuteAsync(parent.Id);

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        execution.ErrorMessage.Should().Contain("paused");
    }

    [Fact]
    public async Task SubWorkflow_ShouldRejectASelfReferentialCycle()
    {
        var node = Node("automation.subWorkflow", "{}");
        var loopingNode = node with { ParametersJson = $"{{\"workflowId\":\"{node.Id}\"}}" };
        var registry = new AutomationNodeRegistry(new FakeHttpClient());

        var act = () => registry.ExecuteAsync(
            loopingNode, System.Text.Json.JsonDocument.Parse("{}").RootElement, null, subWorkflowChain: new HashSet<Guid> { loopingNode.Id });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cycle*");
    }

    [Fact]
    public async Task SubWorkflow_ShouldRejectExceedingMaxCallDepth()
    {
        var targetId = Guid.NewGuid();
        var node = Node("automation.subWorkflow", $"{{\"workflowId\":\"{targetId}\"}}");
        var deepChain = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToHashSet();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());

        var act = () => registry.ExecuteAsync(node, System.Text.Json.JsonDocument.Parse("{}").RootElement, null, subWorkflowChain: deepChain);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum sub-workflow call depth*");
    }

    // --- Part 1: retry-from-failed-node ---

    [Fact]
    public async Task RetryFromFailedNodeAsync_ShouldResumeFromTheFailedNodeNotFromScratch()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Retry target");
        var trigger = workflow.Nodes.Single();
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, NewSetNode("Prep", 350));
        var failNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Always fails", TypeKey = "core.stopError", PositionX = 600, PositionY = 180,
            ParametersJson = "{\"message\":\"boom\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", setNode.Id);
        await workflowService.AddConnectionAsync(workflow.Id, setNode.Id, "main", failNode.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id);
        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        execution.Nodes.Should().HaveCount(3); // manual trigger + Prep + the failing node

        var retried = await executionService.RetryFromFailedNodeAsync(execution.Id);

        retried.Mode.Should().Be(AutomationExecutionModes.Retry);
        retried.Status.Should().Be(AutomationExecutionStatuses.Failed);
        retried.Nodes.Should().ContainSingle();
        retried.Nodes.Single().NodeTypeKey.Should().Be("core.stopError");
        var retriedRow = await db.AutomationExecutions.AsNoTracking().SingleAsync(item => item.Id == retried.Id);
        retriedRow.RetryOfExecutionId.Should().Be(execution.Id);
    }

    [Fact]
    public async Task RetryFromFailedNodeAsync_ShouldRejectAnExecutionThatDidNotFail()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Succeeding workflow");
        await workflowService.PublishAsync(workflow.Id, "v1");
        var execution = await executionService.ExecuteAsync(workflow.Id);
        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);

        var act = () => executionService.RetryFromFailedNodeAsync(execution.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not Failed*");
    }

    // --- Part 1: workflow templates ---

    [Fact]
    public async Task Templates_ShouldCaptureDraftAndInstantiateWithFreshIdentities()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var templateService = new AutomationTemplateService(db, workflowService);
        var workflow = await workflowService.CreateAsync("Source workflow", "desc");
        var trigger = workflow.Nodes.Single();
        var setNode = await workflowService.SaveNodeAsync(workflow.Id, NewSetNode("Prep", 400));
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", setNode.Id);

        var templateId = await templateService.CreateFromWorkflowAsync(workflow.Id, "My Template", "template desc", "user");
        var templates = await templateService.ListAsync();
        templates.Should().ContainSingle(t => t.Id == templateId && t.NodeCount == 2);

        var instantiated = await templateService.InstantiateAsync(templateId, "New From Template", "user");

        instantiated.Nodes.Should().HaveCount(2);
        instantiated.Nodes.Should().OnlyContain(node => node.Id != trigger.Id && node.Id != setNode.Id);
        instantiated.Connections.Should().ContainSingle();
        instantiated.Id.Should().NotBe(workflow.Id);
    }

    [Fact]
    public async Task Templates_ShouldRejectDuplicateNames()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var templateService = new AutomationTemplateService(db, workflowService);
        var workflow = await workflowService.CreateAsync("Dup source");
        await templateService.CreateFromWorkflowAsync(workflow.Id, "Dup", "", "user");

        var act = () => templateService.CreateFromWorkflowAsync(workflow.Id, "Dup", "", "user");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task Templates_DeleteTemplateAsync_ShouldRemoveIt()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var templateService = new AutomationTemplateService(db, workflowService);
        var workflow = await workflowService.CreateAsync("Deletable template source");
        var templateId = await templateService.CreateFromWorkflowAsync(workflow.Id, "Deletable", "", "user");

        await templateService.DeleteTemplateAsync(templateId);

        (await templateService.ListAsync()).Should().BeEmpty();
    }

    // --- Part 1: import/export ---

    [Fact]
    public async Task CreateFromGraphAsync_ShouldRebuildWorkflowWithFreshIdsAndNoCredential()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var credentialId = await credentials.SaveAsync(null, "Some cred", "httpHeader", "{\"headers\":{}}");
        var source = await workflowService.CreateAsync("Export source");
        var trigger = source.Nodes.Single();
        var httpNode = await workflowService.SaveNodeAsync(source.Id, new AutomationNodeEditor
        {
            Name = "Call API", TypeKey = "core.httpRequest", PositionX = 400, PositionY = 200,
            ParametersJson = "{\"method\":\"GET\",\"url\":\"https://example.com\"}", CredentialId = credentialId
        });
        await workflowService.AddConnectionAsync(source.Id, trigger.Id, "main", httpNode.Id);
        var reloaded = (await workflowService.GetAsync(source.Id))!;

        var rebuilt = await workflowService.CreateFromGraphAsync("Rebuilt", reloaded.Description, reloaded.Nodes, reloaded.Connections, "user");

        rebuilt.Id.Should().NotBe(source.Id);
        rebuilt.Nodes.Should().HaveCount(2);
        rebuilt.Nodes.Should().OnlyContain(node => node.CredentialId == null);
        rebuilt.Connections.Should().ContainSingle();
    }

    [Fact]
    public async Task CreateFromGraphAsync_ShouldRejectAnUnregisteredNodeType()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var nodes = new List<AutomationNodeView> { new(Guid.NewGuid(), "Bogus", "not.a.real.type", 1, 0, 0, "{}", null, false, false, false, 1, 0, 0, "") };

        var act = () => workflowService.CreateFromGraphAsync("Imported", "", nodes, [], "user");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not registered*");
    }

    // --- Part 1: version diff & rollback ---

    [Fact]
    public async Task DiffAndRollback_ShouldReportChangesAndRestoreDraft()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Versioned workflow");
        var trigger = workflow.Nodes.Single();
        var v1 = await workflowService.PublishAsync(workflow.Id, "v1");

        var extraNode = await workflowService.SaveNodeAsync(workflow.Id, NewSetNode("Extra", 400));
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", extraNode.Id);
        var v2 = await workflowService.PublishAsync(workflow.Id, "v2");

        var diff = await workflowService.DiffVersionsAsync(workflow.Id, v1, v2);
        diff.AddedNodes.Should().ContainSingle(node => node.Id == extraNode.Id);
        diff.RemovedNodes.Should().BeEmpty();
        diff.AddedConnections.Should().ContainSingle();

        var versions = await workflowService.ListVersionsAsync(workflow.Id);
        versions.Select(version => version.VersionNumber).Should().BeEquivalentTo([v1, v2]);

        await workflowService.RollbackToVersionAsync(workflow.Id, v1, "user");

        var current = await workflowService.GetAsync(workflow.Id);
        current!.Nodes.Should().ContainSingle(node => node.Id == trigger.Id);
        current.Nodes.Should().NotContain(node => node.Id == extraNode.Id);
    }

    // --- Part 1: node-reference expression tooling ---

    [Fact]
    public async Task NodeReferenceExpression_ShouldResolveAnEarlierNodesOutputPastAReplacingNode()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Node reference test");
        var trigger = workflow.Nodes.Single();
        var firstNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "First", TypeKey = "core.set", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"values\":{\"greeting\":\"hello\"}}"
        });
        // core.httpRequest replaces the whole item ({statusCode, body, headers}) rather than
        // merging onto it - unlike every other data node, so it's the one node type that can
        // prove $node(...) reaches past the immediate predecessor instead of just re-resolving
        // $json against something that still happens to carry the same field.
        var replacingNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Replace shape", TypeKey = "core.httpRequest", PositionX = 550, PositionY = 180,
            ParametersJson = "{\"method\":\"GET\",\"url\":\"https://example.com\"}"
        });
        var secondNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Second", TypeKey = "core.template", PositionX = 750, PositionY = 180,
            ParametersJson = "{\"outputField\":\"combined\",\"template\":\"{{ $node(\\\"First\\\").json.greeting }} world\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, trigger.Id, "main", firstNode.Id);
        await workflowService.AddConnectionAsync(workflow.Id, firstNode.Id, "main", replacingNode.Id);
        await workflowService.AddConnectionAsync(workflow.Id, replacingNode.Id, "main", secondNode.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id);

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        execution.OutputJson.Should().Contain("hello world");
    }

    [Fact]
    public async Task NodeReferenceExpression_ShouldResolveToEmptyForAnUnknownNodeName()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Missing node reference");
        var templateNode = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Only node", TypeKey = "core.template", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"outputField\":\"combined\",\"template\":\"{{ $node(\\\"Nonexistent\\\").json.x }} world\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", templateNode.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id);

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        execution.OutputJson.Should().Contain("\" world\"");
    }

    // --- Part 1: cross-module action nodes ---

    [Fact]
    public async Task CrmSetDealStage_ShouldCallCrmServiceWithResolvedExpressions()
    {
        await using var db = await CreateDbAsync();
        var crm = new FakeCrmService();
        var serviceProvider = new FakeServiceProvider().Register<ICrmService>(crm);
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("CRM stage move");
        var dealId = Guid.NewGuid();
        var node = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Move stage", TypeKey = "crm.setDealStage", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"dealId\":\"{{ $json.dealId }}\",\"stage\":\"Won\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", node.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id, $"{{\"dealId\":\"{dealId}\"}}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        crm.StagedDeals.Should().ContainSingle(entry => entry.DealId == dealId && entry.Stage == "Won");
    }

    [Fact]
    public async Task CrmSaveContact_ShouldCallCrmServiceWithResolvedExpressions()
    {
        await using var db = await CreateDbAsync();
        var crm = new FakeCrmService();
        var serviceProvider = new FakeServiceProvider().Register<ICrmService>(crm);
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("CRM save contact");
        var node = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Save contact", TypeKey = "crm.saveContact", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"fullName\":\"{{ $json.name }}\",\"email\":\"{{ $json.email }}\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", node.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        crm.SavedContacts.Should().ContainSingle(entry => entry.FullName == "Ada Lovelace" && entry.Email == "ada@example.com");
    }

    [Fact]
    public async Task CmsSavePage_ShouldCallCmsBuilderServiceWithResolvedExpressions()
    {
        await using var db = await CreateDbAsync();
        var cms = new FakeCmsBuilderService();
        var serviceProvider = new FakeServiceProvider().Register<ICmsBuilderService>(cms);
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("CMS save page");
        var siteId = Guid.NewGuid();
        var node = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Save page", TypeKey = "cms.savePage", PositionX = 350, PositionY = 180,
            ParametersJson = $"{{\"siteId\":\"{siteId}\",\"title\":\"{{{{ $json.title }}}}\",\"blocksJson\":\"[]\"}}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", node.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id, "{\"title\":\"Launch announcement\"}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Succeeded);
        cms.SavedPages.Should().ContainSingle(entry => entry.SiteId == siteId && entry.Title == "Launch announcement");
    }

    [Fact]
    public async Task GrowthPublishSocialPost_ShouldThrowWhenThePublishCallFails()
    {
        await using var db = await CreateDbAsync();
        var social = new FakeSocialPublishingService { ShouldSucceed = false };
        var serviceProvider = new FakeServiceProvider().Register<ISocialPublishingService>(social);
        var registry = new AutomationNodeRegistry(new FakeHttpClient(), serviceProvider: serviceProvider);
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), TimeProvider.System);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, TimeProvider.System);
        var workflow = await workflowService.CreateAsync("Growth publish");
        var postId = Guid.NewGuid();
        var node = await workflowService.SaveNodeAsync(workflow.Id, new AutomationNodeEditor
        {
            Name = "Publish", TypeKey = "growth.publishSocialPost", PositionX = 350, PositionY = 180,
            ParametersJson = "{\"postId\":\"{{ $json.postId }}\"}"
        });
        await workflowService.AddConnectionAsync(workflow.Id, workflow.Nodes.Single().Id, "main", node.Id);
        await workflowService.PublishAsync(workflow.Id, "v1");

        var execution = await executionService.ExecuteAsync(workflow.Id, $"{{\"postId\":\"{postId}\"}}");

        execution.Status.Should().Be(AutomationExecutionStatuses.Failed);
        social.PublishedPostIds.Should().Contain(postId);
    }

    private static AutomationNodeEditor NewSetNode(string name, double x) => new()
    {
        Name = name,
        TypeKey = "core.set",
        PositionX = x,
        PositionY = 180,
        ParametersJson = "{\"values\":{}}"
    };

    private static AutomationNodeSnapshot Node(string typeKey, string parametersJson) => new(
        Guid.NewGuid(), typeKey, typeKey, 1, parametersJson, null, false, false, false, 1, 0, 0);

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class FakeHttpClient : IAutomationHttpClient
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new AutomationHttpResponse(200, "{}", new Dictionary<string, string>());
        }
    }

    // Simulates a debug/echo endpoint (or a misconfigured API) reflecting request headers -
    // including whatever credential header the node injected - back in the response body.
    private sealed class EchoingHttpClient : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutomationHttpResponse(
                200, System.Text.Json.JsonSerializer.Serialize(new { receivedHeaders = request.Headers }), new Dictionary<string, string>()));
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected::{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext))}";
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[11..]));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    // Registered lazily after construction (mirrors the real circular-dependency pattern
    // AutomationNodeRegistry's own doc comment describes for IWikiDatabaseService): the
    // registry is built first with an empty provider, then IAutomationExecutionService/
    // IAutomationWorkflowService are added to it once they exist, since GetService is only
    // ever called at node-execution time, not at registry construction time.
    private sealed class FakeServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public FakeServiceProvider Register<TService>(TService instance) where TService : notnull
        {
            _services[typeof(TService)] = instance;
            return this;
        }
        public object? GetService(Type serviceType) => _services.TryGetValue(serviceType, out var instance) ? instance : null;
    }

    private sealed class FakeCrmService : ICrmService
    {
        public List<(Guid DealId, string Stage)> StagedDeals { get; } = [];
        public List<ContactEditorModel> SavedContacts { get; } = [];

        public Task<DealView> SetDealStageAsync(Guid dealId, string stage, CancellationToken cancellationToken = default)
        {
            StagedDeals.Add((dealId, stage));
            return Task.FromResult(new DealView { Id = dealId, ContactId = Guid.NewGuid(), Title = "Deal", Stage = stage, CreatedAt = DateTimeOffset.UtcNow });
        }

        public Task<Contact> SaveContactAsync(ContactEditorModel editor, CancellationToken cancellationToken = default)
        {
            SavedContacts.Add(editor);
            return Task.FromResult(new Contact
            {
                Id = editor.ContactId ?? Guid.NewGuid(), FullName = editor.FullName, Email = editor.Email, Company = editor.Company, Status = editor.Status
            });
        }

        public Task<CrmDashboardData> GetDashboardAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Contact>> ListContactsAsync(bool includeTrashed = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Contact>> ListTrashedContactsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Contact?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task TrashContactAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RestoreContactAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteContactPermanentlyAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ContactActivityView>> ListActivitiesAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ContactActivityView> AddActivityAsync(Guid contactId, string note, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Contact>> ListDueFollowUpsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CountDueFollowUpsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DealView>> ListDealsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DealView>> ListDealsForContactAsync(Guid contactId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DealView> SaveDealAsync(DealEditorModel editor, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteDealAsync(Guid dealId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeCmsBuilderService : ICmsBuilderService
    {
        public List<CmsPageEditorModel> SavedPages { get; } = [];

        public Task<CmsPage> SavePageAsync(CmsPageEditorModel editor, CancellationToken cancellationToken = default)
        {
            SavedPages.Add(editor);
            return Task.FromResult(new CmsPage
            {
                Id = editor.PageId ?? Guid.NewGuid(), SiteId = editor.SiteId ?? Guid.Empty, Title = editor.Title,
                Slug = string.IsNullOrWhiteSpace(editor.Slug) ? "page" : editor.Slug, BlocksJson = editor.BlocksJson
            });
        }

        public Task<IReadOnlyList<CmsSite>> ListSitesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsSite?> GetSiteAsync(Guid siteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsSite?> GetSiteBySlugAsync(string siteSlug, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsSite> SaveSiteAsync(CmsSiteEditorModel editor, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteSiteAsync(Guid siteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CmsPage>> ListPagesAsync(Guid? siteId = null, bool includeTrashed = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CmsPage>> ListTrashedPagesAsync(Guid siteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CmsPageCategory>> ListPageCategoriesAsync(Guid siteId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsPage?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsPage?> GetPageBySlugAsync(Guid siteId, string pageSlug, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsPage?> GetPageByFullPathAsync(Guid siteId, string fullPath, bool includeUnpublished = false, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public string BuildFullPath(CmsPage page, IReadOnlyList<CmsPage> allPagesInSite) => throw new NotImplementedException();
        public Task TrashPageAsync(Guid pageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RestorePageAsync(Guid pageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeletePageAsync(Guid pageId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CmsWorkflowBlueprintSummary>> ListWorkflowBlueprintsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CmsPage> ApplyWorkflowBlueprintAsync(Guid pageId, string blueprintKey, bool replaceExistingBlocks, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeSocialPublishingService : ISocialPublishingService
    {
        public bool ShouldSucceed { get; set; } = true;
        public List<Guid> PublishedPostIds { get; } = [];

        public Task<SocialPublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
        {
            PublishedPostIds.Add(postId);
            return Task.FromResult(ShouldSucceed
                ? new SocialPublishResult(true, "Published")
                : new SocialPublishResult(false, "Provider rejected the post"));
        }

        public Task<IReadOnlyList<SocialAccountView>> GetAccountsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveAccountAsync(SocialAccountInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SocialPostView>> GetPostsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, string>> GenerateVariantsAsync(string topic, string sourceUrl, IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Guid> SaveDraftAsync(string title, string sourceUrl, IReadOnlyCollection<SocialTargetDraft> targets, DateTimeOffset? scheduledFor, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task PublishDueAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SocialPostAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task MarkAllAlertsReadAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeOllamaService : IOllamaService
    {
        public List<string> RequestedModels { get; } = [];

        public Task<string> GenerateAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken ct = default)
        {
            RequestedModels.Add(model);
            var output = model switch
            {
                "qwen2.5-coder" => "Qwen engineering advice",
                "deepseek-r1" => "DeepSeek reasoning advice",
                "sentinelgpt" => "Sentinel synthesis approved by the reviewer",
                _ => "Unknown model"
            };
            return Task.FromResult(output);
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await GenerateAsync(model, systemPrompt, userPrompt, ct);
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(["qwen2.5-coder", "deepseek-r1", "sentinelgpt"]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
