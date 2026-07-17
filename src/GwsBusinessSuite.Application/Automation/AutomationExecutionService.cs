using System.Text.Json;
using System.Text.Json.Nodes;
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
    // A single stuck node step (e.g. a slow HTTP call) holds the heartbeat still without
    // crashing the process. The threshold must clear the longest a legitimate node can run —
    // the per-node TimeoutMs cap below (600_000ms) plus slack — or the resume sweep will treat
    // a merely-slow execution as orphaned and run its remaining frontier a second time.
    internal const long OrphanThresholdSeconds = 900;

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
            HeartbeatAtUnixSeconds = now.ToUnixTimeSeconds(),
            CreatedBy = "automation-engine"
        };
        db.AutomationExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);

        return await RunToCompletionAsync(execution, snapshot, () =>
        {
            var triggerType = mode switch
            {
                AutomationExecutionModes.Webhook => "core.webhookTrigger",
                AutomationExecutionModes.Schedule => "core.scheduleTrigger",
                _ => "core.manualTrigger"
            };
            var startNodes = snapshot.Nodes.Where(node => node.TypeKey == triggerType && !node.IsDisabled).ToList();
            if (startNodes.Count == 0) throw new InvalidOperationException($"The published workflow has no enabled trigger for {mode} execution.");

            var frontier = new Frontier { LastOutput = input.Clone() };
            foreach (var trigger in startNodes) frontier.Queue.Enqueue((trigger.Id, input.Clone(), "main"));
            return frontier;
        }, cancellationToken);
    }

    public async Task<AutomationExecutionView> ResumeAsync(
        Guid executionId,
        string signalPort = "main",
        string? mergeFieldsJson = null,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.AutomationExecutions.FirstOrDefaultAsync(item => item.Id == executionId, cancellationToken)
            ?? throw new KeyNotFoundException("Execution was not found.");
        if (execution.Status is not (AutomationExecutionStatuses.Running or AutomationExecutionStatuses.Waiting))
            throw new InvalidOperationException($"Execution is {execution.Status} and cannot be resumed.");

        var snapshot = await workflowService.GetSnapshotByVersionAsync(execution.WorkflowId, execution.WorkflowVersion, cancellationToken)
            ?? throw new InvalidOperationException("The workflow version this execution started on is no longer available.");

        execution.Status = AutomationExecutionStatuses.Running;
        await db.SaveChangesAsync(cancellationToken);

        return await RunToCompletionAsync(execution, snapshot, () =>
        {
            var frontier = DeserializeFrontier(execution.PendingStateJson);
            if (execution.WaitingNodeId is { } waitingNodeId)
            {
                var waitingInput = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(execution.WaitingInputJson) ? "{}" : execution.WaitingInputJson).RootElement.Clone();
                var merged = MergeFields(waitingInput, mergeFieldsJson);
                foreach (var connection in snapshot.Connections.Where(connection =>
                    connection.SourceNodeId == waitingNodeId && connection.SourceOutput.Equals(signalPort, StringComparison.OrdinalIgnoreCase)))
                {
                    frontier.Queue.Enqueue((connection.TargetNodeId, merged.Clone(), connection.TargetInput));
                }
                if (frontier.LastOutput.ValueKind == JsonValueKind.Undefined) frontier.LastOutput = merged.Clone();

                execution.WaitingNodeId = null;
                execution.WaitingNodeName = null;
                execution.WaitingNodeTypeKey = null;
                execution.WaitingInputJson = null;
                execution.ResumeAt = null;
                execution.ResumeAtUnixSeconds = null;
                execution.ResumeToken = null;
            }
            return frontier;
        }, cancellationToken);
    }

    public async Task<AutomationExecutionView> CancelAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execution = await db.AutomationExecutions.FirstOrDefaultAsync(item => item.Id == executionId, cancellationToken)
            ?? throw new KeyNotFoundException("Execution was not found.");
        if (execution.Status is AutomationExecutionStatuses.Running or AutomationExecutionStatuses.Waiting)
        {
            var finishedAt = timeProvider.GetUtcNow();
            execution.Status = AutomationExecutionStatuses.Canceled;
            execution.ErrorMessage = "Execution was canceled.";
            execution.FinishedAt = finishedAt;
            execution.FinishedAtUnixSeconds = finishedAt.ToUnixTimeSeconds();
            execution.PendingStateJson = "{}";
            execution.WaitingNodeId = null;
            execution.WaitingNodeName = null;
            execution.WaitingNodeTypeKey = null;
            execution.WaitingInputJson = null;
            execution.ResumeAt = null;
            execution.ResumeAtUnixSeconds = null;
            execution.ResumeToken = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        return await LoadExecutionAsync(executionId, CancellationToken.None);
    }

    public async Task<AutomationExecutionView> ResolveApprovalAsync(
        Guid executionId,
        bool approved,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.AutomationExecutions.AsNoTracking().FirstOrDefaultAsync(item => item.Id == executionId, cancellationToken)
            ?? throw new KeyNotFoundException("Execution was not found.");
        if (execution.Status != AutomationExecutionStatuses.Waiting || execution.WaitingNodeTypeKey != "core.approval")
            throw new InvalidOperationException("This execution is not waiting for an approval.");

        var mergeFields = JsonSerializer.Serialize(new { _approval = new { approved, comment = comment ?? string.Empty } });
        return await ResumeAsync(executionId, approved ? "approved" : "rejected", mergeFields, cancellationToken);
    }

    private async Task<AutomationExecutionView> RunToCompletionAsync(
        AutomationExecution execution,
        AutomationWorkflowSnapshot snapshot,
        Func<Frontier> buildFrontier,
        CancellationToken cancellationToken)
    {
        try
        {
            var frontier = buildFrontier();
            var paused = await RunLoopAsync(execution, snapshot, frontier, cancellationToken);
            if (!paused)
            {
                execution.Status = AutomationExecutionStatuses.Succeeded;
                if (frontier.LastOutput.ValueKind != JsonValueKind.Undefined) execution.OutputJson = frontier.LastOutput.GetRawText();
                execution.PendingStateJson = "{}";
            }
        }
        catch (ExecutionCanceledExternallyException)
        {
            // CancelAsync already committed the Canceled status from another request; nothing further to persist.
        }
        catch (OperationCanceledException)
        {
            execution.Status = AutomationExecutionStatuses.Canceled;
            execution.ErrorMessage = "Execution was canceled.";
            execution.PendingStateJson = "{}";
        }
        catch (Exception ex)
        {
            execution.Status = AutomationExecutionStatuses.Failed;
            execution.ErrorMessage = ex.Message;
            execution.PendingStateJson = "{}";
        }

        if (execution.Status is AutomationExecutionStatuses.Succeeded or AutomationExecutionStatuses.Failed or AutomationExecutionStatuses.Canceled)
        {
            var finishedAt = timeProvider.GetUtcNow();
            execution.FinishedAt = finishedAt;
            execution.FinishedAtUnixSeconds = finishedAt.ToUnixTimeSeconds();
            var workflow = await db.AutomationWorkflows.FirstAsync(item => item.Id == execution.WorkflowId, CancellationToken.None);
            workflow.LastExecutedAt = finishedAt;
            workflow.UpdatedAt = finishedAt;
            workflow.UpdatedBy = "automation-engine";
        }
        await db.SaveChangesAsync(CancellationToken.None);

        return await LoadExecutionAsync(execution.Id, CancellationToken.None);
    }

    private async Task<bool> RunLoopAsync(
        AutomationExecution execution,
        AutomationWorkflowSnapshot snapshot,
        Frontier frontier,
        CancellationToken cancellationToken)
    {
        var nodesById = snapshot.Nodes.ToDictionary(node => node.Id);
        var outgoing = snapshot.Connections.GroupBy(connection => connection.SourceNodeId).ToDictionary(group => group.Key, group => group.ToList());
        var incomingPorts = snapshot.Connections.GroupBy(connection => connection.TargetNodeId).ToDictionary(
            group => group.Key,
            group => group.Select(connection => connection.TargetInput).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var steps = 0;

        while (frontier.Queue.TryDequeue(out var work))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++steps > 10_000) throw new InvalidOperationException("Execution exceeded the 10,000 node-step safety limit.");
            if (await IsCanceledExternallyAsync(execution.Id, cancellationToken)) throw new ExecutionCanceledExternallyException();
            if (!nodesById.TryGetValue(work.NodeId, out var node)) continue;

            if (!node.IsDisabled && node.TypeKey is "core.wait" or "core.approval")
            {
                await PauseAsync(execution, node, work.Input, frontier, cancellationToken);
                return true;
            }

            var nodeInput = work.Input;
            if (node.TypeKey == "core.merge" && !node.IsDisabled)
            {
                nodeInput = TryBuildMergedInput(node.Id, work.TargetInput, work.Input, incomingPorts, frontier.MergeBuffers);
                if (nodeInput.ValueKind == JsonValueKind.Undefined)
                {
                    await CheckpointAsync(execution, frontier, cancellationToken);
                    continue;
                }
            }

            var result = node.IsDisabled
                ? SingleItemResult("main", nodeInput)
                : await ExecuteNodeWithEvidenceAsync(execution, node, nodeInput, cancellationToken);
            var latestItem = result.Outputs.Values.SelectMany(items => items).LastOrDefault();
            if (latestItem.ValueKind != JsonValueKind.Undefined) frontier.LastOutput = latestItem.Clone();

            if (outgoing.TryGetValue(node.Id, out var connections))
            {
                foreach (var connection in connections)
                {
                    if (!result.Outputs.TryGetValue(connection.SourceOutput, out var outputs)) continue;
                    if (!nodesById.ContainsKey(connection.TargetNodeId)) continue;
                    foreach (var output in outputs) frontier.Queue.Enqueue((connection.TargetNodeId, output.Clone(), connection.TargetInput));
                }
            }

            await CheckpointAsync(execution, frontier, cancellationToken);
        }

        return false;
    }

    private async Task<bool> IsCanceledExternallyAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var status = await db.AutomationExecutions.AsNoTracking()
            .Where(item => item.Id == executionId).Select(item => item.Status).FirstOrDefaultAsync(cancellationToken);
        return status == AutomationExecutionStatuses.Canceled;
    }

    private async Task CheckpointAsync(AutomationExecution execution, Frontier frontier, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        execution.PendingStateJson = SerializeFrontier(frontier);
        execution.HeartbeatAtUnixSeconds = now.ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PauseAsync(
        AutomationExecution execution,
        AutomationNodeSnapshot node,
        JsonElement input,
        Frontier frontier,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var mode = node.TypeKey == "core.wait" ? (ReadParameterString(node.ParametersJson, "mode")?.Trim().ToLowerInvariant() ?? "duration") : null;
        var resumeAt = ComputeResumeAt(node, now);

        execution.Status = AutomationExecutionStatuses.Waiting;
        execution.WaitingNodeId = node.Id;
        execution.WaitingNodeName = node.Name;
        execution.WaitingNodeTypeKey = node.TypeKey;
        execution.WaitingInputJson = input.GetRawText();
        execution.ResumeAt = resumeAt;
        execution.ResumeAtUnixSeconds = resumeAt?.ToUnixTimeSeconds();
        execution.ResumeToken = mode == "webhook" ? Guid.NewGuid().ToString("N") : null;
        execution.PendingStateJson = SerializeFrontier(frontier);
        execution.HeartbeatAtUnixSeconds = now.ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DateTimeOffset? ComputeResumeAt(AutomationNodeSnapshot node, DateTimeOffset now)
    {
        var root = ParseParameters(node.ParametersJson);
        if (node.TypeKey == "core.wait")
        {
            var mode = root["mode"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "duration";
            return mode switch
            {
                "duration" => now.AddMilliseconds(root["durationMs"]?.GetValue<double>() ?? 60_000),
                "timestamp" => DateTimeOffset.TryParse(root["timestamp"]?.GetValue<string>(), out var timestamp) ? timestamp : now,
                _ => null
            };
        }
        if (node.TypeKey == "core.approval")
        {
            var hours = root["timeoutHours"]?.GetValue<double>() ?? 0;
            return hours > 0 ? now.AddHours(hours) : null;
        }
        return null;
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
                var result = node.TimeoutMs > 0
                    ? await ExecuteWithTimeoutAsync(node, input, credentialJson, cancellationToken)
                    : await nodeRegistry.ExecuteAsync(node, input, credentialJson, cancellationToken);
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

    private async Task<AutomationNodeRunResult> ExecuteWithTimeoutAsync(
        AutomationNodeSnapshot node,
        JsonElement input,
        string? credentialJson,
        CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Clamp(node.TimeoutMs, 100, 600_000);
        using var timeoutSource = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            return await nodeRegistry.ExecuteAsync(node, input, credentialJson, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{node.Name} timed out after {timeoutMs}ms.");
        }
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

        var merged = new JsonObject();
        foreach (var port in requiredPorts)
            merged[port] = JsonNode.Parse(ports[port].Dequeue().GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }

    private static JsonElement MergeFields(JsonElement input, string? mergeFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(mergeFieldsJson)) return input.Clone();
        var target = input.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(input.GetRawText())!.AsObject()
            : new JsonObject { ["value"] = JsonNode.Parse(input.GetRawText()) };
        var extra = JsonNode.Parse(mergeFieldsJson)?.AsObject();
        if (extra is not null) foreach (var property in extra) target[property.Key] = property.Value?.DeepClone();
        return JsonSerializer.SerializeToElement(target);
    }

    private static JsonObject ParseParameters(string json) =>
        (string.IsNullOrWhiteSpace(json) ? new JsonObject() : JsonNode.Parse(json)?.AsObject()) ?? new JsonObject();

    private static string? ReadParameterString(string parametersJson, string name) => ParseParameters(parametersJson)[name]?.GetValue<string>();

    private static string SerializeFrontier(Frontier frontier)
    {
        var dto = new FrontierDto(
            frontier.Queue.Select(item => new FrontierItemDto(item.NodeId, item.Input, item.TargetInput)).ToList(),
            frontier.MergeBuffers.SelectMany(nodeEntry => nodeEntry.Value.Select(portEntry =>
                new MergeBufferDto(nodeEntry.Key, portEntry.Key, portEntry.Value.ToList()))).ToList(),
            frontier.LastOutput.ValueKind == JsonValueKind.Undefined ? null : frontier.LastOutput);
        return JsonSerializer.Serialize(dto);
    }

    private static Frontier DeserializeFrontier(string json)
    {
        var frontier = new Frontier();
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return frontier;
        var dto = JsonSerializer.Deserialize<FrontierDto>(json);
        if (dto is null) return frontier;
        foreach (var item in dto.Queue) frontier.Queue.Enqueue((item.NodeId, item.Input.Clone(), item.TargetInput));
        foreach (var buffer in dto.MergeBuffers)
        {
            if (!frontier.MergeBuffers.TryGetValue(buffer.NodeId, out var ports))
                frontier.MergeBuffers[buffer.NodeId] = ports = new Dictionary<string, Queue<JsonElement>>(StringComparer.OrdinalIgnoreCase);
            ports[buffer.Port] = new Queue<JsonElement>(buffer.Items.Select(value => value.Clone()));
        }
        if (dto.LastOutput.HasValue) frontier.LastOutput = dto.LastOutput.Value.Clone();
        return frontier;
    }

    private async Task<AutomationExecutionView> LoadExecutionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await db.AutomationExecutions.AsNoTracking()
            .Include(item => item.NodeExecutions)
            .FirstAsync(item => item.Id == executionId, cancellationToken);
        return AutomationWorkflowService.ToExecutionView(execution);
    }

    private sealed class Frontier
    {
        public Queue<(Guid NodeId, JsonElement Input, string TargetInput)> Queue { get; } = new();
        public Dictionary<Guid, Dictionary<string, Queue<JsonElement>>> MergeBuffers { get; } = new();
        public JsonElement LastOutput { get; set; }
    }

    private sealed record FrontierItemDto(Guid NodeId, JsonElement Input, string TargetInput);

    private sealed record MergeBufferDto(Guid NodeId, string Port, List<JsonElement> Items);

    private sealed record FrontierDto(List<FrontierItemDto> Queue, List<MergeBufferDto> MergeBuffers, JsonElement? LastOutput);

    private sealed class ExecutionCanceledExternallyException : Exception;
}
