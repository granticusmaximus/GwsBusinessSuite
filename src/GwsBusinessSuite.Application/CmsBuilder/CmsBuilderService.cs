using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class CmsBuilderService(IAppDbContext dbContext) : ICmsBuilderService
{
    private static readonly IReadOnlyList<CmsWorkflowBlueprintSummary> WorkflowBlueprints =
    [
        new()
        {
            Key = "landing-conversion",
            Name = "Landing Page Conversion",
            Category = "Marketing",
            Description = "Hero, proof, feature stack, and CTA funnel workflow."
        },
        new()
        {
            Key = "blog-editorial",
            Name = "Blog Editorial",
            Category = "Publishing",
            Description = "Editorial blog workflow with header, toc, content, and author box."
        },
        new()
        {
            Key = "product-launch",
            Name = "Product Launch",
            Category = "Commerce",
            Description = "Pre-launch and launch-day conversion workflow blocks."
        },
        new()
        {
            Key = "service-business",
            Name = "Service Business",
            Category = "Local Business",
            Description = "Lead generation workflow with service cards, trust, and booking CTA."
        }
    ];

    private static readonly Dictionary<string, string> WorkflowBlueprintBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["landing-conversion"] =
            "[{\"type\":\"hero\",\"title\":\"Build Your Next Growth Funnel\",\"subtitle\":\"A focused landing workflow for conversion.\",\"primaryCta\":\"Start Free Trial\"},{\"type\":\"proof-grid\",\"title\":\"Trusted by teams\",\"items\":[\"Case Study One\",\"Case Study Two\",\"Case Study Three\"]},{\"type\":\"feature-stack\",\"title\":\"What you get\",\"items\":[\"Automation\",\"Analytics\",\"Integrations\"]},{\"type\":\"cta\",\"title\":\"Ready to launch?\",\"button\":\"Book a Demo\"}]",
        ["blog-editorial"] =
            "[{\"type\":\"article-header\",\"title\":\"Editorial Title\",\"subtitle\":\"Strong angle and reader promise.\"},{\"type\":\"toc\",\"title\":\"In this article\"},{\"type\":\"rich-content\",\"body\":\"## Section\\nLong-form editorial content goes here.\"},{\"type\":\"author-box\",\"name\":\"GWS Editorial\",\"role\":\"Editor\"},{\"type\":\"newsletter-cta\",\"title\":\"Get weekly updates\",\"button\":\"Subscribe\"}]",
        ["product-launch"] =
            "[{\"type\":\"hero\",\"title\":\"Product Launch Week\",\"subtitle\":\"Ship your message with precision.\",\"primaryCta\":\"Join Waitlist\"},{\"type\":\"countdown\",\"title\":\"Launch countdown\",\"days\":7},{\"type\":\"pricing-table\",\"plans\":[\"Starter\",\"Pro\",\"Scale\"]},{\"type\":\"faq\",\"title\":\"Questions answered\"}]",
        ["service-business"] =
            "[{\"type\":\"hero\",\"title\":\"Professional Services\",\"subtitle\":\"Built for outcomes and trust.\",\"primaryCta\":\"Schedule Consultation\"},{\"type\":\"service-list\",\"items\":[\"Discovery\",\"Implementation\",\"Support\"]},{\"type\":\"testimonials\",\"title\":\"Client results\"},{\"type\":\"contact-form\",\"title\":\"Get your plan\"}]"
    };

    public async Task<IReadOnlyList<CmsSite>> ListSitesAsync(CancellationToken cancellationToken = default)
    {
        var sites = await dbContext.CmsSites
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return sites
            .OrderByDescending(site => site.UpdatedAt ?? site.CreatedAt)
            .ThenBy(site => site.Name)
            .ToList();
    }

    public async Task<CmsSite?> GetSiteAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        return await dbContext.CmsSites
            .AsNoTracking()
            .FirstOrDefaultAsync(site => site.Id == siteId, cancellationToken);
    }

    public async Task<CmsSite?> GetSiteBySlugAsync(string siteSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug))
        {
            return null;
        }

        return await dbContext.CmsSites
            .AsNoTracking()
            .FirstOrDefaultAsync(site => site.Slug == siteSlug, cancellationToken);
    }

    public async Task<CmsSite> SaveSiteAsync(CmsSiteEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        var site = editor.SiteId is { } siteId
            ? await dbContext.CmsSites.FirstOrDefaultAsync(item => item.Id == siteId, cancellationToken)
            : null;

        var isNew = site is null;
        site ??= new CmsSite
        {
            Name = string.Empty,
            Slug = string.Empty,
            Theme = "Default",
            CreatedAt = now,
            CreatedBy = "cms-ui"
        };

        var requestedSlug = string.IsNullOrWhiteSpace(editor.Slug)
            ? CreateSlug(editor.Name)
            : CreateSlug(editor.Slug);
        var uniqueSlug = await GetUniqueSiteSlugAsync(requestedSlug, site.Id, cancellationToken);

        site.Name = editor.Name.Trim();
        site.Slug = uniqueSlug;
        site.Theme = string.IsNullOrWhiteSpace(editor.Theme) ? "Default" : editor.Theme.Trim();
        site.UpdatedAt = now;
        site.UpdatedBy = "cms-ui";

        if (isNew)
        {
            await dbContext.CmsSites.AddAsync(site, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return site;
    }

    public async Task DeleteSiteAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var site = await dbContext.CmsSites.FirstOrDefaultAsync(item => item.Id == siteId, cancellationToken);
        if (site is null)
        {
            return;
        }

        var pages = await dbContext.CmsPages
            .Where(page => page.SiteId == siteId)
            .ToListAsync(cancellationToken);

        if (pages.Count > 0)
        {
            dbContext.CmsPages.RemoveRange(pages);
        }

        dbContext.CmsSites.Remove(site);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CmsPage>> ListPagesAsync(Guid? siteId = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CmsPages.AsNoTracking();
        if (siteId is { } actualSiteId)
        {
            query = query.Where(page => page.SiteId == actualSiteId);
        }

        var pages = await query.ToListAsync(cancellationToken);
        return pages
            .OrderByDescending(page => page.UpdatedAt ?? page.CreatedAt)
            .ThenBy(page => page.Title)
            .ToList();
    }

    public async Task<CmsPage?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        return await dbContext.CmsPages
            .AsNoTracking()
            .FirstOrDefaultAsync(page => page.Id == pageId, cancellationToken);
    }

    public async Task<CmsPage?> GetPageBySlugAsync(Guid siteId, string pageSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pageSlug))
        {
            return null;
        }

        return await dbContext.CmsPages
            .AsNoTracking()
            .FirstOrDefaultAsync(page => page.SiteId == siteId && page.Slug == pageSlug, cancellationToken);
    }

    public async Task<CmsPage> SavePageAsync(CmsPageEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        if (editor.SiteId is not { } siteId)
        {
            throw new InvalidOperationException("Select a CMS site before creating or editing a page.");
        }

        var siteExists = await dbContext.CmsSites.AnyAsync(site => site.Id == siteId, cancellationToken);
        if (!siteExists)
        {
            throw new InvalidOperationException("The selected CMS site no longer exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var page = editor.PageId is { } pageId
            ? await dbContext.CmsPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken)
            : null;

        var isNew = page is null;
        page ??= new CmsPage
        {
            SiteId = siteId,
            Title = string.Empty,
            Slug = string.Empty,
            BlocksJson = "[]",
            CreatedAt = now,
            CreatedBy = "cms-ui"
        };

        var requestedSlug = string.IsNullOrWhiteSpace(editor.Slug)
            ? CreateSlug(editor.Title)
            : CreateSlug(editor.Slug);
        var uniqueSlug = await GetUniquePageSlugAsync(siteId, requestedSlug, page.Id, cancellationToken);

        page.SiteId = siteId;
        page.Title = editor.Title.Trim();
        page.Slug = uniqueSlug;
        page.BlocksJson = NormalizeBlocksJson(editor.BlocksJson);
        page.MetaTitle = editor.MetaTitle?.Trim() ?? string.Empty;
        page.MetaDescription = editor.MetaDescription?.Trim() ?? string.Empty;
        page.OgImageUrl = editor.OgImageUrl?.Trim() ?? string.Empty;
        page.UpdatedAt = now;
        page.UpdatedBy = "cms-ui";

        if (isNew)
        {
            await dbContext.CmsPages.AddAsync(page, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task DeletePageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.CmsPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        dbContext.CmsPages.Remove(page);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<CmsWorkflowBlueprintSummary>> ListWorkflowBlueprintsAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<CmsWorkflowBlueprintSummary>>(WorkflowBlueprints);
    }

    public async Task<CmsPage> ApplyWorkflowBlueprintAsync(
        Guid pageId,
        string blueprintKey,
        bool replaceExistingBlocks,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blueprintKey))
        {
            throw new ArgumentException("Blueprint key is required.", nameof(blueprintKey));
        }

        var page = await dbContext.CmsPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null)
        {
            throw new InvalidOperationException("The selected CMS page no longer exists.");
        }

        if (!WorkflowBlueprintBlocks.TryGetValue(blueprintKey.Trim(), out var blueprintBlocksJson))
        {
            throw new InvalidOperationException($"Workflow blueprint '{blueprintKey}' was not found.");
        }

        page.BlocksJson = replaceExistingBlocks
            ? NormalizeBlocksJson(blueprintBlocksJson)
            : MergeBlocksJson(page.BlocksJson, blueprintBlocksJson);
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "cms-workflow-blueprint";

        await dbContext.SaveChangesAsync(cancellationToken);
        return page;
    }

    internal static string CreateSlug(string value)
    {
        var slug = value.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(slug.Length);
        var previousWasDash = false;

        foreach (var character in slug)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasDash = false;
                continue;
            }

            if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        var normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "cms-page" : normalized;
    }

    private async Task<string> GetUniqueSiteSlugAsync(string requestedSlug, Guid currentSiteId, CancellationToken cancellationToken)
    {
        var baseSlug = string.IsNullOrWhiteSpace(requestedSlug) ? "cms-site" : requestedSlug;
        var slugs = await dbContext.CmsSites
            .Where(site => site.Id != currentSiteId)
            .Select(site => site.Slug)
            .ToListAsync(cancellationToken);

        if (!slugs.Contains(baseSlug, StringComparer.OrdinalIgnoreCase))
        {
            return baseSlug;
        }

        for (var counter = 2; ; counter++)
        {
            var candidate = $"{baseSlug}-{counter}";
            if (!slugs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }

    private async Task<string> GetUniquePageSlugAsync(Guid siteId, string requestedSlug, Guid currentPageId, CancellationToken cancellationToken)
    {
        var baseSlug = string.IsNullOrWhiteSpace(requestedSlug) ? "cms-page" : requestedSlug;
        var slugs = await dbContext.CmsPages
            .Where(page => page.SiteId == siteId && page.Id != currentPageId)
            .Select(page => page.Slug)
            .ToListAsync(cancellationToken);

        if (!slugs.Contains(baseSlug, StringComparer.OrdinalIgnoreCase))
        {
            return baseSlug;
        }

        for (var counter = 2; ; counter++)
        {
            var candidate = $"{baseSlug}-{counter}";
            if (!slugs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }

    private static string NormalizeBlocksJson(string blocksJson)
    {
        var trimmed = string.IsNullOrWhiteSpace(blocksJson) ? "[]" : blocksJson.Trim();
        using var document = JsonDocument.Parse(trimmed);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static string MergeBlocksJson(string existingBlocksJson, string blueprintBlocksJson)
    {
        var existingArray = ParseToArray(existingBlocksJson);
        var blueprintArray = ParseToArray(blueprintBlocksJson);
        var mergedArray = new JsonArray();

        foreach (var block in existingArray)
        {
            mergedArray.Add(block?.DeepClone());
        }

        foreach (var block in blueprintArray)
        {
            mergedArray.Add(block?.DeepClone());
        }

        return mergedArray.ToJsonString();
    }

    private static JsonArray ParseToArray(string blocksJson)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(blocksJson) ? "[]" : blocksJson.Trim());
        return node as JsonArray ?? throw new InvalidOperationException("Blocks JSON must be a JSON array.");
    }
}
