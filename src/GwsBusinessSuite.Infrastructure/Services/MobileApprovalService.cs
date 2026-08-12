using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Mobile;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class MobileApprovalService(IAppDbContext db) : IMobileApprovalService
{
    public async Task<IReadOnlyList<MobilePendingApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default)
    {
        // StartedAtUnixSeconds, not StartedAt, for the same SQLite/DateTimeOffset server-ordering
        // reason as AutomationWorkflowService.ListRecentFailuresAsync. Approximates "waiting
        // since" with the execution's overall start time (there is no separate "entered Waiting"
        // timestamp) - close enough for a v1 pending-approvals list.
        var pending = await db.AutomationExecutions.AsNoTracking()
            .Where(item => item.Status == AutomationExecutionStatuses.Waiting && item.WaitingNodeTypeKey == "core.approval")
            .OrderBy(item => item.StartedAtUnixSeconds)
            .Select(item => new { item.Id, item.WorkflowId, item.WaitingNodeName, item.StartedAt })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return [];

        var workflowNames = await db.AutomationWorkflows.AsNoTracking()
            .Where(workflow => pending.Select(item => item.WorkflowId).Contains(workflow.Id))
            .Select(workflow => new { workflow.Id, workflow.Name })
            .ToDictionaryAsync(workflow => workflow.Id, workflow => workflow.Name, cancellationToken);

        return pending.Select(item => new MobilePendingApprovalView(
            item.Id, item.WorkflowId, workflowNames.GetValueOrDefault(item.WorkflowId, "(deleted workflow)"),
            item.WaitingNodeName ?? "Approval", item.StartedAt)).ToList();
    }
}
