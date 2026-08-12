using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SuiteSearchService(IAppDbContext db, ISentinelWorkspaceService sentinelWorkspaceService) : ISuiteSearchService
{
    // Each module query is bounded and materialized before filtering client-side (same
    // "materialize then filter" shape as SentinelAiService.BuildSuiteContextUncachedAsync) -
    // this is a live type-ahead search, so keeping every module's query cheap and independent of
    // the others matters more than a single unified full-text query.
    private const int ModuleScanLimit = 200;
    private const int PerCategoryLimit = 4;

    public async Task<IReadOnlyList<SuiteSearchResult>> SearchAsync(
        string query, string performedBy, int take = 12, CancellationToken cancellationToken = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length < 2) return [];

        var results = new List<SuiteSearchResult>();

        var sentinelMatches = await sentinelWorkspaceService.SearchAsync(query, performedBy, cancellationToken: cancellationToken);
        results.AddRange(sentinelMatches.Take(PerCategoryLimit).Select(item => new SuiteSearchResult(
            item.Title,
            item.Preview,
            item.IsDatabase ? "Sentinel database" : "Sentinel page",
            item.IsDatabase ? $"/admin/sentinel?database={item.Id}" : $"/admin/sentinel?page={item.Id}",
            item.IsDatabase ? "bi bi-grid-3x3-gap-fill" : "bi bi-file-earmark-text")));

        var contacts = await db.Contacts.AsNoTracking().Where(item => item.TrashedAt == null)
            .Take(ModuleScanLimit).Select(item => new { item.FullName, item.Company }).ToListAsync(cancellationToken);
        results.AddRange(contacts.Where(item => Matches(query, item.FullName, item.Company)).Take(PerCategoryLimit)
            .Select(item => new SuiteSearchResult(item.FullName, item.Company ?? "CRM contact", "CRM contact", "/admin/crm", "bi bi-person-lines-fill")));

        var deals = await db.Deals.AsNoTracking()
            .Take(ModuleScanLimit).Select(item => new { item.Title, item.Stage }).ToListAsync(cancellationToken);
        results.AddRange(deals.Where(item => Matches(query, item.Title, item.Stage)).Take(PerCategoryLimit)
            .Select(item => new SuiteSearchResult(item.Title, $"Stage: {item.Stage}", "CRM deal", "/admin/crm", "bi bi-graph-up-arrow")));

        var workflows = await db.AutomationWorkflows.AsNoTracking()
            .Take(ModuleScanLimit).Select(item => new { item.Id, item.Name, item.Description, item.Status }).ToListAsync(cancellationToken);
        results.AddRange(workflows.Where(item => Matches(query, item.Name, item.Description)).Take(PerCategoryLimit)
            .Select(item => new SuiteSearchResult(item.Name, $"{item.Status} workflow", "Automation", $"/admin/automation/{item.Id}", "bi bi-diagram-3-fill")));

        var pages = await db.CmsPages.AsNoTracking().Where(item => item.TrashedAt == null)
            .Take(ModuleScanLimit).Select(item => new { item.Id, item.Title, item.Slug }).ToListAsync(cancellationToken);
        results.AddRange(pages.Where(item => Matches(query, item.Title, item.Slug)).Take(PerCategoryLimit)
            .Select(item => new SuiteSearchResult(item.Title, $"/{item.Slug}", "CMS page", $"/admin/pages/edit/{item.Id}", "bi bi-file-earmark-richtext")));

        var articles = await db.Articles.AsNoTracking().Where(item => item.TrashedAt == null)
            .Take(ModuleScanLimit).Select(item => new { item.Title, item.Topic }).ToListAsync(cancellationToken);
        results.AddRange(articles.Where(item => Matches(query, item.Title, item.Topic)).Take(PerCategoryLimit)
            .Select(item => new SuiteSearchResult(item.Title, item.Topic ?? "Article", "Article", "/admin/article-editor", "bi bi-newspaper")));

        var offers = await db.AffiliateOffers.AsNoTracking()
            .Take(ModuleScanLimit).Select(item => new { item.LinkName, item.AdvertiserName }).ToListAsync(cancellationToken);
        results.AddRange(offers.Where(item => Matches(query, item.LinkName, item.AdvertiserName)).Take(PerCategoryLimit)
            .Select(item => new SuiteSearchResult(item.LinkName, item.AdvertiserName, "Affiliate offer", "/admin/cj-ads", "bi bi-link-45deg")));

        return results.Take(Math.Clamp(take, 1, 40)).ToList();
    }

    private static bool Matches(string query, params string?[] values) =>
        values.Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));
}
