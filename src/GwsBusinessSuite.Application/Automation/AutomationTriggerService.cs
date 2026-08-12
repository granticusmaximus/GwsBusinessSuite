using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationTriggerService(
    IAppDbContext db,
    IAutomationWorkflowService workflowService,
    IAutomationExecutionService executionService,
    IAutomationCredentialService credentialService,
    TimeProvider timeProvider,
    ILogger<AutomationTriggerService> logger) : IAutomationTriggerService
{
    private static readonly SemaphoreSlim ScheduleLock = new(1, 1);
    private static readonly SemaphoreSlim ResumeLock = new(1, 1);

    public async Task<AutomationExecutionView?> TriggerWebhookAsync(
        string path,
        string inputJson,
        string? providedSecret,
        CancellationToken cancellationToken = default)
    {
        var workflow = await db.AutomationWorkflows.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Status == AutomationWorkflowStatuses.Active && item.WebhookPath == path, cancellationToken);
        if (workflow is null) return null;

        var snapshot = await workflowService.GetPublishedSnapshotAsync(workflow.Id, cancellationToken)
            ?? throw new InvalidOperationException("The active workflow has no published version.");
        var trigger = snapshot.Nodes.FirstOrDefault(node => node.TypeKey == "core.webhookTrigger" && !node.IsDisabled)
            ?? throw new InvalidOperationException("The active workflow has no enabled Webhook Trigger.");
        if (trigger.CredentialId.HasValue)
        {
            var credentialJson = await credentialService.GetDecryptedDataAsync(trigger.CredentialId.Value, cancellationToken)
                ?? throw new UnauthorizedAccessException("Webhook credential is unavailable.");
            using var document = JsonDocument.Parse(credentialJson);
            var requiredSecret = document.RootElement.TryGetProperty("secret", out var value) ? value.GetString() : null;
            if (!FixedTimeEquals(requiredSecret, providedSecret)) throw new UnauthorizedAccessException("Webhook secret is invalid.");
        }
        else
        {
            // Intentional n8n-style design: the webhook response echoes the execution's full
            // output back to the caller. Without a secret credential attached, that response -
            // and whatever data the workflow's nodes touched - is readable by anyone who knows
            // or guesses this path, with zero auth check. Not something to silently redesign
            // (a secret-less webhook is a legitimate choice for a workflow with nothing
            // sensitive to leak), but an author who forgot to attach one gets no signal
            // anything is exposed - this at least makes it discoverable.
            logger.LogWarning(
                "Automation webhook {Path} for workflow {WorkflowId} fired with no secret credential attached - " +
                "its response (including full execution output) is readable by anyone who can reach this URL.",
                path, workflow.Id);
        }

        return await executionService.ExecuteAsync(
            workflow.Id, inputJson, AutomationExecutionModes.Webhook, cancellationToken: cancellationToken);
    }

    public async Task<int> RunDueSchedulesAsync(CancellationToken cancellationToken = default)
    {
        if (!await ScheduleLock.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            var now = timeProvider.GetUtcNow();
            var nowUnix = now.ToUnixTimeSeconds();
            var due = await db.AutomationWorkflows.Where(item =>
                item.Status == AutomationWorkflowStatuses.Active
                && (item.ScheduleIntervalMinutes != null || item.ScheduleCronExpression != null)
                && item.NextScheduledAtUnixSeconds != null
                && item.NextScheduledAtUnixSeconds <= nowUnix).ToListAsync(cancellationToken);
            foreach (var workflow in due)
            {
                workflow.NextScheduledAt = workflow.ScheduleCronExpression is not null
                    ? CronSchedule.GetNextOccurrence(workflow.ScheduleCronExpression, now)
                    : now.AddMinutes(workflow.ScheduleIntervalMinutes!.Value);
                workflow.NextScheduledAtUnixSeconds = workflow.NextScheduledAt.Value.ToUnixTimeSeconds();
                workflow.UpdatedAt = now;
                workflow.UpdatedBy = "automation-scheduler";
            }
            if (due.Count > 0) await db.SaveChangesAsync(cancellationToken);

            foreach (var workflow in due)
            {
                var input = JsonSerializer.Serialize(new { scheduledAt = now, workflowId = workflow.Id });
                await executionService.ExecuteAsync(
                    workflow.Id, input, AutomationExecutionModes.Schedule, cancellationToken: cancellationToken);
            }
            return due.Count;
        }
        finally { ScheduleLock.Release(); }
    }

    public async Task<int> ResumeDueWaitsAsync(CancellationToken cancellationToken = default)
    {
        if (!await ResumeLock.WaitAsync(0, cancellationToken)) return 0;
        try
        {
            var nowUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var orphanCutoff = nowUnix - AutomationExecutionService.OrphanThresholdSeconds;
            var due = await db.AutomationExecutions.AsNoTracking().Where(item =>
                (item.Status == AutomationExecutionStatuses.Waiting
                    && item.ResumeAtUnixSeconds != null && item.ResumeAtUnixSeconds <= nowUnix)
                || (item.Status == AutomationExecutionStatuses.Running
                    && item.HeartbeatAtUnixSeconds != null && item.HeartbeatAtUnixSeconds < orphanCutoff))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);

            var resumed = 0;
            foreach (var executionId in due)
            {
                try { await executionService.ResumeAsync(executionId, cancellationToken: cancellationToken); resumed++; }
                catch (InvalidOperationException) { /* execution moved on (e.g. canceled) between the sweep query and resume */ }
            }
            return resumed;
        }
        finally { ResumeLock.Release(); }
    }

    public async Task<int> TriggerDatabaseRowChangedAsync(Guid wikiDatabaseId, string inputJson, CancellationToken cancellationToken = default)
    {
        var subscribers = await db.AutomationWorkflows.AsNoTracking().Where(item =>
            item.Status == AutomationWorkflowStatuses.Active
            && item.TriggerWikiDatabaseId == wikiDatabaseId).Select(item => item.Id).ToListAsync(cancellationToken);

        var triggered = 0;
        foreach (var workflowId in subscribers)
        {
            try
            {
                // Published snapshots are memory-cached indefinitely per (workflowId, version)
                // (AutomationWorkflowService's own cache comment), so this per-subscriber lookup
                // is cheap in the common case of the same workflow firing repeatedly.
                var snapshot = await workflowService.GetPublishedSnapshotAsync(workflowId, cancellationToken);
                var triggerNode = snapshot?.Nodes.FirstOrDefault(node => node.TypeKey == "database.rowChangedTrigger" && !node.IsDisabled);
                if (triggerNode is not null && !DatabaseTriggerConditions.Matches(triggerNode.ParametersJson, inputJson)) continue;

                await executionService.ExecuteAsync(workflowId, inputJson, AutomationExecutionModes.DatabaseTrigger, cancellationToken: cancellationToken);
                triggered++;
            }
            catch (Exception ex)
            {
                // A single workflow's own failure (missing published version, disabled
                // trigger node removed after publish, etc.) is visible in that workflow's own
                // Executions tab where relevant, but must not stop the row save that reached
                // here, nor stop other subscribed workflows from still running.
                logger.LogWarning(ex, "Database row-changed trigger failed for automation workflow {WorkflowId}.", workflowId);
            }
        }
        return triggered;
    }

    public async Task<int> TriggerCrmDealStageChangedAsync(string stage, string inputJson, CancellationToken cancellationToken = default)
    {
        var subscribers = await db.AutomationWorkflows.AsNoTracking().Where(item =>
            item.Status == AutomationWorkflowStatuses.Active && item.TriggerCrmDealStageChanged)
            .Select(item => item.Id).ToListAsync(cancellationToken);

        var triggered = 0;
        foreach (var workflowId in subscribers)
        {
            try
            {
                var snapshot = await workflowService.GetPublishedSnapshotAsync(workflowId, cancellationToken);
                var triggerNode = snapshot?.Nodes.FirstOrDefault(node => node.TypeKey == "crm.dealStageChangedTrigger" && !node.IsDisabled);
                if (triggerNode is null) continue;
                var toStage = ReadStringParameter(triggerNode.ParametersJson, "toStage");
                if (!string.IsNullOrWhiteSpace(toStage) && !string.Equals(toStage, stage, StringComparison.OrdinalIgnoreCase)) continue;

                await executionService.ExecuteAsync(workflowId, inputJson, AutomationExecutionModes.CrmDealStageChanged, cancellationToken: cancellationToken);
                triggered++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CRM deal-stage-changed trigger failed for automation workflow {WorkflowId}.", workflowId);
            }
        }
        return triggered;
    }

    public async Task<int> TriggerCmsPagePublishedAsync(Guid siteId, string inputJson, CancellationToken cancellationToken = default)
    {
        var subscribers = await db.AutomationWorkflows.AsNoTracking().Where(item =>
            item.Status == AutomationWorkflowStatuses.Active && item.TriggerCmsPagePublished)
            .Select(item => item.Id).ToListAsync(cancellationToken);

        var triggered = 0;
        foreach (var workflowId in subscribers)
        {
            try
            {
                var snapshot = await workflowService.GetPublishedSnapshotAsync(workflowId, cancellationToken);
                var triggerNode = snapshot?.Nodes.FirstOrDefault(node => node.TypeKey == "cms.pagePublishedTrigger" && !node.IsDisabled);
                if (triggerNode is null) continue;

                await executionService.ExecuteAsync(workflowId, inputJson, AutomationExecutionModes.CmsPagePublished, cancellationToken: cancellationToken);
                triggered++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "CMS page-published trigger failed for automation workflow {WorkflowId}.", workflowId);
            }
        }
        return triggered;
    }

    private static string? ReadStringParameter(string parametersJson, string propertyName)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson);
        return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public async Task<AutomationExecutionView?> ResumeViaWebhookAsync(string token, string bodyJson, CancellationToken cancellationToken = default)
    {
        var execution = await db.AutomationExecutions.AsNoTracking().FirstOrDefaultAsync(item =>
            item.Status == AutomationExecutionStatuses.Waiting && item.ResumeToken == token, cancellationToken);
        if (execution is null) return null;
        if (execution.WaitingNodeTypeKey != "core.wait")
            throw new InvalidOperationException("This execution is not waiting on a resume webhook.");

        using var body = JsonDocument.Parse(string.IsNullOrWhiteSpace(bodyJson) ? "{}" : bodyJson);
        var mergeFields = JsonSerializer.Serialize(new { _resume = body.RootElement });
        return await executionService.ResumeAsync(execution.Id, "main", mergeFields, cancellationToken);
    }

    private static bool FixedTimeEquals(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
