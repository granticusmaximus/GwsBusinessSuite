using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationWorkflowService(
    IAppDbContext db,
    IAutomationNodeRegistry nodeRegistry,
    TimeProvider timeProvider,
    IMemoryCache? cache = null,
    // Optional, resolved by DI in production - records publish/activate governance actions into
    // the unified security audit stream (Part 4.9). Routine execution runs are deliberately not
    // recorded here: AutomationExecution/AutomationNodeExecution already durably capture every
    // run's own full evidence trail, so duplicating that volume into the audit stream would just
    // be noise: this only covers the governance actions "runs" don't already account for.
    // ActorUsername is left unset on each RecordAsync call so SecurityAuditService resolves the
    // real signed-in user itself via ICurrentUserAccessor, since this service's own UpdatedBy
    // tracking is a pre-existing "user" placeholder, not a real actor.
    ISecurityAuditService? securityAudit = null) : IAutomationWorkflowService
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);
    // Falls back to a private, per-instance cache when constructed without DI - existing
    // tests new this up directly with just the first three dependencies, same
    // optional-dependency pattern as CrmService's own IMemoryCache fallback.
    private readonly IMemoryCache _cache = cache ?? new MemoryCache(new MemoryCacheOptions());

    // A published version's SnapshotJson is immutable once written (a re-publish creates a new
    // version number), so caching by (workflowId, versionNumber) is safe indefinitely. This
    // matters most for AutomationTriggerService.ResumeDueWaitsAsync, which calls ResumeAsync (and
    // therefore this lookup) once per due execution in a sweep - executions from the same
    // workflow/version otherwise re-fetch and re-deserialize the identical snapshot on every
    // iteration.
    private static string SnapshotCacheKey(Guid workflowId, int versionNumber) =>
        $"automation-workflow-snapshot:{workflowId:N}:{versionNumber}";

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
                workflow.TagsCsv,
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
            workflow.TagsCsv,
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

    public async Task DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await db.AutomationWorkflows.FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken);
        if (workflow is null)
        {
            return;
        }

        var permissions = await db.SentinelResourcePermissions
            .Where(item => item.TargetId == workflowId)
            .ToListAsync(cancellationToken);
        db.SentinelResourcePermissions.RemoveRange(permissions);

        var shares = await db.SentinelPublicShares
            .Where(item => item.TargetId == workflowId && item.IsAutomationWorkflow)
            .ToListAsync(cancellationToken);
        db.SentinelPublicShares.RemoveRange(shares);

        var workflowName = workflow.Name;
        db.AutomationWorkflows.Remove(workflow);
        await db.SaveChangesAsync(cancellationToken);

        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "AutomationWorkflowDeleted", SecurityAuditOutcomes.Succeeded,
                TargetType: "AutomationWorkflow", TargetId: workflowId.ToString(),
                Details: new Dictionary<string, string?> { ["name"] = workflowName }), cancellationToken);
        }
    }

    public async Task<AutomationWorkflowView> DuplicateAsync(
        Guid workflowId, string newName, string performedBy, CancellationToken cancellationToken = default)
    {
        var source = await GetAsync(workflowId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow was not found.");
        var name = string.IsNullOrWhiteSpace(newName) ? $"{source.Name} (copy)" : newName.Trim();
        return await CreateFromGraphAsync(name, source.Description, source.Nodes, source.Connections, performedBy, cancellationToken);
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
        node.TimeoutMs = Math.Clamp(editor.TimeoutMs, 0, 600_000);
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
        {
            var cronExpression = ReadStringParameter(node.ParametersJson, "cronExpression");
            if (!string.IsNullOrWhiteSpace(cronExpression))
            {
                try { CronSchedule.Validate(cronExpression); }
                catch (FormatException ex) { errors.Add($"Schedule Trigger '{node.Name}' has an invalid cronExpression: {ex.Message}"); }
            }
            else if (ReadIntParameter(node.ParametersJson, "intervalMinutes") is not (>= 1 and <= 525600))
            {
                errors.Add($"Schedule Trigger '{node.Name}' needs intervalMinutes between 1 and 525600, or a cronExpression.");
            }
        }

        foreach (var databaseTriggerNode in workflow.Nodes.Where(node => node.TypeKey == "database.rowChangedTrigger" && !node.IsDisabled))
        {
            try { DatabaseTriggerConditions.ValidateConditions(databaseTriggerNode.ParametersJson, databaseTriggerNode.Name); }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }

        foreach (var mergeNode in workflow.Nodes.Where(node => node.TypeKey == "core.merge" && !node.IsDisabled))
        {
            var inputs = workflow.Connections.Where(connection => connection.TargetNodeId == mergeNode.Id)
                .Select(connection => connection.TargetInput)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (inputs.Count < 2)
                errors.Add($"Merge node '{mergeNode.Name}' needs at least two differently labeled inputs, such as input1 and input2.");
        }

        foreach (var waitNode in workflow.Nodes.Where(node => node.TypeKey == "core.wait" && !node.IsDisabled))
        {
            var mode = ReadStringParameter(waitNode.ParametersJson, "mode");
            if (mode is not ("duration" or "timestamp" or "webhook"))
            {
                errors.Add($"Wait node '{waitNode.Name}' needs mode to be duration, timestamp, or webhook.");
            }
            else if (mode == "duration" && ReadIntParameter(waitNode.ParametersJson, "durationMs") is not (>= 1000))
            {
                errors.Add($"Wait node '{waitNode.Name}' needs durationMs of at least 1000.");
            }
            else if (mode == "timestamp" && !DateTimeOffset.TryParse(ReadStringParameter(waitNode.ParametersJson, "timestamp"), out _))
            {
                errors.Add($"Wait node '{waitNode.Name}' needs a valid ISO-8601 timestamp.");
            }
        }

        foreach (var approvalNode in workflow.Nodes.Where(node => node.TypeKey == "core.approval" && !node.IsDisabled))
            if (ReadIntParameter(approvalNode.ParametersJson, "timeoutHours") is int hours and < 0)
                errors.Add($"Approval node '{approvalNode.Name}' needs timeoutHours to be 0 or greater.");

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
        var databaseTriggerNode = workflow.Nodes.FirstOrDefault(node => node.TypeKey == "database.rowChangedTrigger" && !node.IsDisabled);
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
        var cronExpression = scheduleNode is null ? null : ReadStringParameter(scheduleNode.ParametersJson, "cronExpression")?.Trim();
        workflow.ScheduleCronExpression = string.IsNullOrWhiteSpace(cronExpression) ? null : cronExpression;
        // cronExpression takes precedence when both are set - intervalMinutes stays populated
        // either way so switching back to interval-only mode later doesn't lose the old value.
        workflow.ScheduleIntervalMinutes = scheduleNode is null ? null : ReadIntParameter(scheduleNode.ParametersJson, "intervalMinutes");
        workflow.NextScheduledAt = workflow.ScheduleCronExpression is not null
            ? CronSchedule.GetNextOccurrence(workflow.ScheduleCronExpression, timeProvider.GetUtcNow())
            : workflow.ScheduleIntervalMinutes.HasValue
                ? timeProvider.GetUtcNow().AddMinutes(workflow.ScheduleIntervalMinutes.Value)
                : null;
        workflow.NextScheduledAtUnixSeconds = workflow.NextScheduledAt?.ToUnixTimeSeconds();
        workflow.TriggerWikiDatabaseId = databaseTriggerNode is not null
            && Guid.TryParse(ReadStringParameter(databaseTriggerNode.ParametersJson, "wikiDatabaseId"), out var wikiDatabaseId)
                ? wikiDatabaseId
                : null;
        workflow.TriggerCrmDealStageChanged = workflow.Nodes.Any(node => node.TypeKey == "crm.dealStageChangedTrigger" && !node.IsDisabled);
        workflow.TriggerCmsPagePublished = workflow.Nodes.Any(node => node.TypeKey == "cms.pagePublishedTrigger" && !node.IsDisabled);
        workflow.TriggerSentinelChatPromptSubmitted = workflow.Nodes.Any(node => node.TypeKey == "sentinel.chatPromptSubmittedTrigger" && !node.IsDisabled);
        workflow.TriggerSupportTicketCreated = workflow.Nodes.Any(node => node.TypeKey == "support.ticketCreatedTrigger" && !node.IsDisabled);
        workflow.TriggerSupportTicketReplied = workflow.Nodes.Any(node => node.TypeKey == "support.ticketRepliedTrigger" && !node.IsDisabled);
        workflow.TriggerSupportTicketSlaBreached = workflow.Nodes.Any(node => node.TypeKey == "support.ticketSlaBreachedTrigger" && !node.IsDisabled);
        if (workflow.Status == AutomationWorkflowStatuses.Draft) workflow.Status = AutomationWorkflowStatuses.Inactive;
        Touch(workflow);
        await db.SaveChangesAsync(cancellationToken);
        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, "AutomationWorkflowPublished", SecurityAuditOutcomes.Succeeded,
                TargetType: "AutomationWorkflow", TargetId: workflow.Id.ToString(),
                Details: new Dictionary<string, string?> { ["version"] = versionNumber.ToString() }), cancellationToken);
        }
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
        if (securityAudit is not null)
        {
            await securityAudit.RecordAsync(new SecurityAuditInput(
                SecurityAuditCategories.DataLifecycle, active ? "AutomationWorkflowActivated" : "AutomationWorkflowDeactivated",
                SecurityAuditOutcomes.Succeeded, TargetType: "AutomationWorkflow", TargetId: workflow.Id.ToString()), cancellationToken);
        }
    }

    public async Task SetAllowDownstreamAutomationTriggersAsync(Guid workflowId, bool allow, CancellationToken cancellationToken = default)
    {
        var workflow = await GetWorkflowAsync(workflowId, cancellationToken);
        workflow.AllowDownstreamAutomationTriggers = allow;
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

    public async Task<AutomationWorkflowSnapshot?> GetSnapshotByVersionAsync(Guid workflowId, int versionNumber, CancellationToken cancellationToken = default)
    {
        var cacheKey = SnapshotCacheKey(workflowId, versionNumber);
        if (_cache.TryGetValue(cacheKey, out AutomationWorkflowSnapshot? cached)) return cached;

        var version = await db.AutomationWorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.WorkflowId == workflowId && item.VersionNumber == versionNumber, cancellationToken);
        var snapshot = version is null
            ? null
            : JsonSerializer.Deserialize<AutomationWorkflowSnapshot>(version.SnapshotJson, SnapshotJsonOptions);
        if (snapshot is not null) _cache.Set(cacheKey, snapshot, TimeSpan.FromHours(1));
        return snapshot;
    }

    public async Task<AutomationExecutionView?> GetExecutionAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execution = await db.AutomationExecutions.AsNoTracking()
            .Include(item => item.NodeExecutions)
            .FirstOrDefaultAsync(item => item.Id == executionId, cancellationToken);
        return execution is null ? null : ToExecutionView(execution);
    }

    public async Task<IReadOnlyList<AutomationRecentFailureView>> ListRecentFailuresAsync(
        int take = 20, CancellationToken cancellationToken = default)
    {
        // FinishedAtUnixSeconds (a plain long) is server-orderable, unlike the DateTimeOffset
        // FinishedAt column it mirrors - see the project-wide SQLite/DateTimeOffset note.
        var failures = await db.AutomationExecutions.AsNoTracking()
            .Where(execution => execution.Status == AutomationExecutionStatuses.Failed)
            .OrderByDescending(execution => execution.FinishedAtUnixSeconds)
            .Take(Math.Clamp(take, 1, 100))
            .Select(execution => new { execution.Id, execution.WorkflowId, execution.Mode, execution.ErrorMessage, execution.FinishedAt })
            .ToListAsync(cancellationToken);
        if (failures.Count == 0) return [];

        var workflowNames = await db.AutomationWorkflows.AsNoTracking()
            .Where(workflow => failures.Select(f => f.WorkflowId).Contains(workflow.Id))
            .Select(workflow => new { workflow.Id, workflow.Name })
            .ToDictionaryAsync(workflow => workflow.Id, workflow => workflow.Name, cancellationToken);

        return failures
            .Select(failure => new AutomationRecentFailureView(
                failure.Id,
                failure.WorkflowId,
                workflowNames.GetValueOrDefault(failure.WorkflowId, "(deleted workflow)"),
                failure.Mode,
                failure.ErrorMessage,
                failure.FinishedAt))
            .ToList();
    }

    public async Task<AutomationPublicStatusView?> GetPublicStatusAsync(Guid workflowId, int take = 10, CancellationToken cancellationToken = default)
    {
        var workflow = await db.AutomationWorkflows.AsNoTracking()
            .Where(item => item.Id == workflowId)
            .Select(item => new { item.Name, item.Description, item.Status, item.LastExecutedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (workflow is null) return null;

        // StartedAtUnixSeconds (a plain long), not StartedAt, for the same SQLite/DateTimeOffset
        // server-ordering reason as ListRecentFailuresAsync above.
        var runs = await db.AutomationExecutions.AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .OrderByDescending(item => item.StartedAtUnixSeconds)
            .Take(Math.Clamp(take, 1, 50))
            .Select(item => new AutomationPublicStatusRun(item.Status, item.Mode, item.StartedAt, item.FinishedAt))
            .ToListAsync(cancellationToken);

        return new AutomationPublicStatusView(workflow.Name, workflow.Description, workflow.Status, workflow.LastExecutedAt, runs);
    }

    public async Task<IReadOnlyList<AutomationWorkflowVersionSummary>> ListVersionsAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var versions = await db.AutomationWorkflowVersions.AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .OrderByDescending(item => item.VersionNumber)
            .Select(item => new { item.VersionNumber, item.CreatedAt, item.ChangeSummary, item.CreatedBy, item.SnapshotJson })
            .ToListAsync(cancellationToken);

        return versions.Select(version =>
        {
            var nodeCount = 0;
            try
            {
                nodeCount = JsonSerializer.Deserialize<AutomationWorkflowSnapshot>(version.SnapshotJson, SnapshotJsonOptions)?.Nodes.Count ?? 0;
            }
            catch (JsonException) { /* A version predating a snapshot-shape change - report 0 rather than fail the whole list. */ }
            return new AutomationWorkflowVersionSummary(version.VersionNumber, version.CreatedAt, version.ChangeSummary, version.CreatedBy, nodeCount);
        }).ToList();
    }

    // A coarse, node/connection-level diff (AutomationNodeSnapshot/AutomationConnectionSnapshot
    // are records, so `!=` is already a full-value comparison) - not a generic property-level
    // JSON diff. Sufficient for "what changed" review without a diff engine.
    public async Task<AutomationWorkflowDiff> DiffVersionsAsync(Guid workflowId, int fromVersion, int toVersion, CancellationToken cancellationToken = default)
    {
        var from = await GetSnapshotByVersionAsync(workflowId, fromVersion, cancellationToken)
            ?? throw new KeyNotFoundException($"Version {fromVersion} was not found.");
        var to = await GetSnapshotByVersionAsync(workflowId, toVersion, cancellationToken)
            ?? throw new KeyNotFoundException($"Version {toVersion} was not found.");

        var fromNodesById = from.Nodes.ToDictionary(node => node.Id);
        var toNodesById = to.Nodes.ToDictionary(node => node.Id);

        var addedNodes = to.Nodes.Where(node => !fromNodesById.ContainsKey(node.Id)).ToList();
        var removedNodes = from.Nodes.Where(node => !toNodesById.ContainsKey(node.Id)).ToList();
        var modifiedNodes = to.Nodes
            .Where(node => fromNodesById.TryGetValue(node.Id, out var before) && before != node)
            .Select(node => new AutomationNodeDiffChange(node.Id, node.Name, fromNodesById[node.Id], node))
            .ToList();

        var fromConnections = from.Connections.ToHashSet();
        var toConnections = to.Connections.ToHashSet();
        var addedConnections = to.Connections.Where(connection => !fromConnections.Contains(connection)).ToList();
        var removedConnections = from.Connections.Where(connection => !toConnections.Contains(connection)).ToList();

        return new AutomationWorkflowDiff(addedNodes, removedNodes, modifiedNodes, addedConnections, removedConnections);
    }

    public async Task RollbackToVersionAsync(Guid workflowId, int targetVersion, string performedBy, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotByVersionAsync(workflowId, targetVersion, cancellationToken)
            ?? throw new KeyNotFoundException($"Version {targetVersion} was not found.");
        var workflow = await db.AutomationWorkflows
            .Include(item => item.Nodes)
            .Include(item => item.Connections)
            .FirstOrDefaultAsync(item => item.Id == workflowId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow was not found.");

        db.AutomationConnections.RemoveRange(workflow.Connections);
        db.AutomationNodes.RemoveRange(workflow.Nodes);

        // Reuses the snapshot's own node/connection ids rather than minting new ones (unlike
        // CreateFromGraphAsync below) - this restores the workflow's draft in place, it isn't
        // creating a new independent workflow, so there's no reason to disturb identities.
        // Position/Notes reset to defaults: the publish snapshot never carried them
        // (AutomationNodeSnapshot has no PositionX/PositionY/Notes fields), so any version
        // predates that data by construction - not a rollback-specific regression.
        foreach (var node in snapshot.Nodes)
        {
            db.AutomationNodes.Add(new AutomationNode
            {
                Id = node.Id,
                WorkflowId = workflowId,
                Name = node.Name,
                TypeKey = node.TypeKey,
                TypeVersion = node.TypeVersion,
                ParametersJson = node.ParametersJson,
                CredentialId = node.CredentialId,
                IsDisabled = node.IsDisabled,
                ContinueOnFail = node.ContinueOnFail,
                RetryOnFail = node.RetryOnFail,
                MaxTries = node.MaxTries,
                WaitBetweenTriesMs = node.WaitBetweenTriesMs,
                TimeoutMs = node.TimeoutMs,
                CreatedBy = performedBy
            });
        }
        foreach (var connection in snapshot.Connections)
        {
            db.AutomationConnections.Add(new AutomationConnection
            {
                WorkflowId = workflowId,
                SourceNodeId = connection.SourceNodeId,
                SourceOutput = connection.SourceOutput,
                TargetNodeId = connection.TargetNodeId,
                TargetInput = connection.TargetInput,
                CreatedBy = performedBy
            });
        }
        Touch(workflow);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Builds a brand-new, independent workflow from a portable node/connection graph - shared by
    // AutomationTemplateService.InstantiateAsync and the export/import endpoint rather than
    // duplicating the remap logic. Always mints fresh node/connection ids (unlike
    // RollbackToVersionAsync above, which restores a specific workflow's own history in place)
    // and never carries CredentialId across, since a credential reference is only meaningful to
    // the workflow/instance that originally held it - the new workflow starts uncredentialed and
    // those nodes need reattaching manually.
    public async Task<AutomationWorkflowView> CreateFromGraphAsync(
        string name,
        string description,
        IReadOnlyList<AutomationNodeView> nodes,
        IReadOnlyList<AutomationConnectionView> connections,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Workflow name is required.", nameof(name));
        if (nodes.Count == 0) throw new ArgumentException("The graph must contain at least one node.", nameof(nodes));

        var workflow = new AutomationWorkflow { Name = name.Trim(), Description = description?.Trim() ?? string.Empty, CreatedBy = performedBy };
        db.AutomationWorkflows.Add(workflow);

        var idMap = new Dictionary<Guid, Guid>();
        foreach (var node in nodes)
        {
            if (nodeRegistry.Find(node.TypeKey, node.TypeVersion) is null)
                throw new InvalidOperationException($"Node type '{node.TypeKey}' version {node.TypeVersion} is not registered.");
            var newId = Guid.NewGuid();
            idMap[node.Id] = newId;
            db.AutomationNodes.Add(new AutomationNode
            {
                Id = newId,
                WorkflowId = workflow.Id,
                Name = node.Name,
                TypeKey = node.TypeKey,
                TypeVersion = node.TypeVersion,
                PositionX = Math.Clamp(node.PositionX, 0, 5000),
                PositionY = Math.Clamp(node.PositionY, 0, 5000),
                ParametersJson = string.IsNullOrWhiteSpace(node.ParametersJson) ? "{}" : node.ParametersJson,
                CredentialId = null,
                IsDisabled = node.IsDisabled,
                ContinueOnFail = node.ContinueOnFail,
                RetryOnFail = node.RetryOnFail,
                MaxTries = Math.Clamp(node.MaxTries, 1, 10),
                WaitBetweenTriesMs = Math.Clamp(node.WaitBetweenTriesMs, 0, 60_000),
                TimeoutMs = Math.Clamp(node.TimeoutMs, 0, 600_000),
                Notes = node.Notes,
                CreatedBy = performedBy
            });
        }
        foreach (var connection in connections)
        {
            if (!idMap.TryGetValue(connection.SourceNodeId, out var sourceId) || !idMap.TryGetValue(connection.TargetNodeId, out var targetId)) continue;
            db.AutomationConnections.Add(new AutomationConnection
            {
                WorkflowId = workflow.Id,
                SourceNodeId = sourceId,
                SourceOutput = connection.SourceOutput,
                TargetNodeId = targetId,
                TargetInput = connection.TargetInput,
                CreatedBy = performedBy
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAsync(workflow.Id, cancellationToken))!;
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
            node.IsDisabled, node.ContinueOnFail, node.RetryOnFail, node.MaxTries, node.WaitBetweenTriesMs, node.TimeoutMs)).ToList(),
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
        AllowDownstreamAutomationTriggers = workflow.AllowDownstreamAutomationTriggers,
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
        node.RetryOnFail, node.MaxTries, node.WaitBetweenTriesMs, node.TimeoutMs, node.Notes);

    private static AutomationConnectionView ToConnectionView(AutomationConnection connection) => new(
        connection.Id, connection.SourceNodeId, connection.SourceOutput, connection.TargetNodeId, connection.TargetInput);

    internal static AutomationExecutionView ToExecutionView(AutomationExecution execution) => new(
        execution.Id, execution.WorkflowId, execution.WorkflowVersion, execution.Mode, execution.Status,
        execution.InputJson, execution.OutputJson, execution.ErrorMessage, execution.StartedAt, execution.FinishedAt,
        execution.WaitingNodeId.HasValue
            ? new AutomationWaitStatus(execution.WaitingNodeTypeKey ?? string.Empty, execution.WaitingNodeName ?? string.Empty, execution.ResumeAt)
            : null,
        execution.NodeExecutions.OrderBy(node => node.StartedAtUnixSeconds).Select(node => new AutomationNodeExecutionView(
            node.Id, node.NodeId, node.NodeName, node.NodeTypeKey, node.Status, node.Attempt,
            node.InputJson, node.OutputJson, node.ErrorMessage, node.StartedAt, node.FinishedAt, node.IsSimulated)).ToList());
}
