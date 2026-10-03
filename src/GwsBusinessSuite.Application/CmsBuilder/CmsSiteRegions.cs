using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

// A starting layout for the site-wide header or footer (Appearance > Header & Footer).
public sealed record CmsRegionTemplate(string Key, string Region, string Name, string Description, Func<PageLayout> Build);

public static class CmsRegionTemplates
{
    public static readonly IReadOnlyList<CmsRegionTemplate> All =
    [
        new("header-logo-menu", CmsPageRegions.Header, "Logo and menu",
            "Your logo on the left, the header menu on the right.",
            () => Layout(Section("half-half", "sm",
                Column(6, SiteLogo()),
                Column(6, NavMenu(CmsPageRegions.Header, "horizontal", "right"))))),
        new("header-logo-menu-button", CmsPageRegions.Header, "Logo, menu and button",
            "Logo, a centred menu and a call-to-action button.",
            () => Layout(Section("thirds", "sm",
                Column(4, SiteLogo()),
                Column(4, NavMenu(CmsPageRegions.Header, "horizontal", "center")),
                Column(4, Button("Get in touch", "/contact", "right"))))),
        new("header-centered", CmsPageRegions.Header, "Centred",
            "Logo centred above the menu - good for blogs and portfolios.",
            () => Layout(Section("full", "sm",
                Column(12, SiteLogo("center"), NavMenu(CmsPageRegions.Header, "horizontal", "center"))))),

        new("footer-simple", CmsPageRegions.Footer, "Simple",
            "The footer menu and a copyright line, centred.",
            () => Layout(Section("full", "md",
                Column(12, NavMenu(CmsPageRegions.Footer, "horizontal", "center"), Copyright("center"))))),
        new("footer-columns", CmsPageRegions.Footer, "Three columns",
            "About blurb, footer links and copyright side by side.",
            () => Layout(Section("thirds", "lg",
                Column(4, SiteLogo(), Paragraph("Software for the way your business actually works.")),
                Column(4, NavMenu(CmsPageRegions.Footer, "vertical", "left")),
                Column(4, Copyright("left"))))),
        new("footer-newsletter", CmsPageRegions.Footer, "With newsletter signup",
            "An email signup for new articles above the footer menu and copyright.",
            () => Layout(
                Section("full", "lg", Column(12, EmailSignup())),
                Section("full", "sm", Column(12, NavMenu(CmsPageRegions.Footer, "horizontal", "center"), Copyright("center"))))),
    ];

