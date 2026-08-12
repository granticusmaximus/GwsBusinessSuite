using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Automation;

// Mirrors SentinelTemplateService's pattern (Infrastructure/Services/SentinelTemplateService.cs):
// capture a point-in-time snapshot of the source, mint fresh identities whenever it's
// instantiated so the result is fully independent of both the source workflow and every other
// instantiation. Unlike AutomationWorkflowVersion's publish snapshot (which drops canvas
// position/notes), a template's SnapshotJson serializes the editor views - AutomationNodeView /
// AutomationConnectionView - which retain them, and captures the live draft rather than the
// last published version.
public sealed class AutomationTemplateService(
    IAppDbContext db,
    IAutomationWorkflowService workflowService) : IAutomationTemplateService
{
    private static readonly JsonSerializerOptions GraphJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AutomationWorkflowTemplateSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var templates = await db.AutomationWorkflowTemplates.AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new { item.Id, item.Name, item.Description, item.TagsCsv, item.SnapshotJson, item.UpdatedAt, item.CreatedAt })
            .ToListAsync(cancellationToken);

        return templates.Select(template =>
        {
            var nodeCount = 0;
            try { nodeCount = JsonSerializer.Deserialize<AutomationWorkflowGraphPackage>(template.SnapshotJson, GraphJsonOptions)?.Nodes.Count ?? 0; }
            catch (JsonException) { /* A malformed/stale row should still list, just with an unknown node count. */ }
            return new AutomationWorkflowTemplateSummary(
                template.Id, template.Name, template.Description, template.TagsCsv, nodeCount, template.UpdatedAt ?? template.CreatedAt);
        }).ToList();
    }

    public async Task<Guid> CreateFromWorkflowAsync(
        Guid workflowId, string name, string description, string performedBy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Template name is required.", nameof(name));
        var normalizedName = NormalizeName(name);
        if (await db.AutomationWorkflowTemplates.AsNoTracking().AnyAsync(item => item.NormalizedName == normalizedName, cancellationToken))
            throw new InvalidOperationException($"A template named '{name.Trim()}' already exists.");

        // Captures the live draft (not the last published snapshot) - a template should reflect
        // whatever the author is currently looking at in the editor, published or not.
        var workflow = await workflowService.GetAsync(workflowId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow was not found.");
        if (workflow.Nodes.Count == 0) throw new InvalidOperationException("This workflow has no nodes to save as a template.");

        var package = new AutomationWorkflowGraphPackage(workflow.Name, workflow.Description, workflow.Nodes, workflow.Connections);
        var template = new AutomationWorkflowTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            Description = description?.Trim() ?? string.Empty,
            SnapshotJson = JsonSerializer.Serialize(package, GraphJsonOptions),
            CreatedBy = performedBy
        };
        db.AutomationWorkflowTemplates.Add(template);
        await db.SaveChangesAsync(cancellationToken);
        return template.Id;
    }

    public async Task<AutomationWorkflowView> InstantiateAsync(
        Guid templateId, string newWorkflowName, string performedBy, CancellationToken cancellationToken = default)
    {
        var template = await db.AutomationWorkflowTemplates.AsNoTracking().FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new KeyNotFoundException("Template was not found.");
        var package = JsonSerializer.Deserialize<AutomationWorkflowGraphPackage>(template.SnapshotJson, GraphJsonOptions)
            ?? throw new InvalidOperationException("This template's saved graph could not be read.");

        var name = string.IsNullOrWhiteSpace(newWorkflowName) ? template.Name : newWorkflowName.Trim();
        return await workflowService.CreateFromGraphAsync(name, package.Description, package.Nodes, package.Connections, performedBy, cancellationToken);
    }

    public async Task DeleteTemplateAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await db.AutomationWorkflowTemplates.FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new KeyNotFoundException("Template was not found.");
        db.AutomationWorkflowTemplates.Remove(template);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException("A template name is required.", nameof(name));
        return trimmed.ToLowerInvariant();
    }
}
