using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

public sealed class AutomationTriggerService(
    IAppDbContext db,
    IAutomationWorkflowService workflowService,
    IAutomationExecutionService executionService,
    IAutomationCredentialService credentialService,
    TimeProvider timeProvider) : IAutomationTriggerService
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
                && item.ScheduleIntervalMinutes != null
                && item.NextScheduledAtUnixSeconds != null
                && item.NextScheduledAtUnixSeconds <= nowUnix).ToListAsync(cancellationToken);
            foreach (var workflow in due)
            {
                workflow.NextScheduledAt = now.AddMinutes(workflow.ScheduleIntervalMinutes!.Value);
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
