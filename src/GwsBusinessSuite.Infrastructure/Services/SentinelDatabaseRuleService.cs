using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelDatabaseRuleService(
    IAppDbContext dbContext,
    IAutomationWorkflowService workflows,
    IWikiDatabaseService databases) : ISentinelDatabaseRuleService
{
    public async Task<IReadOnlyList<SentinelDatabaseRuleView>> ListAsync(Guid wikiDatabaseId, CancellationToken cancellationToken = default) =>
        (await workflows.ListAsync(cancellationToken))
            .Select(workflow => SentinelDatabaseRuleBuilder.SummaryFromDescription(workflow.Description, wikiDatabaseId) is { } summary
                ? new SentinelDatabaseRuleView(workflow.Id, summary, workflow.Status)
                : null)
            .OfType<SentinelDatabaseRuleView>()
            .ToList();

    public async Task<SentinelDatabaseRuleView> CreateAsync(SentinelDatabaseRule rule, string performedBy, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(performedBy, cancellationToken);
        var database = await databases.GetDatabaseAsync(rule.WikiDatabaseId, cancellationToken)
            ?? throw new KeyNotFoundException("The database no longer exists.");
        if (SentinelDatabaseRuleBuilder.Validate(rule, database) is { } problem) throw new InvalidOperationException(problem);

        string? workflowName = null;
        if (rule.Then == SentinelDatabaseRuleKinds.ThenRunWorkflow)
        {
            workflowName = (await workflows.ListAsync(cancellationToken)).FirstOrDefault(workflow => workflow.Id == rule.RunWorkflowId)?.Name
                ?? throw new InvalidOperationException("That workflow no longer exists.");
        }

        var graph = SentinelDatabaseRuleBuilder.Build(rule, database, workflowName);
        var created = await workflows.CreateFromGraphAsync(graph.Name, graph.Description, graph.Nodes, graph.Connections, performedBy, cancellationToken);
        try
        {
            await workflows.PublishAsync(created.Id, "Created from the database's Automations panel", cancellationToken);
            await workflows.SetActiveAsync(created.Id, true, cancellationToken);
        }
        catch
        {
            // Don't leave a half-made rule behind: it would show in the panel but never run.
            await workflows.DeleteWorkflowAsync(created.Id, cancellationToken);
            throw;
        }

        return new SentinelDatabaseRuleView(created.Id, SentinelDatabaseRuleBuilder.Describe(rule, database, workflowName), AutomationWorkflowStatuses.Active);
    }

    public async Task DeleteAsync(Guid wikiDatabaseId, Guid workflowId, string performedBy, CancellationToken cancellationToken = default)
    {
        await EnsureAdminAsync(performedBy, cancellationToken);
        // Only a rule of this database: the panel must never be a way to delete other workflows.
        var rule = (await ListAsync(wikiDatabaseId, cancellationToken)).FirstOrDefault(item => item.WorkflowId == workflowId);
        if (rule is null) return;
        await workflows.DeleteWorkflowAsync(workflowId, cancellationToken);
    }

    private async Task EnsureAdminAsync(string username, CancellationToken cancellationToken)
    {
        var isAdmin = await dbContext.AppUsers.AsNoTracking()
            .AnyAsync(user => user.Username == username && user.IsActive && user.Role == AppRoles.Admin, cancellationToken);
        if (!isAdmin) throw new UnauthorizedAccessException("Only Admins can create or remove database automations.");
    }
}
