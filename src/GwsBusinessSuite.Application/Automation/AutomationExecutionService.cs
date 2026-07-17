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
            var queue = new Queue<(AutomationNodeSnapshot Node, JsonElement Input)>();
            foreach (var trigger in startNodes) queue.Enqueue((trigger, input.Clone()));
            var lastOutput = input.Clone();
            var steps = 0;

            while (queue.TryDequeue(out var work))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++steps > 10_000) throw new InvalidOperationException("Execution exceeded the 10,000 node-step safety limit.");
                var result = work.Node.IsDisabled
                    ? new AutomationNodeRunResult(new Dictionary<string, JsonElement> { ["main"] = work.Input.Clone() }, work.Input.GetRawText())
                    : await ExecuteNodeWithEvidenceAsync(execution, work.Node, work.Input, cancellationToken);
                lastOutput = result.Outputs.Values.LastOrDefault().ValueKind == JsonValueKind.Undefined
                    ? lastOutput
                    : result.Outputs.Values.Last().Clone();

                if (!outgoing.TryGetValue(work.Node.Id, out var connections)) continue;
                foreach (var connection in connections)
                {
                    if (!result.Outputs.TryGetValue(connection.SourceOutput, out var output)) continue;
                    if (nodesById.TryGetValue(connection.TargetNodeId, out var target)) queue.Enqueue((target, output.Clone()));
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
                    return new AutomationNodeRunResult(new Dictionary<string, JsonElement> { ["main"] = error }, error.GetRawText());
                }
                throw;
            }
        }
        throw new InvalidOperationException("Node execution ended without a result.");
    }

    private async Task<AutomationExecutionView> LoadExecutionAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await db.AutomationExecutions.AsNoTracking()
            .Include(item => item.NodeExecutions)
            .FirstAsync(item => item.Id == executionId, cancellationToken);
        return AutomationWorkflowService.ToExecutionView(execution);
    }
}
