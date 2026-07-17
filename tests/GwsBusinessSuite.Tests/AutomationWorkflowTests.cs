using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        var triggers = new AutomationTriggerService(db, workflowService, executions, credentials, TimeProvider.System);
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
    public async Task ResumeDueWaitsAsync_ShouldRecoverAnOrphanedRunningExecution()
    {
        await using var db = await CreateDbAsync();
        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var timeProvider = new FakeTimeProvider();
        var workflowService = new AutomationWorkflowService(db, registry, timeProvider);
        var credentials = new AutomationCredentialService(db, new FakeSecretProtector(), timeProvider);
        var executionService = new AutomationExecutionService(db, workflowService, registry, credentials, timeProvider);
        var triggerService = new AutomationTriggerService(db, workflowService, executionService, credentials, timeProvider);
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

    private sealed class FakeHttpClient : IAutomationHttpClient
    {
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new AutomationHttpResponse(200, "{}", new Dictionary<string, string>());
        }
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
}
