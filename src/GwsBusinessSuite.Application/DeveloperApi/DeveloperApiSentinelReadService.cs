using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.DeveloperApi;

// Deliberately its own small, independent implementation rather than a refactor of
// SentinelAiService.ExecuteToolCallAsync's search_wiki/get_page cases - purely additive, zero
// risk to that 1991-line orchestration engine.
public sealed class DeveloperApiSentinelReadService(
    IAppDbContext db,
    ISentinelWorkspaceService workspaceService,
    ISentinelAccessService accessService) : IDeveloperApiSentinelReadService
{
    public async Task<IReadOnlyList<DeveloperApiSentinelSearchResult>> SearchWikiAsync(
        string query, string ownerUsername, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = await workspaceService.SearchAsync(query, ownerUsername, maxResults: 10, cancellationToken);
        return results.Select(result => new DeveloperApiSentinelSearchResult(result.Id, result.IsDatabase, result.Title, result.Preview)).ToList();
    }

    public async Task<DeveloperApiSentinelPage?> GetPageAsync(
        Guid pageId, string ownerUsername, CancellationToken cancellationToken = default)
    {
        if (!await accessService.CanAccessAsync(pageId, isDatabase: false, ownerUsername, SentinelAccessLevels.View, cancellationToken))
            return null;

        var page = await db.WikiPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken);
        if (page is null) return null;

        var text = string.Join(" ", WikiBlockJson.ParseBlocks(page.BlocksJson).Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 500)));
        return new DeveloperApiSentinelPage(page.Id, page.Title, Limit(text, 4_000));
    }

    public async Task<DeveloperApiSentinelCrmResults> SearchCrmAsync(
        string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new([], []);
        var term = query.Trim();

        // Contact.FollowUpDate and Deal.ExpectedCloseDate are DateTimeOffset columns, which
        // EF Core cannot sort or range-compare on SQLite - materialize the (small, capped) match
        // set first and do every date-shaped operation in memory. Same constraint that put
        // CreatedAtUnixSeconds on Deal in the first place.
        var contacts = await db.Contacts.AsNoTracking()
            .Where(contact => contact.TrashedAt == null
                && (EF.Functions.Like(contact.FullName, $"%{term}%")
                    || EF.Functions.Like(contact.Email ?? string.Empty, $"%{term}%")
                    || EF.Functions.Like(contact.Company ?? string.Empty, $"%{term}%")))
            .Take(MaxCrmResults)
            .ToListAsync(cancellationToken);

        var deals = await db.Deals.AsNoTracking()
            .Where(deal => EF.Functions.Like(deal.Title, $"%{term}%")
                || EF.Functions.Like(deal.Notes, $"%{term}%"))
            .Take(MaxCrmResults)
            .ToListAsync(cancellationToken);

        // A second round trip rather than a join, purely for simplicity over this capped result
        // set. Deal.ContactId is a cascading FK, so a deal can never outlive its contact and the
        // null case below is unreachable defence rather than a scenario to rely on.
        var contactIds = deals.Select(deal => deal.ContactId).Distinct().ToList();
        var names = await db.Contacts.AsNoTracking()
            .Where(contact => contactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);

        return new(
            contacts
                .OrderBy(contact => contact.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(contact => new DeveloperApiSentinelContact(
                    contact.Id, contact.FullName, contact.Email, contact.Company, contact.Status, contact.FollowUpDate))
                .ToList(),
            deals
                .OrderByDescending(deal => deal.ValueUsd)
                .Select(deal => new DeveloperApiSentinelDeal(
                    deal.Id, deal.Title, deal.Stage, deal.ValueUsd,
                    names.GetValueOrDefault(deal.ContactId), deal.ExpectedCloseDate))
                .ToList());
    }

    public async Task<DeveloperApiSentinelPipeline> GetPipelineAsync(CancellationToken cancellationToken = default)
    {
        // Summing a decimal column server-side is exactly the shape EF Core warns about on
        // SQLite (it has no decimal type, so precision is not preserved through the aggregate).
        // The deal table is small enough to total in memory, which is also the only way to get
        // a trustworthy figure out of it.
        var deals = await db.Deals.AsNoTracking()
            .Select(deal => new { deal.Stage, deal.ValueUsd })
            .ToListAsync(cancellationToken);

        var stages = deals
            .GroupBy(deal => deal.Stage)
            .Select(group => new DeveloperApiSentinelPipelineStage(
                group.Key, group.Count(), group.Sum(deal => deal.ValueUsd)))
            .OrderBy(stage => Array.IndexOf(DealStages.All, stage.Stage))
            .ToList();

        var open = deals.Where(deal => deal.Stage is not (DealStages.Won or DealStages.Lost)).ToList();
        return new(
            stages,
            open.Count,
            open.Sum(deal => deal.ValueUsd),
            deals.Where(deal => deal.Stage == DealStages.Won).Sum(deal => deal.ValueUsd),
            deals.Where(deal => deal.Stage == DealStages.Lost).Sum(deal => deal.ValueUsd));
    }

    public async Task<IReadOnlyList<DeveloperApiSentinelCmsPageSummary>> SearchCmsPagesAsync(
        string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var term = query.Trim();

        var pages = await db.CmsPages.AsNoTracking()
            .Where(page => page.TrashedAt == null
                && (EF.Functions.Like(page.Title, $"%{term}%")
                    || EF.Functions.Like(page.Slug, $"%{term}%")
                    || EF.Functions.Like(page.MetaDescription, $"%{term}%")))
            .Take(MaxCrmResults)
            .ToListAsync(cancellationToken);

        // PublishedAt is a DateTimeOffset - ordered here, after materializing, for the same
        // SQLite reason as above.
        return pages
            .OrderByDescending(page => page.PublishedAt ?? DateTimeOffset.MinValue)
            .Select(page => new DeveloperApiSentinelCmsPageSummary(
                page.Id, page.Title, page.Slug, page.Status, page.PublishedAt))
            .ToList();
    }

    public async Task<DeveloperApiSentinelSystemHealth> GetSystemHealthAsync(CancellationToken cancellationToken = default)
    {
        // Unread count is computed over a bool, which SQLite handles - only the ordering has to
        // happen in memory. Capped rather than paged: this exists to answer "is anything wrong
        // right now", not to be an alert browser.
        var unread = await db.DockerHealthAlerts.AsNoTracking()
            .CountAsync(alert => !alert.IsRead, cancellationToken);

        var alerts = await db.DockerHealthAlerts.AsNoTracking()
            .Where(alert => !alert.IsRead)
            .Take(MaxAlertScan)
            .ToListAsync(cancellationToken);

        return new(
            unread,
            alerts
                .OrderByDescending(alert => alert.CreatedAt)
                .Take(MaxAlerts)
                .Select(alert => new DeveloperApiSentinelHealthAlert(
                    alert.ContainerName, alert.Severity, alert.Message, alert.IsRead, alert.CreatedAt))
                .ToList());
    }

    private const int MaxCrmResults = 10;

    // Scanned wider than returned so the newest alerts are actually among the candidates -
    // Take() before the in-memory sort would otherwise pick an arbitrary ten.
    private const int MaxAlertScan = 200;
    private const int MaxAlerts = 10;

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
