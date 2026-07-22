using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelTemplateService(
    IAppDbContext dbContext,
    IWikiService wikiService,
    IWikiDatabaseService wikiDatabaseService)
    : ISentinelTemplateService
{
    public async Task<IReadOnlyList<SentinelPageTemplateView>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await dbContext.SentinelPageTemplates
            .AsNoTracking()
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);

        return templates.Select(ToView).ToList();
    }

    public async Task<SentinelPageTemplateView> CreateFromPageAsync(
        Guid wikiPageId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var page = await dbContext.WikiPages
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel page no longer exists.");

        if (await dbContext.SentinelPageTemplates.AnyAsync(
                template => template.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException("A Sentinel template with that name already exists.");
        }

        var template = new SentinelPageTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            PageTitle = page.Title,
            BlocksJson = page.BlocksJson,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            CreatedBy = NormalizeUser(performedBy)
        };

        await dbContext.SentinelPageTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(template);
    }

    public async Task<WikiPage> CreatePageAsync(
        Guid templateId,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelPageTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel template no longer exists.");

        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson)
            .Select(block => block with { Id = Guid.NewGuid() })
            .ToList();

        return await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = template.PageTitle,
            BlocksJson = WikiBlockJson.Serialize(blocks),
            Icon = template.Icon,
            CoverImageUrl = template.CoverImageUrl,
            ParentWikiPageId = parentWikiPageId
        }, NormalizeUser(performedBy), cancellationToken);
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelPageTemplates
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null) return;

        dbContext.SentinelPageTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelBlockTemplateView>> ListBlockTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await dbContext.SentinelBlockTemplates
            .AsNoTracking()
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);
        return templates.Select(ToBlockView).ToList();
    }

    public async Task<SentinelBlockTemplateView> CreateBlockTemplateAsync(
        string name,
        string blocksJson,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        var blocks = WikiBlockJson.ParseBlocks(blocksJson);
        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("A block template must contain at least one block.");
        }
        if (await dbContext.SentinelBlockTemplates.AnyAsync(
                template => template.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException("A Sentinel block template with that name already exists.");
        }

        var template = new SentinelBlockTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            BlocksJson = WikiBlockJson.Serialize(blocks),
            CreatedBy = NormalizeUser(performedBy)
        };
        await dbContext.SentinelBlockTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToBlockView(template);
    }

    public async Task<string> MaterializeBlockTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelBlockTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel block template no longer exists.");
        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson)
            .Select(block => block with { Id = Guid.NewGuid() })
            .ToList();
        return WikiBlockJson.Serialize(blocks);
    }

    public async Task DeleteBlockTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelBlockTemplates
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null) return;
        dbContext.SentinelBlockTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelDatabaseTemplateView>> ListDatabaseTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        var templates = await dbContext.SentinelDatabaseTemplates
            .AsNoTracking()
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);
        return templates.Select(ToDatabaseView).ToList();
    }

    public async Task<SentinelDatabaseTemplateView> CreateFromDatabaseAsync(
        Guid wikiDatabaseId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeName(name);
        if (await dbContext.SentinelDatabaseTemplates.AnyAsync(
                template => template.NormalizedName == normalizedName, cancellationToken))
        {
            throw new InvalidOperationException("A Sentinel database template with that name already exists.");
        }

        var snapshot = await wikiDatabaseService.CreateTemplateSnapshotAsync(wikiDatabaseId, cancellationToken);
        var template = new SentinelDatabaseTemplate
        {
            Name = name.Trim(),
            NormalizedName = normalizedName,
            DatabaseTitle = snapshot.Title,
            Icon = snapshot.Icon,
            SnapshotJson = JsonSerializer.Serialize(snapshot, WikiPropertyValues.Options),
            CreatedBy = NormalizeUser(performedBy)
        };
        await dbContext.SentinelDatabaseTemplates.AddAsync(template, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDatabaseView(template);
    }

    public async Task<WikiDatabase> CreateDatabaseAsync(
        Guid templateId,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelDatabaseTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken)
            ?? throw new InvalidOperationException("The Sentinel database template no longer exists.");
        var snapshot = JsonSerializer.Deserialize<WikiDatabaseTemplateSnapshot>(
                template.SnapshotJson, WikiPropertyValues.Options)
            ?? throw new InvalidOperationException("The Sentinel database template snapshot is invalid.");
        return await wikiDatabaseService.CreateDatabaseFromTemplateAsync(
            snapshot, parentWikiPageId, NormalizeUser(performedBy), cancellationToken);
    }

    public async Task DeleteDatabaseTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        var template = await dbContext.SentinelDatabaseTemplates
            .FirstOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null) return;
        dbContext.SentinelDatabaseTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static SentinelPageTemplateView ToView(SentinelPageTemplate template) => new(
        template.Id,
        template.Name,
        template.PageTitle,
        template.Icon,
        WikiBlockJson.ParseBlocks(template.BlocksJson).Count,
        template.CreatedAt,
        template.CreatedBy);

    private static SentinelDatabaseTemplateView ToDatabaseView(SentinelDatabaseTemplate template)
    {
        var snapshot = JsonSerializer.Deserialize<WikiDatabaseTemplateSnapshot>(
                template.SnapshotJson, WikiPropertyValues.Options)
            ?? new WikiDatabaseTemplateSnapshot(template.DatabaseTitle, template.Icon, [], [], []);
        return new SentinelDatabaseTemplateView(
            template.Id,
            template.Name,
            template.DatabaseTitle,
            template.Icon,
            snapshot.Properties.Count,
            snapshot.Rows.Count,
            snapshot.Views.Count,
            template.CreatedAt,
            template.CreatedBy);
    }

    private static SentinelBlockTemplateView ToBlockView(SentinelBlockTemplate template)
    {
        var blocks = WikiBlockJson.ParseBlocks(template.BlocksJson);
        var preview = string.Join(" · ", blocks
            .Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 48))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Take(2));
        return new SentinelBlockTemplateView(
            template.Id,
            template.Name,
            blocks.Count,
            string.IsNullOrWhiteSpace(preview) ? "Structured blocks" : preview,
            template.CreatedAt,
            template.CreatedBy);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A template name is required.", nameof(name));
        }
        if (trimmed.Length > 120)
        {
            throw new ArgumentException("Template names cannot exceed 120 characters.", nameof(name));
        }

        return trimmed.ToUpperInvariant();
    }

    private static string NormalizeUser(string performedBy) =>
        string.IsNullOrWhiteSpace(performedBy) ? "unknown" : performedBy.Trim().ToLowerInvariant();
}
