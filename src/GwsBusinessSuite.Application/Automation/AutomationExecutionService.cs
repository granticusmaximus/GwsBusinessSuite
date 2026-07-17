using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationExecutionService(
    IAppDbContext db,
    IAutomationWorkflowService workflowService,
    IAutomationNodeRegistry nodeRegistry,
    IAutomationCredentialService credentialService,
    TimeProvider timeProvider) : IAutomationExecutionService
{
    public async Task<AutomationExecutionView> ExecuteAsync(
        Guid workflowId,
        string inputJson = "{}",
        string mode = AutomationExecutionModes.Manual,
        Guid? retryOfExecutionId = null,
        CancellationToken cancellationToken = default)
    {
        JsonElement input;
        try { input = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson).RootElement.Clone(); }
        catch (JsonException ex) { throw new ArgumentException($"Execution input must be valid JSON: {ex.Message}", nameof(inputJson)); }

        var snapshot = await workflowService.GetPublishedSnapshotAsync(workflowId, cancellationToken)
            ?? throw new InvalidOperationException("Publish the workflow before running it.");
        var now = timeProvider.GetUtcNow();
        var execution = new AutomationExecution
        {
            WorkflowId = workflowId,
            WorkflowVersion = snapshot.Version,
            Mode = mode,
            Status = AutomationExecutionStatuses.Running,
            InputJson = input.GetRawText(),
            StartedAt = now,
            StartedAtUnixSeconds = now.ToUnixTimeSeconds(),
            RetryOfExecutionId = retryOfExecutionId,
            CreatedBy = "automation-engine"
        };
        db.AutomationExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var triggerType = mode switch
            {
                AutomationExecutionModes.Webhook => "core.webhookTrigger",
                AutomationExecutionModes.Schedule => "core.scheduleTrigger",
                _ => "core.manualTrigger"
            };
            var startNodes = snapshot.Nodes.Where(node => node.TypeKey == triggerType && !node.IsDisabled).ToList();
            if (startNodes.Count == 0) throw new InvalidOperationException($"The published workflow has no enabled trigger for {mode} execution.");

            var nodesById = snapshot.Nodes.ToDictionary(node => node.Id);
            var outgoing = snapshot.Connections.GroupBy(connection => connection.SourceNodeId).ToDictionary(group => group.Key, group => group.ToList());
            var incomingPorts = snapshot.Connections.GroupBy(connection => connection.TargetNodeId).ToDictionary(
                group => group.Key,
                group => group.Select(connection => connection.TargetInput).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            var mergeBuffers = new Dictionary<Guid, Dictionary<string, Queue<JsonElement>>>();
            var queue = new Queue<(AutomationNodeSnapshot Node, JsonElement Input, string TargetInput)>();
            foreach (var trigger in startNodes) queue.Enqueue((trigger, input.Clone(), "main"));
            var lastOutput = input.Clone();
            var steps = 0;

            while (queue.TryDequeue(out var work))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++steps > 10_000) throw new InvalidOperationException("Execution exceeded the 10,000 node-step safety limit.");
                var nodeInput = work.Input;
                if (work.Node.TypeKey == "core.merge" && !work.Node.IsDisabled)
                {
                    nodeInput = TryBuildMergedInput(work.Node.Id, work.TargetInput, work.Input, incomingPorts, mergeBuffers);
                    if (nodeInput.ValueKind == JsonValueKind.Undefined) continue;
                }
                var result = work.Node.IsDisabled
                    ? SingleItemResult("main", nodeInput)
                    : await ExecuteNodeWithEvidenceAsync(execution, work.Node, nodeInput, cancellationToken);
                var latestItem = result.Outputs.Values.SelectMany(items => items).LastOrDefault();
                if (latestItem.ValueKind != JsonValueKind.Undefined) lastOutput = latestItem.Clone();

                if (!outgoing.TryGetValue(work.Node.Id, out var connections)) continue;
                foreach (var connection in connections)
                {
                    if (!result.Outputs.TryGetValue(connection.SourceOutput, out var outputs)) continue;
                    if (!nodesById.TryGetValue(connection.TargetNodeId, out var target)) continue;
                    foreach (var output in outputs) queue.Enqueue((target, output.Clone(), connection.TargetInput));
                }
            }

            execution.Status = AutomationExecutionStatuses.Succeeded;
            execution.OutputJson = lastOutput.GetRawText();
        }
        catch (OperationCanceledException)
        {
            execution.Status = AutomationExecutionStatuses.Canceled;
            execution.ErrorMessage = "Execution was canceled.";
        }
        catch (Exception ex)
        {
            execution.Status = AutomationExecutionStatuses.Failed;
            execution.ErrorMessage = ex.Message;
        }

        var finishedAt = timeProvider.GetUtcNow();
        execution.FinishedAt = finishedAt;
        execution.FinishedAtUnixSeconds = finishedAt.ToUnixTimeSeconds();
        var workflow = await db.AutomationWorkflows.FirstAsync(item => item.Id == workflowId, CancellationToken.None);
        workflow.LastExecutedAt = finishedAt;
        workflow.UpdatedAt = finishedAt;
        workflow.UpdatedBy = "automation-engine";
        await db.SaveChangesAsync(CancellationToken.None);

        return await LoadExecutionAsync(execution.Id, CancellationToken.None);
    }

    private async Task<AutomationNodeRunResult> ExecuteNodeWithEvidenceAsync(
        AutomationExecution execution,
        AutomationNodeSnapshot node,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var maxAttempts = node.RetryOnFail ? Math.Clamp(node.MaxTries, 1, 10) : 1;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var startedAt = timeProvider.GetUtcNow();
            var evidence = new AutomationNodeExecution
            {
                ExecutionId = execution.Id,
                NodeId = node.Id,
                NodeName = node.Name,
                NodeTypeKey = node.TypeKey,
                Status = AutomationExecutionStatuses.Running,
                Attempt = attempt,
                InputJson = input.GetRawText(),
                StartedAt = startedAt,
                StartedAtUnixSeconds = startedAt.ToUnixTimeSeconds(),
                CreatedBy = "automation-engine"
            };
            db.AutomationNodeExecutions.Add(evidence);
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var credentialJson = node.CredentialId.HasValue
                    ? await credentialService.GetDecryptedDataAsync(node.CredentialId.Value, cancellationToken)
                    : null;
                var result = await nodeRegistry.ExecuteAsync(node, input, credentialJson, cancellationToken);
                var finishedAt = timeProvider.GetUtcNow();
                evidence.Status = AutomationExecutionStatuses.Succeeded;
                evidence.OutputJson = result.DisplayOutputJson;
                evidence.FinishedAt = finishedAt;
                evidence.FinishedAtUnixSeconds = finishedAt.ToUnixTimeSeconds();
                await db.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var finishedAt = timeProvider.GetUtcNow();
                evidence.Status = AutomationExecutionStatuses.Failed;
                evidence.ErrorMessage = ex.Message;
                evidence.FinishedAt = finishedAt;
                evidence.FinishedAtUnixSeconds = finishedAt.ToUnixTimeSeconds();
                await db.SaveChangesAsync(cancellationToken);
                if (attempt < maxAttempts)
                {
                    if (node.WaitBetweenTriesMs > 0) await Task.Delay(node.WaitBetweenTriesMs, cancellationToken);
                    continue;
                }
                if (node.ContinueOnFail)
                {
                    var error = JsonSerializer.SerializeToElement(new { error = ex.Message, node = node.Name });
                    return SingleItemResult("main", error);
                }
                throw;
            }
        }
        throw new InvalidOperationException("Node execution ended without a result.");
    }

    private static AutomationNodeRunResult SingleItemResult(string port, JsonElement value)
    {
        var cloned = value.Clone();
        return new AutomationNodeRunResult(
            new Dictionary<string, IReadOnlyList<JsonElement>> { [port] = [cloned] },
            cloned.GetRawText());
    }

    private static JsonElement TryBuildMergedInput(
        Guid nodeId,
        string targetInput,
        JsonElement input,
        IReadOnlyDictionary<Guid, List<string>> incomingPorts,
        IDictionary<Guid, Dictionary<string, Queue<JsonElement>>> mergeBuffers)
    {
        if (!mergeBuffers.TryGetValue(nodeId, out var ports))
            mergeBuffers[nodeId] = ports = new Dictionary<string, Queue<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        if (!ports.TryGetValue(targetInput, out var values)) ports[targetInput] = values = new Queue<JsonElement>();
        values.Enqueue(input.Clone());

        var requiredPorts = incomingPorts.TryGetValue(nodeId, out var configured) ? configured : [targetInput];
        if (requiredPorts.Count < 2 || requiredPorts.Any(port => !ports.TryGetValue(port, out var queued) || queued.Count == 0))
            return default;

        var merged = new System.Text.Json.Nodes.JsonObject();
        foreach (var port in requiredPorts)
            merged[port] = System.Text.Json.Nodes.JsonNode.Parse(ports[port].Dequeue().GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }

    private async Task<AutomationExecutionView> LoadExecutionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await db.AutomationExecutions.AsNoTracking()
            .Include(item => item.NodeExecutions)
            .FirstAsync(item => item.Id == executionId, cancellationToken);
        return AutomationWorkflowService.ToExecutionView(execution);
    }
}
