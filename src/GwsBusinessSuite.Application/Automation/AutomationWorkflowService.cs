using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationWorkflowService(
    IAppDbContext db,
    IAutomationNodeRegistry nodeRegistry,
    TimeProvider timeProvider) : IAutomationWorkflowService
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AutomationWorkflowSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var workflows = await db.AutomationWorkflows.AsNoTracking()
            .OrderBy(workflow => workflow.Name)
            .Select(workflow => new
            {
                workflow.Id,
                workflow.Name,
                workflow.Description,
                workflow.Status,
                workflow.CurrentVersion,
                workflow.LastExecutedAt,
                workflow.CreatedAt,
                workflow.UpdatedAt,
                NodeCount = workflow.Nodes.Count
            })
            .ToListAsync(cancellationToken);

        return workflows.Select(workflow => new AutomationWorkflowSummary(
            workflow.Id,
            workflow.Name,
            workflow.Description,
            workflow.Status,
            workflow.CurrentVersion,
            workflow.NodeCount,
            workflow.LastExecutedAt,
            workflow.UpdatedAt ?? workflow.CreatedAt)).ToList();
    }

    public async Task<AutomationWorkflowView?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await db.AutomationWorkflows.AsNoTracking()
            .Include(item => item.Nodes)
            .Include(item => item.Connections)
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken);
        if (workflow is null) return null;

        var executions = await db.AutomationExecutions.AsNoTracking()
            .Where(execution => execution.WorkflowId == workflowId)
            .OrderByDescending(execution => execution.StartedAtUnixSeconds)
            .Take(20)
            .ToListAsync(cancellationToken);
        return ToView(workflow, executions);
    }

    public async Task<AutomationWorkflowView> CreateAsync(
        string name,
        string description = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Workflow name is required.", nameof(name));

        var workflow = new AutomationWorkflow
        {
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            CreatedBy = "user"
        };
        var trigger = nodeRegistry.Find("core.manualTrigger")
            ?? throw new InvalidOperationException("The Manual Trigger node is not registered.");
        workflow.Nodes.Add(new AutomationNode
        {
            Name = trigger.DisplayName,
            TypeKey = trigger.TypeKey,
            TypeVersion = trigger.Version,
            PositionX = 120,
            PositionY = 180,
            ParametersJson = trigger.DefaultParametersJson,
            CreatedBy = "user"
        });
        db.AutomationWorkflows.Add(workflow);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(workflow.Id, cancellationToken))!;
    }

    public async Task UpdateMetadataAsync(
        Guid workflowId,
        string name,
        string description,
        string tagsCsv,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Workflow name is required.", nameof(name));
        var workflow = await GetWorkflowAsync(workflowId, cancellationToken);
        workflow.Name = name.Trim();
        workflow.Description = description?.Trim() ?? string.Empty;
        workflow.TagsCsv = tagsCsv?.Trim() ?? string.Empty;
        Touch(workflow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationNodeView> SaveNodeAsync(
        Guid workflowId,
        AutomationNodeEditor editor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var definition = nodeRegistry.Find(editor.TypeKey, editor.TypeVersion)
            ?? throw new InvalidOperationException($"Node type '{editor.TypeKey}' version {editor.TypeVersion} is not registered.");
        if (string.IsNullOrWhiteSpace(editor.Name)) throw new ArgumentException("Node name is required.", nameof(editor));
        EnsureValidJson(editor.ParametersJson, "Node parameters");

        var duplicateName = await db.AutomationNodes.AsNoTracking().AnyAsync(node =>
            node.WorkflowId == workflowId && node.Name == editor.Name.Trim() && node.Id != editor.Id, cancellationToken);
        if (duplicateName) throw new InvalidOperationException($"A node named '{editor.Name.Trim()}' already exists in this workflow.");

        AutomationNode node;
        if (editor.Id.HasValue)
        {
            node = await db.AutomationNodes.FirstOrDefaultAsync(item =>
                item.WorkflowId == workflowId && item.Id == editor.Id.Value, cancellationToken)
                ?? throw new KeyNotFoundException("Workflow node was not found.");
        }
        else
        {
            node = new AutomationNode { WorkflowId = workflowId, Name = editor.Name.Trim(), TypeKey = definition.TypeKey, CreatedBy = "user" };
            db.AutomationNodes.Add(node);
        }

        node.Name = editor.Name.Trim();
        node.TypeKey = definition.TypeKey;
        node.TypeVersion = definition.Version;
        node.PositionX = Math.Clamp(editor.PositionX, 0, 5000);
        node.PositionY = Math.Clamp(editor.PositionY, 0, 5000);
        node.ParametersJson = string.IsNullOrWhiteSpace(editor.ParametersJson) ? "{}" : editor.ParametersJson.Trim();
        node.CredentialId = editor.CredentialId;
        node.IsDisabled = editor.IsDisabled;
        node.ContinueOnFail = editor.ContinueOnFail;
        node.RetryOnFail = editor.RetryOnFail;
        node.MaxTries = Math.Clamp(editor.MaxTries, 1, 10);
        node.WaitBetweenTriesMs = Math.Clamp(editor.WaitBetweenTriesMs, 0, 60_000);
        node.Notes = editor.Notes?.Trim() ?? string.Empty;
        node.UpdatedAt = timeProvider.GetUtcNow();
        node.UpdatedBy = "user";
        await db.SaveChangesAsync(cancellationToken);
        return ToNodeView(node);
    }

    public async Task MoveNodeAsync(
        Guid workflowId,
        Guid nodeId,
        double positionX,
        double positionY,
        CancellationToken cancellationToken = default)
    {
        var node = await db.AutomationNodes.FirstOrDefaultAsync(item =>
            item.WorkflowId == workflowId && item.Id == nodeId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow node was not found.");
        node.PositionX = Math.Clamp(positionX, 0, 5000);
        node.PositionY = Math.Clamp(positionY, 0, 5000);
        node.UpdatedAt = timeProvider.GetUtcNow();
        node.UpdatedBy = "user";
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteNodeAsync(Guid workflowId, Guid nodeId, CancellationToken cancellationToken = default)
    {
        var node = await db.AutomationNodes.FirstOrDefaultAsync(item =>
            item.WorkflowId == workflowId && item.Id == nodeId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow node was not found.");
        var connections = await db.AutomationConnections.Where(connection =>
            connection.WorkflowId == workflowId
            && (connection.SourceNodeId == nodeId || connection.TargetNodeId == nodeId)).ToListAsync(cancellationToken);
        db.AutomationConnections.RemoveRange(connections);
        db.AutomationNodes.Remove(node);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationConnectionView> AddConnectionAsync(
        Guid workflowId,
        Guid sourceNodeId,
        string sourceOutput,
        Guid targetNodeId,
        string targetInput = "main",
        CancellationToken cancellationToken = default)
    {
        if (sourceNodeId == targetNodeId) throw new InvalidOperationException("A node cannot connect to itself.");
        var nodes = await db.AutomationNodes.AsNoTracking()
            .Where(node => node.WorkflowId == workflowId && (node.Id == sourceNodeId || node.Id == targetNodeId))
            .ToListAsync(cancellationToken);
        if (nodes.Count != 2) throw new InvalidOperationException("Both connection nodes must belong to this workflow.");

        var source = nodes.Single(node => node.Id == sourceNodeId);
        var sourceDefinition = nodeRegistry.Find(source.TypeKey, source.TypeVersion)!;
        var normalizedOutput = string.IsNullOrWhiteSpace(sourceOutput) ? "main" : sourceOutput.Trim();
        if (!sourceDefinition.Outputs.Contains(normalizedOutput, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Node '{source.Name}' has no '{normalizedOutput}' output.");

        var connection = new AutomationConnection
        {
            WorkflowId = workflowId,
            SourceNodeId = sourceNodeId,
            SourceOutput = normalizedOutput,
            TargetNodeId = targetNodeId,
            TargetInput = string.IsNullOrWhiteSpace(targetInput) ? "main" : targetInput.Trim(),
            CreatedBy = "user"
        };
        db.AutomationConnections.Add(connection);
        await db.SaveChangesAsync(cancellationToken);

        var validation = await ValidateAsync(workflowId, cancellationToken);
        if (validation.Errors.Any(error => error.Contains("cycle", StringComparison.OrdinalIgnoreCase)))
        {
            db.AutomationConnections.Remove(connection);
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("This connection would create a workflow cycle. Use a future Loop node for controlled iteration.");
        }
        return ToConnectionView(connection);
    }

    public async Task DeleteConnectionAsync(Guid workflowId, Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await db.AutomationConnections.FirstOrDefaultAsync(item =>
            item.WorkflowId == workflowId && item.Id == connectionId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow connection was not found.");
        db.AutomationConnections.Remove(connection);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationValidationResult> ValidateAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await db.AutomationWorkflows.AsNoTracking()
            .Include(item => item.Nodes)
            .Include(item => item.Connections)
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow was not found.");
        var errors = new List<string>();
        if (workflow.Nodes.Count == 0) errors.Add("Add at least one node.");
        if (!workflow.Nodes.Any(node => nodeRegistry.Find(node.TypeKey, node.TypeVersion)?.IsTrigger == true))
            errors.Add("Add at least one trigger node.");

        foreach (var node in workflow.Nodes)
        {
            if (nodeRegistry.Find(node.TypeKey, node.TypeVersion) is null)
                errors.Add($"Node '{node.Name}' uses unavailable type '{node.TypeKey}' v{node.TypeVersion}.");
            try { EnsureValidJson(node.ParametersJson, $"Parameters for '{node.Name}'"); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }

        var webhookNodes = workflow.Nodes.Where(node => node.TypeKey == "core.webhookTrigger" && !node.IsDisabled).ToList();
        if (webhookNodes.Count > 1) errors.Add("This foundation supports one enabled Webhook Trigger per workflow.");
        foreach (var node in webhookNodes)
        {
            var path = ReadStringParameter(node.ParametersJson, "path");
            if (string.IsNullOrWhiteSpace(path) || path.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
                errors.Add($"Webhook Trigger '{node.Name}' needs a path containing only letters, numbers, hyphens, or underscores.");
        }
        var scheduleNodes = workflow.Nodes.Where(node => node.TypeKey == "core.scheduleTrigger" && !node.IsDisabled).ToList();
        if (scheduleNodes.Count > 1) errors.Add("This foundation supports one enabled Schedule Trigger per workflow.");
        foreach (var node in scheduleNodes)
            if (ReadIntParameter(node.ParametersJson, "intervalMinutes") is not (>= 1 and <= 525600))
                errors.Add($"Schedule Trigger '{node.Name}' needs intervalMinutes between 1 and 525600.");

        foreach (var mergeNode in workflow.Nodes.Where(node => node.TypeKey == "core.merge" && !node.IsDisabled))
        {
            var inputs = workflow.Connections.Where(connection => connection.TargetNodeId == mergeNode.Id)
                .Select(connection => connection.TargetInput)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (inputs.Count < 2)
                errors.Add($"Merge node '{mergeNode.Name}' needs at least two differently labeled inputs, such as input1 and input2.");
        }

        var nodeIds = workflow.Nodes.Select(node => node.Id).ToHashSet();
        if (workflow.Connections.Any(connection => !nodeIds.Contains(connection.SourceNodeId) || !nodeIds.Contains(connection.TargetNodeId)))
            errors.Add("One or more connections reference a missing node.");
        if (HasCycle(nodeIds, workflow.Connections)) errors.Add("The workflow graph contains a cycle.");
        return new AutomationValidationResult(errors.Count == 0, errors);
    }

    public async Task<int> PublishAsync(Guid workflowId, string changeSummary, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(workflowId, cancellationToken);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(" ", validation.Errors));

        var workflow = await db.AutomationWorkflows
            .Include(item => item.Nodes)
            .Include(item => item.Connections)
            .FirstAsync(item => item.Id == workflowId, cancellationToken);
        var versionNumber = workflow.CurrentVersion + 1;
        var snapshot = BuildSnapshot(workflow, versionNumber);
        var webhookNode = workflow.Nodes.FirstOrDefault(node => node.TypeKey == "core.webhookTrigger" && !node.IsDisabled);
        var scheduleNode = workflow.Nodes.FirstOrDefault(node => node.TypeKey == "core.scheduleTrigger" && !node.IsDisabled);
        var webhookPath = webhookNode is null ? null : ReadStringParameter(webhookNode.ParametersJson, "path")?.Trim();
        if (!string.IsNullOrWhiteSpace(webhookPath))
        {
            var pathInUse = await db.AutomationWorkflows.AsNoTracking().AnyAsync(item =>
                item.Id != workflow.Id && item.WebhookPath == webhookPath, cancellationToken);
            if (pathInUse) throw new InvalidOperationException($"Webhook path '{webhookPath}' is already used by another workflow.");
        }
        db.AutomationWorkflowVersions.Add(new AutomationWorkflowVersion
        {
            WorkflowId = workflow.Id,
            VersionNumber = versionNumber,
            SnapshotJson = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions),
            ChangeSummary = changeSummary?.Trim() ?? string.Empty,
            CreatedBy = "user"
        });
        workflow.CurrentVersion = versionNumber;
        workflow.PublishedAt = timeProvider.GetUtcNow();
        workflow.WebhookPath = webhookPath;
        workflow.ScheduleIntervalMinutes = scheduleNode is null ? null : ReadIntParameter(scheduleNode.ParametersJson, "intervalMinutes");
        workflow.NextScheduledAt = workflow.ScheduleIntervalMinutes.HasValue
            ? timeProvider.GetUtcNow().AddMinutes(workflow.ScheduleIntervalMinutes.Value)
            : null;
        workflow.NextScheduledAtUnixSeconds = workflow.NextScheduledAt?.ToUnixTimeSeconds();
        if (workflow.Status == AutomationWorkflowStatuses.Draft) workflow.Status = AutomationWorkflowStatuses.Inactive;
        Touch(workflow);
        await db.SaveChangesAsync(cancellationToken);
        return versionNumber;
    }

    public async Task SetActiveAsync(Guid workflowId, bool active, CancellationToken cancellationToken = default)
    {
        var workflow = await GetWorkflowAsync(workflowId, cancellationToken);
        if (active && workflow.CurrentVersion == 0)
            throw new InvalidOperationException("Publish a valid workflow before activating it.");
        workflow.Status = active ? AutomationWorkflowStatuses.Active : AutomationWorkflowStatuses.Inactive;
        Touch(workflow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AutomationWorkflowSnapshot?> GetPublishedSnapshotAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var version = await db.AutomationWorkflowVersions.AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .OrderByDescending(item => item.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);
        return version is null
            ? null
            : JsonSerializer.Deserialize<AutomationWorkflowSnapshot>(version.SnapshotJson, SnapshotJsonOptions);
    }

    public async Task<AutomationExecutionView?> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execution = await db.AutomationExecutions.AsNoTracking()
            .Include(item => item.NodeExecutions)
            .FirstOrDefaultAsync(item => item.Id == executionId, cancellationToken);
        return execution is null ? null : ToExecutionView(execution);
    }

    private async Task<AutomationWorkflow> GetWorkflowAsync(Guid id, CancellationToken cancellationToken) =>
        await db.AutomationWorkflows.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException("Workflow was not found.");

    private void Touch(AutomationWorkflow workflow)
    {
        workflow.UpdatedAt = timeProvider.GetUtcNow();
        workflow.UpdatedBy = "user";
    }

    private static void EnsureValidJson(string json, string label)
    {
        try { JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).Dispose(); }
        catch (JsonException ex) { throw new InvalidOperationException($"{label} must be valid JSON: {ex.Message}"); }
    }

    private static string? ReadStringParameter(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static int? ReadIntParameter(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool HasCycle(IReadOnlySet<Guid> nodeIds, IEnumerable<AutomationConnection> connections)
    {
        var indegree = nodeIds.ToDictionary(id => id, _ => 0);
        var outgoing = nodeIds.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var connection in connections)
        {
            if (!indegree.ContainsKey(connection.TargetNodeId) || !outgoing.ContainsKey(connection.SourceNodeId)) continue;
            indegree[connection.TargetNodeId]++;
            outgoing[connection.SourceNodeId].Add(connection.TargetNodeId);
        }
        var queue = new Queue<Guid>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (queue.TryDequeue(out var id))
        {
            visited++;
            foreach (var target in outgoing[id]) if (--indegree[target] == 0) queue.Enqueue(target);
        }
        return visited != nodeIds.Count;
    }

    private static AutomationWorkflowSnapshot BuildSnapshot(AutomationWorkflow workflow, int version) => new()
    {
        WorkflowId = workflow.Id,
        Name = workflow.Name,
        Version = version,
        Nodes = workflow.Nodes.Select(node => new AutomationNodeSnapshot(
            node.Id, node.Name, node.TypeKey, node.TypeVersion, node.ParametersJson, node.CredentialId,
            node.IsDisabled, node.ContinueOnFail, node.RetryOnFail, node.MaxTries, node.WaitBetweenTriesMs)).ToList(),
        Connections = workflow.Connections.Select(connection => new AutomationConnectionSnapshot(
            connection.SourceNodeId, connection.SourceOutput, connection.TargetNodeId, connection.TargetInput)).ToList()
    };

    private static AutomationWorkflowView ToView(AutomationWorkflow workflow, IReadOnlyList<AutomationExecution> executions) => new()
    {
        Id = workflow.Id,
        Name = workflow.Name,
        Description = workflow.Description,
        Status = workflow.Status,
        TagsCsv = workflow.TagsCsv,
        CurrentVersion = workflow.CurrentVersion,
        PublishedAt = workflow.PublishedAt,
        Nodes = workflow.Nodes.OrderBy(node => node.CreatedAt).Select(ToNodeView).ToList(),
        Connections = workflow.Connections.Select(ToConnectionView).ToList(),
        RecentExecutions = executions.Select(execution => new AutomationExecutionSummary(
            execution.Id, execution.Mode, execution.Status, execution.StartedAt, execution.FinishedAt, execution.ErrorMessage)).ToList()
    };

    private static AutomationNodeView ToNodeView(AutomationNode node) => new(
        node.Id, node.Name, node.TypeKey, node.TypeVersion, node.PositionX, node.PositionY,
        node.ParametersJson, node.CredentialId, node.IsDisabled, node.ContinueOnFail,
        node.RetryOnFail, node.MaxTries, node.WaitBetweenTriesMs, node.Notes);

    private static AutomationConnectionView ToConnectionView(AutomationConnection connection) => new(
        connection.Id, connection.SourceNodeId, connection.SourceOutput, connection.TargetNodeId, connection.TargetInput);

    internal static AutomationExecutionView ToExecutionView(AutomationExecution execution) => new(
        execution.Id, execution.WorkflowId, execution.WorkflowVersion, execution.Mode, execution.Status,
        execution.InputJson, execution.OutputJson, execution.ErrorMessage, execution.StartedAt, execution.FinishedAt,
        execution.NodeExecutions.OrderBy(node => node.StartedAtUnixSeconds).Select(node => new AutomationNodeExecutionView(
            node.Id, node.NodeId, node.NodeName, node.NodeTypeKey, node.Status, node.Attempt,
            node.InputJson, node.OutputJson, node.ErrorMessage, node.StartedAt, node.FinishedAt)).ToList());
}
