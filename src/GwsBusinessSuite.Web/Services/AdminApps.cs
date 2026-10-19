using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Web.Services;

// Small admin apps whose pages share one sidebar entry and a tab strip (AppTabs) instead of a
// sidebar entry each. Every page keeps its own address, so bookmarks and links still work, and
// each tab stays findable in ⌘K through NavMenu's hidden command entries.
public sealed record AdminAppTab(string Href, string Label, string Description, string Icon, bool AdminOnly = false);

public sealed record AdminApp(string Label, string Description, string Icon, IReadOnlyList<AdminAppTab> Tabs)
{
    public string Href => Tabs[0].Href;

    public IEnumerable<AdminAppTab> TabsFor(string role) =>
        Tabs.Where(tab => !tab.AdminOnly || role == AppRoles.Admin);

    // Whether a base-relative path (e.g. "admin/community/messages?x=1") is one of this app's pages.
    public bool Contains(string relativePath)
    {
        var path = Normalize(relativePath);
        return Tabs.Any(tab => path == tab.Href || path.StartsWith(tab.Href + "/", StringComparison.Ordinal));
    }

    private static string Normalize(string relativePath)
    {
        var end = relativePath.IndexOfAny(['?', '#']);
        return (end < 0 ? relativePath : relativePath[..end]).Trim('/').ToLowerInvariant();
    }
}

public static class AdminApps
{
    public static readonly AdminApp Community = new("Community", "Team directory and messages", "bi-people-fill",
    [
        new("admin/community", "Directory", "Find teammates and departments", "bi-people-fill"),
        new("admin/community/activity", "Activity", "Recent team activity", "bi-activity"),
        new("admin/community/messages", "Messages", "Direct messages with teammates", "bi-chat-left-text"),
        new("admin/community/departments", "Departments", "Teams and department leads", "bi-diagram-3", AdminOnly: true)
    ]);

    public static readonly AdminApp Crm = new("CRM", "Contacts, deals and deal scores", "bi-people-fill",
    [
        new("admin/crm", "Contacts & deals", "Contacts and activity", "bi-people-fill"),
        new("admin/deal-scoring", "Deal scoring", "Win-likelihood by deal", "bi-graph-up-arrow")
    ]);

    public static readonly AdminApp PageBuilder = new("SentinelGPT Builder", "Draft new site pages and review them", "bi-stars",
    [
        new("admin/app-generation", "Builder", "Draft new site pages", "bi-stars"),
        new("admin/app-generation-queue", "Approval queue", "Review generated plans", "bi-inbox-fill", AdminOnly: true)
    ]);

    public static readonly AdminApp SiteDesign = new("Site design", "Menus, header & footer, appearance", "bi-palette",
    [
        new("admin/appearance/menus", "Menus", "Header and footer navigation", "bi-list"),
        new("admin/appearance/header-footer", "Header & footer", "Design the site-wide header and footer", "bi-layout-text-window"),
        new("admin/appearance/customize", "Appearance", "Brand, fonts and CSS", "bi-palette")
    ]);

    public static readonly AdminApp Affiliate = new("Affiliate", "CJ campaigns, placements and earnings", "bi-tags-fill",
    [
        new("admin/cj-ads", "CJ Ads", "Affiliate campaigns", "bi-tags-fill"),
        new("admin/affiliate-suggestions", "Suggestions", "SentinelGPT-matched placements", "bi-link-45deg"),
        new("admin/affiliate-analytics", "Analytics", "Clicks and commission", "bi-graph-up-arrow")
    ]);

    public static readonly IReadOnlyList<AdminApp> All = [Community, Crm, PageBuilder, SiteDesign, Affiliate];
}