    public static CmsRegionTemplate? Find(string key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<CmsRegionTemplate> For(string region) => All.Where(t => t.Region == region);

    private static PageLayout Layout(params LayoutSection[] sections) => new() { Sections = [.. sections] };

    private static LayoutSection Section(string columnLayout, string padding, params LayoutColumn[] columns) =>
        new() { Label = "Section", ColumnLayout = columnLayout, Padding = padding, Columns = [.. columns] };

    private static LayoutColumn Column(int span, params LayoutWidget[] widgets) => new() { Span = span, Widgets = [.. widgets] };

    private static LayoutWidget SiteLogo(string align = "left") =>
        new() { WidgetType = "site-logo", Props = new() { ["showName"] = "true", ["height"] = "36", ["align"] = align } };

    private static LayoutWidget NavMenu(string menu, string direction, string align) =>
        new() { WidgetType = "nav-menu", Props = new() { ["menu"] = menu, ["direction"] = direction, ["align"] = align } };

    private static LayoutWidget Copyright(string align) =>
        new() { WidgetType = "copyright", Props = new() { ["holder"] = "Grant Watson", ["text"] = "", ["showAdminLink"] = "true", ["align"] = align } };

    private static LayoutWidget Paragraph(string text) =>
        new() { WidgetType = "paragraph", Props = new() { ["text"] = text } };

    private static LayoutWidget Button(string label, string href, string align) =>
        new() { WidgetType = "button", Props = new() { ["label"] = label, ["href"] = href, ["variant"] = "primary", ["align"] = align } };

    private static LayoutWidget EmailSignup() =>
        new() { WidgetType = "email-signup", Props = new() { ["campaignId"] = "", ["heading"] = "Get new articles by email", ["description"] = "", ["showFirstName"] = "false", ["buttonLabel"] = "Subscribe", ["consentText"] = "No spam. Unsubscribe any time.", ["successMessage"] = "Almost done - check your inbox and click the link to confirm.", ["align"] = "center" } };
}

public sealed record CmsSiteRegionView(string Region, Guid? PageId, bool IsLive, bool HasUnpublishedChanges, DateTimeOffset? UpdatedAt);

// The site-wide header/footer, stored as hidden CmsPages (Region set) so the page editor - with
// its autosave, drafts, undo and revisions - builds them like any page.
public interface ICmsSiteRegionService
{
    Task<IReadOnlyList<CmsSiteRegionView>> ListAsync(Guid siteId, CancellationToken cancellationToken = default);
    // The region page itself (for the editor canvas), by its reserved slug; null if not a region slug.
    Task<CmsPage?> GetBySlugAsync(Guid siteId, string slug, CancellationToken cancellationToken = default);
    Task<CmsPage> CreateAsync(Guid siteId, string region, string templateKey, string actor, CancellationToken cancellationToken = default);
    Task SetLiveAsync(Guid siteId, string region, bool live, string actor, CancellationToken cancellationToken = default);
    // Published layouts only (what visitors see); null when the region uses the built-in default.
    Task<(PageLayout? Header, PageLayout? Footer)> GetLiveLayoutsAsync(Guid siteId, CancellationToken cancellationToken = default);
}

public sealed class CmsSiteRegionService(IAppDbContext db, TimeProvider? timeProvider = null) : ICmsSiteRegionService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<CmsSiteRegionView>> ListAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var pages = await RegionPages(siteId).AsNoTracking().ToListAsync(cancellationToken);
        return CmsPageRegions.All.Select(region =>
        {
            var page = pages.FirstOrDefault(p => p.Region == region);
            return new CmsSiteRegionView(region, page?.Id, page?.Status == CmsPageStatuses.Published,
                page is not null && page.DraftBlocksJson is not null && page.DraftBlocksJson != page.BlocksJson,
                page?.UpdatedAt ?? page?.CreatedAt);
        }).ToList();
    }

    public async Task<CmsPage?> GetBySlugAsync(Guid siteId, string slug, CancellationToken cancellationToken = default)
    {
        var region = CmsPageRegions.All.FirstOrDefault(r => string.Equals(CmsPageRegions.SlugFor(r), slug, StringComparison.OrdinalIgnoreCase));
        return region is null
            ? null
            : await RegionPages(siteId).AsNoTracking().FirstOrDefaultAsync(p => p.Region == region, cancellationToken);
    }

    public async Task<CmsPage> CreateAsync(Guid siteId, string region, string templateKey, string actor, CancellationToken cancellationToken = default)
    {
        if (!CmsPageRegions.All.Contains(region)) throw new ArgumentException($"Unknown region '{region}'.", nameof(region));
        var existing = await RegionPages(siteId).FirstOrDefaultAsync(p => p.Region == region, cancellationToken);
        if (existing is not null) return existing;

        var template = CmsRegionTemplates.Find(templateKey);
        if (template is null || template.Region != region) throw new ArgumentException($"'{templateKey}' isn't a {region} template.", nameof(templateKey));

        var now = _time.GetUtcNow();
        var page = new CmsPage
        {
            SiteId = siteId,
            Region = region,
            Title = region == CmsPageRegions.Header ? "Site header" : "Site footer",
            Slug = CmsPageRegions.SlugFor(region),
            BlocksJson = CmsBuilderJson.Serialize(template.Build()),
            Status = CmsPageStatuses.Draft,
            CreatedAt = now,
            CreatedBy = actor
        };
        db.CmsPages.Add(page);
        await db.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task SetLiveAsync(Guid siteId, string region, bool live, string actor, CancellationToken cancellationToken = default)
    {
        var page = await RegionPages(siteId).FirstOrDefaultAsync(p => p.Region == region, cancellationToken)
            ?? throw new InvalidOperationException($"There's no custom {region} yet - design one first.");
        var now = _time.GetUtcNow();
        if (live)
        {
            // Going live publishes the latest edits, exactly like "Publish changes" on a page.
            if (page.DraftBlocksJson is not null) page.BlocksJson = page.DraftBlocksJson;
            page.DraftBlocksJson = null;
            page.Status = CmsPageStatuses.Published;
            page.PublishedAt ??= now;
        }
        else
        {
            page.Status = CmsPageStatuses.Draft;
        }

        page.UpdatedAt = now;
        page.UpdatedBy = actor;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(PageLayout? Header, PageLayout? Footer)> GetLiveLayoutsAsync(Guid siteId, CancellationToken cancellationToken = default)
    {
        var pages = await RegionPages(siteId).AsNoTracking()
            .Where(p => p.Status == CmsPageStatuses.Published && p.TrashedAt == null)
            .Select(p => new { p.Region, p.BlocksJson })
            .ToListAsync(cancellationToken);
        PageLayout? Parse(string region) =>
            pages.FirstOrDefault(p => p.Region == region) is { } row && CmsBuilderJson.ParseLayout(row.BlocksJson) is { Sections.Count: > 0 } layout
                ? layout
                : null;
        return (Parse(CmsPageRegions.Header), Parse(CmsPageRegions.Footer));
    }

    private IQueryable<CmsPage> RegionPages(Guid siteId) =>
        db.CmsPages.IgnoreQueryFilters().Where(p => p.SiteId == siteId && p.Region != null);
}
