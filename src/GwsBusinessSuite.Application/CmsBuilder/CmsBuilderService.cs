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

    // Section/Column/Widget layout JSON (the schema the Studio edits and CmsBlockRenderer.jsx
    // / CmsBlockHtmlRenderer.cs / CmsBlockPreview.razor all render) — not the old flat block
    // array. Widget vocabulary is generic primitives (heading, paragraph, card, button, form,
    // etc.) rather than pre-composed marketing blocks, so specialized old block types
    // (countdown, pricing-table, faq, testimonials) are approximated with those primitives.
    private static readonly Dictionary<string, string> WorkflowBlueprintBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["landing-conversion"] = """
            {"sections":[
              {"id":"hero","padding":"lg","columnLayout":"full","columns":[{"id":"hero-col","widgets":[
                {"id":"hero-w","widgetType":"hero","props":{"headline":"Build Your Next Growth Funnel","subline":"A focused landing workflow for conversion.","cta1Label":"Start Free Trial","cta1Href":"#","align":"left"}}
              ]}]},
              {"id":"proof","background":"light","padding":"md","columnLayout":"full","columns":[{"id":"proof-col","widgets":[
                {"id":"proof-h","widgetType":"heading","props":{"text":"Trusted by teams","level":"h2","align":"left"}}
              ]}]},
              {"id":"proof-cards","background":"light","padding":"sm","columnLayout":"thirds","columns":[
                {"id":"pc1","widgets":[{"id":"pc1w","widgetType":"card","props":{"title":"Case Study One","body":"Result summary goes here."}}]},
                {"id":"pc2","widgets":[{"id":"pc2w","widgetType":"card","props":{"title":"Case Study Two","body":"Result summary goes here."}}]},
                {"id":"pc3","widgets":[{"id":"pc3w","widgetType":"card","props":{"title":"Case Study Three","body":"Result summary goes here."}}]}
              ]},
              {"id":"features","padding":"md","columnLayout":"full","columns":[{"id":"features-col","widgets":[
                {"id":"features-h","widgetType":"heading","props":{"text":"What you get","level":"h2","align":"left"}}
              ]}]},
              {"id":"features-cards","padding":"sm","columnLayout":"thirds","columns":[
                {"id":"fc1","widgets":[{"id":"fc1w","widgetType":"card","props":{"title":"Automation","body":"Automate the repetitive parts of your workflow."}}]},
                {"id":"fc2","widgets":[{"id":"fc2w","widgetType":"card","props":{"title":"Analytics","body":"See what's working and double down."}}]},
                {"id":"fc3","widgets":[{"id":"fc3w","widgetType":"card","props":{"title":"Integrations","body":"Connect the tools you already use."}}]}
              ]},
              {"id":"cta","background":"dark","padding":"lg","columnLayout":"full","columns":[{"id":"cta-col","widgets":[
                {"id":"cta-h","widgetType":"heading","props":{"text":"Ready to launch?","level":"h2","align":"center"}},
                {"id":"cta-b","widgetType":"button","props":{"label":"Book a Demo","href":"#","variant":"primary","align":"center"}}
              ]}]}
            ]}
            """,
        ["blog-editorial"] = """
            {"sections":[
              {"id":"header","padding":"lg","columnLayout":"full","columns":[{"id":"header-col","widgets":[
                {"id":"h1","widgetType":"heading","props":{"text":"Editorial Title","level":"h1","align":"left"}},
                {"id":"h1-sub","widgetType":"paragraph","props":{"text":"Strong angle and reader promise.","align":"left"}}
              ]}]},
              {"id":"body","padding":"md","columnLayout":"full","columns":[{"id":"body-col","widgets":[
                {"id":"toc","widgetType":"heading","props":{"text":"In this article","level":"h3","align":"left"}},
                {"id":"content","widgetType":"html","props":{"content":"<h2>Section</h2><p>Long-form editorial content goes here.</p>"}}
              ]}]},
              {"id":"author","padding":"sm","columnLayout":"full","columns":[{"id":"author-col","widgets":[
                {"id":"author-card","widgetType":"card","props":{"title":"GWS Editorial","body":"Editor"}}
              ]}]},
              {"id":"newsletter","background":"accent","padding":"lg","columnLayout":"full","columns":[{"id":"newsletter-col","widgets":[
                {"id":"n-h","widgetType":"heading","props":{"text":"Get weekly updates","level":"h2","align":"center"}},
                {"id":"n-b","widgetType":"button","props":{"label":"Subscribe","href":"#","variant":"primary","align":"center"}}
              ]}]}
            ]}
            """,
        ["product-launch"] = """
            {"sections":[
              {"id":"hero","padding":"lg","columnLayout":"full","columns":[{"id":"hero-col","widgets":[
                {"id":"hero-w","widgetType":"hero","props":{"headline":"Product Launch Week","subline":"Ship your message with precision.","cta1Label":"Join Waitlist","cta1Href":"#","align":"left"}}
              ]}]},
              {"id":"countdown","padding":"md","columnLayout":"full","columns":[{"id":"countdown-col","widgets":[
                {"id":"cd-h","widgetType":"heading","props":{"text":"Launch countdown: 7 days","level":"h2","align":"center"}}
              ]}]},
              {"id":"pricing","padding":"sm","columnLayout":"thirds","columns":[
                {"id":"p1","widgets":[{"id":"p1w","widgetType":"card","props":{"title":"Starter","body":"Everything you need to get going."}}]},
                {"id":"p2","widgets":[{"id":"p2w","widgetType":"card","props":{"title":"Pro","body":"For teams that are scaling fast."}}]},
                {"id":"p3","widgets":[{"id":"p3w","widgetType":"card","props":{"title":"Scale","body":"Custom limits and dedicated support."}}]}
              ]},
              {"id":"faq","padding":"md","columnLayout":"full","columns":[{"id":"faq-col","widgets":[
                {"id":"faq-h","widgetType":"heading","props":{"text":"Questions answered","level":"h2","align":"left"}}
              ]}]}
            ]}
            """,
        ["service-business"] = """
            {"sections":[
              {"id":"hero","padding":"lg","columnLayout":"full","columns":[{"id":"hero-col","widgets":[
                {"id":"hero-w","widgetType":"hero","props":{"headline":"Professional Services","subline":"Built for outcomes and trust.","cta1Label":"Schedule Consultation","cta1Href":"#","align":"left"}}
              ]}]},
              {"id":"services","padding":"sm","columnLayout":"thirds","columns":[
                {"id":"s1","widgets":[{"id":"s1w","widgetType":"card","props":{"title":"Discovery","body":"We start by understanding your goals."}}]},
                {"id":"s2","widgets":[{"id":"s2w","widgetType":"card","props":{"title":"Implementation","body":"We build and ship the solution."}}]},
                {"id":"s3","widgets":[{"id":"s3w","widgetType":"card","props":{"title":"Support","body":"We stick around after launch."}}]}
              ]},
              {"id":"testimonials","padding":"md","columnLayout":"full","columns":[{"id":"testimonials-col","widgets":[
                {"id":"t-h","widgetType":"heading","props":{"text":"Client results","level":"h2","align":"left"}}
              ]}]},
              {"id":"contact","padding":"lg","columnLayout":"full","columns":[{"id":"contact-col","widgets":[
                {"id":"contact-form","widgetType":"form","props":{"submitLabel":"Get your plan","fieldsJson":"[{\"key\":\"name\",\"label\":\"Name\",\"type\":\"text\",\"required\":true},{\"key\":\"email\",\"label\":\"Email\",\"type\":\"email\",\"required\":true},{\"key\":\"message\",\"label\":\"Project details\",\"type\":\"textarea\",\"required\":true}]"}}
              ]}]}
            ]}
            """
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
        site.CustomCss = editor.CustomCss?.Trim() ?? string.Empty;
        site.NavMenuJson = string.IsNullOrWhiteSpace(editor.NavMenuJson) ? "[]" : editor.NavMenuJson.Trim();
        site.FooterNavMenuJson = string.IsNullOrWhiteSpace(editor.FooterNavMenuJson) ? "[]" : editor.FooterNavMenuJson.Trim();
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
            var pageIds = pages.Select(page => page.Id).ToList();
            var submissions = await dbContext.FormSubmissions
                .Where(submission => pageIds.Contains(submission.PageId))
                .ToListAsync(cancellationToken);

            if (submissions.Count > 0)
            {
                dbContext.FormSubmissions.RemoveRange(submissions);
            }

            var siteRevisions = await dbContext.CmsPageRevisions
                .Where(revision => pageIds.Contains(revision.PageId))
                .ToListAsync(cancellationToken);

            if (siteRevisions.Count > 0)
            {
                dbContext.CmsPageRevisions.RemoveRange(siteRevisions);
            }

            dbContext.CmsPages.RemoveRange(pages);
        }

        dbContext.CmsSites.Remove(site);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CmsPage>> ListPagesAsync(Guid? siteId = null, bool includeTrashed = false, CancellationToken cancellationToken = default)
    {
        var query = dbContext.CmsPages.AsNoTracking();
        if (siteId is { } actualSiteId)
        {
            query = query.Where(page => page.SiteId == actualSiteId);
        }
        if (!includeTrashed)
        {
            query = query.Where(page => page.TrashedAt == null);
        }

        var pages = await query.ToListAsync(cancellationToken);
        return pages
            .OrderByDescending(page => page.UpdatedAt ?? page.CreatedAt)
            .ThenBy(page => page.Title)
            .ToList();
    }

    // Thin wrapper for the Trash view — every page for the site that IS trashed, most
    // recently trashed first (mirrors ListPagesAsync's own recency-first ordering).
    public async Task<IReadOnlyList<CmsPage>> ListTrashedPagesAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var pages = await dbContext.CmsPages
            .AsNoTracking()
            .Where(page => page.SiteId == siteId && page.TrashedAt != null)
            .ToListAsync(cancellationToken);

        return pages
            .OrderByDescending(page => page.TrashedAt)
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

    // Resolves a nested path ("services/web-dev") by walking it segment-by-segment against
    // parent/child relationships, rather than a single Slug lookup — slugs are only unique
    // per parent (see the composite index on CmsPage), not per site, so a bare slug lookup
    // can't disambiguate /services/pricing from /products/pricing.
    //
    // includeUnpublished lets an authenticated admin preview a Draft page (see the
    // /cms/{siteSlug}/{**pageSlug} route in Program.cs) while public visitors never resolve
    // one — only the final segment's status is checked, not intermediate ancestors, so a
    // published child under a draft parent still resolves (matches WordPress: page hierarchy
    // is structural, independent of each ancestor's own publish state).
    public async Task<CmsPage?> GetPageByFullPathAsync(Guid siteId, string fullPath, bool includeUnpublished = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var pages = await dbContext.CmsPages
            .AsNoTracking()
            .Where(page => page.SiteId == siteId)
            .ToListAsync(cancellationToken);

        var segments = fullPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        CmsPage? current = null;
        Guid? parentId = null;
        foreach (var segment in segments)
        {
            current = pages.FirstOrDefault(p => p.ParentPageId == parentId && p.Slug == segment);
            if (current is null)
            {
                return null;
            }
            parentId = current.Id;
        }

        // Trashed is unconditional — unlike Draft, an authenticated admin previewing via
        // the Studio doesn't get to see a trashed page either. Trash means "not visible
        // anywhere until restored."
        if (current is not null && current.TrashedAt is not null)
        {
            return null;
        }

        if (current is not null && !includeUnpublished && current.Status != CmsPageStatuses.Published)
        {
            return null;
        }

        return current;
    }

    // Shared by the Studio's page list (display) and Program.cs (nav hrefs, "View Live Page")
    // — walks ParentPageId back to the root. allPagesInSite must include every page for the
    // site the given page belongs to, or ancestors won't resolve.
    public string BuildFullPath(CmsPage page, IReadOnlyList<CmsPage> allPagesInSite)
    {
        var byId = allPagesInSite.ToDictionary(p => p.Id);
        var segments = new List<string>();
        CmsPage? current = page;
        var guard = 0;
        while (current is not null && guard++ < 64)
        {
            segments.Insert(0, current.Slug);
            current = current.ParentPageId is { } parentId && byId.TryGetValue(parentId, out var parent) ? parent : null;
        }

        return string.Join('/', segments);
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
            BlocksJson = "{\"sections\":[]}",
            CreatedAt = now,
            CreatedBy = "cms-ui"
        };

        if (editor.ParentPageId is { } requestedParentId)
        {
            if (requestedParentId == page.Id)
            {
                throw new ArgumentException("A page cannot be its own parent.");
            }

            var descendants = await GetDescendantIdsAsync(siteId, page.Id, cancellationToken);
            if (descendants.Contains(requestedParentId))
            {
                throw new ArgumentException("A page cannot be moved under one of its own child pages.");
            }

            var parentExists = await dbContext.CmsPages.AnyAsync(p => p.Id == requestedParentId && p.SiteId == siteId, cancellationToken);
            if (!parentExists)
            {
                throw new ArgumentException("The selected parent page no longer exists.");
            }
        }

        var requestedSlug = string.IsNullOrWhiteSpace(editor.Slug)
            ? CreateSlug(editor.Title)
            : CreateSlug(editor.Slug);
        var uniqueSlug = await GetUniquePageSlugAsync(siteId, editor.ParentPageId, requestedSlug, page.Id, cancellationToken);

        var requestedStatus = editor.Status == CmsPageStatuses.Published ? CmsPageStatuses.Published : CmsPageStatuses.Draft;

        page.SiteId = siteId;
        page.ParentPageId = editor.ParentPageId;
        page.Title = editor.Title.Trim();
        page.Slug = uniqueSlug;
        page.BlocksJson = NormalizeBlocksJson(editor.BlocksJson);
        page.MetaTitle = editor.MetaTitle?.Trim() ?? string.Empty;
        page.MetaDescription = editor.MetaDescription?.Trim() ?? string.Empty;
        page.OgImageUrl = editor.OgImageUrl?.Trim() ?? string.Empty;
        page.CustomCss = editor.CustomCss?.Trim() ?? string.Empty;
        // Stamped once, the first time a page goes live, and left alone after — re-saving an
        // already-published page (or unpublishing and republishing) doesn't reset it.
        if (requestedStatus == CmsPageStatuses.Published && page.PublishedAt is null)
        {
            page.PublishedAt = now;
        }
        page.Status = requestedStatus;
        page.UpdatedAt = now;
        page.UpdatedBy = "cms-ui";

        if (isNew)
        {
            await dbContext.CmsPages.AddAsync(page, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return page;
    }

    // Permanent, unrecoverable delete — only allowed on a page that's already in the Trash
    // (see TrashPageAsync), so a stray click on the normal page editor can never permanently
    // destroy content; it has to go through Trash first.
    public async Task DeletePageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.CmsPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        if (page.TrashedAt is null)
        {
            throw new ArgumentException("Move this page to Trash before deleting it permanently.");
        }

        var children = await dbContext.CmsPages
            .Where(p => p.ParentPageId == pageId)
            .Select(p => p.Title)
            .ToListAsync(cancellationToken);

        if (children.Count > 0)
        {
            throw new ArgumentException(
                $"Delete or move its child page{(children.Count == 1 ? "" : "s")} first: {string.Join(", ", children)}.");
        }

        var submissions = await dbContext.FormSubmissions
            .Where(submission => submission.PageId == pageId)
            .ToListAsync(cancellationToken);

        if (submissions.Count > 0)
        {
            dbContext.FormSubmissions.RemoveRange(submissions);
        }

        var revisions = await dbContext.CmsPageRevisions
            .Where(revision => revision.PageId == pageId)
            .ToListAsync(cancellationToken);

        if (revisions.Count > 0)
        {
            dbContext.CmsPageRevisions.RemoveRange(revisions);
        }

        dbContext.CmsPages.Remove(page);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Soft delete — the page disappears from the normal Pages list and every public/preview
    // route (GetPageByFullPathAsync) but isn't gone until DeletePageAsync (permanent) is
    // called on it explicitly from the Trash view.
    public async Task TrashPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.CmsPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        var activeChildren = await dbContext.CmsPages
            .Where(p => p.ParentPageId == pageId && p.TrashedAt == null)
            .Select(p => p.Title)
            .ToListAsync(cancellationToken);

        if (activeChildren.Count > 0)
        {
            throw new ArgumentException(
                $"Trash or move its child page{(activeChildren.Count == 1 ? "" : "s")} first: {string.Join(", ", activeChildren)}.");
        }

        page.TrashedAt = DateTimeOffset.UtcNow;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "cms-ui";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Restores a trashed page exactly as it was — Status (Draft/Published) is untouched, so
    // restoring never silently publishes or unpublishes anything.
    public async Task RestorePageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.CmsPages.FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        page.TrashedAt = null;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "cms-ui";
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

    // Scoped to siblings under the same parent, not every page in the site — WordPress-style,
    // /services/pricing and /products/pricing can share the slug "pricing" since their full
    // paths still differ (see the composite unique index on CmsPage).
    private async Task<string> GetUniquePageSlugAsync(Guid siteId, Guid? parentPageId, string requestedSlug, Guid currentPageId, CancellationToken cancellationToken)
    {
        var baseSlug = string.IsNullOrWhiteSpace(requestedSlug) ? "cms-page" : requestedSlug;
        var slugs = await dbContext.CmsPages
            .Where(page => page.SiteId == siteId && page.ParentPageId == parentPageId && page.Id != currentPageId)
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

    // BFS over ParentPageId to find every descendant of a page — used to block both
    // "delete a page with children" and "reparent a page under its own descendant".
    private async Task<HashSet<Guid>> GetDescendantIdsAsync(Guid siteId, Guid pageId, CancellationToken cancellationToken)
    {
        var allPages = await dbContext.CmsPages
            .Where(p => p.SiteId == siteId)
            .Select(p => new { p.Id, p.ParentPageId })
            .ToListAsync(cancellationToken);

        var descendants = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(pageId);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in allPages.Where(p => p.ParentPageId == current))
            {
                if (descendants.Add(child.Id))
                {
                    frontier.Enqueue(child.Id);
                }
            }
        }

        return descendants;
    }

    private static string NormalizeBlocksJson(string blocksJson)
    {
        var trimmed = string.IsNullOrWhiteSpace(blocksJson) ? "{\"sections\":[]}" : blocksJson.Trim();
        using var document = JsonDocument.Parse(trimmed);
        return JsonSerializer.Serialize(document.RootElement);
    }

    private static string MergeBlocksJson(string existingBlocksJson, string blueprintBlocksJson)
    {
        var existingSections = ParseSections(existingBlocksJson);
        var blueprintSections = ParseSections(blueprintBlocksJson);
        var mergedSections = new JsonArray();

        foreach (var section in existingSections)
        {
            mergedSections.Add(section?.DeepClone());
        }

        foreach (var section in blueprintSections)
        {
            mergedSections.Add(section?.DeepClone());
        }

        return new JsonObject { ["sections"] = mergedSections }.ToJsonString();
    }

    private static JsonArray ParseSections(string blocksJson)
    {
        var node = JsonNode.Parse(string.IsNullOrWhiteSpace(blocksJson) ? "{}" : blocksJson.Trim());
        if (node is not JsonObject obj)
        {
            throw new InvalidOperationException("Page layout JSON must be an object with a \"sections\" array.");
        }

        return obj["sections"] as JsonArray ?? [];
    }
}
