using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SuiteSearchService(
    IAppDbContext db,
    ISentinelWorkspaceService sentinelWorkspaceService,
    IHybridSearchService? hybridSearchService = null) : ISuiteSearchService
{
    // Each module query is bounded and materialized before filtering client-side (same
    // "materialize then filter" shape as SentinelAiService.BuildSuiteContextUncachedAsync) -
    // this is a live type-ahead search, so keeping every module's query cheap and independent of
    // the others matters more than a single unified full-text query.
    private const int ModuleScanLimit = 200;
    private const int PerCategoryLimit = 4;
    private const int FilteredCategoryLimit = 12;
    // The semantic part waits on the local Ollama, which may be busy with other work (a queued
    // generation held it for 9 s in testing); past this, the palette goes on with keyword matches.
    public TimeSpan SemanticBudget { get; init; } = TimeSpan.FromMilliseconds(1500);

    public async Task<IReadOnlyList<SuiteSearchResult>> SearchAsync(
        string query, string performedBy, int take = 12, CancellationToken cancellationToken = default)
    {
        // "t:ticket acme" narrows to one kind (SuiteSearchQuery); with a filter, the text may be
        // empty, which lists that kind's newest records, and each kind returns more results.
        var parsed = SuiteSearchQuery.Parse(query);
        var text = parsed.Text;
        if (!parsed.IsFiltered && text.Length < 2) return [];
        var perCategory = parsed.IsFiltered ? FilteredCategoryLimit : PerCategoryLimit;

        var results = new List<SuiteSearchResult>();

        if (text.Length >= 2 && (parsed.Wants(SuiteSearchCategories.SentinelPage) || parsed.Wants(SuiteSearchCategories.SentinelDatabase)))
        {
            var sentinelMatches = await sentinelWorkspaceService.SearchAsync(text, performedBy, cancellationToken: cancellationToken);
            results.AddRange(sentinelMatches
                .Where(item => parsed.Wants(item.IsDatabase ? SuiteSearchCategories.SentinelDatabase : SuiteSearchCategories.SentinelPage))
                .Take(perCategory).Select(item => new SuiteSearchResult(
                    string.IsNullOrWhiteSpace(item.Title) ? "Untitled" : item.Title,
                    item.Preview,
                    item.IsDatabase ? SuiteSearchCategories.SentinelDatabase : SuiteSearchCategories.SentinelPage,
                    item.IsDatabase
                        ? $"/admin/sentinel?database={item.Id}{(item.SourceRowId is { } rowId ? $"&row={rowId}" : string.Empty)}"
                        : $"/admin/sentinel?page={item.Id}",
                    item.IsDatabase ? "bi bi-grid-3x3-gap-fill" : "bi bi-file-earmark-text")));
        }

        if (hybridSearchService is not null && text.Length >= 2)
        {
            IReadOnlyList<SemanticSearchHit> hybridMatches;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(SemanticBudget);
            try
            {
                hybridMatches = await hybridSearchService.SearchAsync(
                    text,
                    [SemanticSourceTypes.CrmContact, SemanticSourceTypes.CrmDeal, SemanticSourceTypes.CmsPage],
                    PerCategoryLimit * 3,
                    budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                hybridMatches = [];
            }
            results.AddRange(hybridMatches.Select(item => item.SourceType switch
            {
                SemanticSourceTypes.CrmContact => new SuiteSearchResult(
                    item.Title, item.Preview, SuiteSearchCategories.CrmContact, $"/admin/crm/contacts/{item.SourceId}", "bi bi-person-lines-fill"),
                SemanticSourceTypes.CrmDeal => new SuiteSearchResult(
                    item.Title, item.Preview, SuiteSearchCategories.CrmDeal, "/admin/crm", "bi bi-graph-up-arrow"),
                SemanticSourceTypes.CmsPage => new SuiteSearchResult(
                    item.Title, item.Preview, SuiteSearchCategories.CmsPage, $"/admin/pages/edit/{item.SourceId}", "bi bi-file-earmark-richtext"),
                _ => null
            }).OfType<SuiteSearchResult>().Where(item => parsed.Wants(item.Category)));
        }

        // Each record opens itself: a contact's page, the ticket or invoice in its list's
        // inspector, the article's editor. Deals have no page of their own, so they open CRM.
        if (parsed.Wants(SuiteSearchCategories.CrmContact))
        {
            var contacts = await db.Contacts.AsNoTracking().Where(item => item.TrashedAt == null)
                .Take(ModuleScanLimit).Select(item => new { item.Id, item.FullName, item.Company, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(contacts.Where(item => Matches(text, item.FullName, item.Company)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.FullName, item.Company ?? "CRM contact", SuiteSearchCategories.CrmContact, $"/admin/crm/contacts/{item.Id}", "bi bi-person-lines-fill")));
        }

        if (parsed.Wants(SuiteSearchCategories.CrmDeal))
        {
            var deals = await db.Deals.AsNoTracking()
                .Take(ModuleScanLimit).Select(item => new { item.Title, item.Stage, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(deals.Where(item => Matches(text, item.Title, item.Stage)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.Title, $"Stage: {item.Stage}", SuiteSearchCategories.CrmDeal, "/admin/crm", "bi bi-graph-up-arrow")));
        }

        if (parsed.Wants(SuiteSearchCategories.Automation))
        {
            var workflows = await db.AutomationWorkflows.AsNoTracking()
                .Take(ModuleScanLimit).Select(item => new { item.Id, item.Name, item.Description, item.Status, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(workflows.Where(item => Matches(text, item.Name, item.Description)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.Name, $"{item.Status} workflow", SuiteSearchCategories.Automation, $"/admin/automation/{item.Id}", "bi bi-diagram-3-fill")));
        }

        if (parsed.Wants(SuiteSearchCategories.CmsPage))
        {
            var pages = await db.CmsPages.AsNoTracking().Where(item => item.TrashedAt == null)
                .Take(ModuleScanLimit).Select(item => new { item.Id, item.Title, item.Slug, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(pages.Where(item => Matches(text, item.Title, item.Slug)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.Title, $"/{item.Slug}", SuiteSearchCategories.CmsPage, $"/admin/pages/edit/{item.Id}", "bi bi-file-earmark-richtext")));
        }

        if (parsed.Wants(SuiteSearchCategories.Article))
        {
            var articles = await db.Articles.AsNoTracking().Where(item => item.TrashedAt == null)
                .Take(ModuleScanLimit).Select(item => new { item.Id, item.Title, item.Topic, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(articles.Where(item => Matches(text, item.Title, item.Topic)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.Title, item.Topic ?? "Article", SuiteSearchCategories.Article, $"/admin/article-editor/{item.Id}", "bi bi-newspaper")));
        }

        if (parsed.Wants(SuiteSearchCategories.AffiliateOffer))
        {
            var offers = await db.AffiliateOffers.AsNoTracking()
                .Take(ModuleScanLimit).Select(item => new { item.LinkName, item.AdvertiserName }).ToListAsync(cancellationToken);
            results.AddRange(offers.Where(item => Matches(text, item.LinkName, item.AdvertiserName)).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.LinkName, item.AdvertiserName, SuiteSearchCategories.AffiliateOffer, "/admin/cj-ads", "bi bi-link-45deg")));
        }

        // Support tickets and billing invoices were the two areas the September audit flagged
        // as the highest-friction gaps in ⌘K search - the two feature areas most likely to need
        // "find this specific record right now" rather than browsing.
        if (parsed.Wants(SuiteSearchCategories.SupportTicket))
        {
            var tickets = await db.SupportTickets.AsNoTracking()
                .Take(ModuleScanLimit).Select(item => new { item.Id, item.Subject, item.Status, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(tickets.Where(item => Matches(text, item.Subject)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.Subject, $"{item.Status} ticket", SuiteSearchCategories.SupportTicket, $"/admin/support?ticket={item.Id}", "bi bi-life-preserver")));
        }

        if (parsed.Wants(SuiteSearchCategories.BillingInvoice))
        {
            var invoices = await db.Invoices.AsNoTracking()
                .Take(ModuleScanLimit).Select(item => new { item.Id, item.Title, item.Status, item.CreatedAt }).ToListAsync(cancellationToken);
            results.AddRange(Newest(invoices.Where(item => Matches(text, item.Title)), item => item.CreatedAt, text).Take(perCategory)
                .Select(item => new SuiteSearchResult(item.Title, $"{item.Status} invoice", SuiteSearchCategories.BillingInvoice, $"/admin/billing?invoice={item.Id}", "bi bi-receipt")));
        }

        return results
            .DistinctBy(item => (item.Category, item.Title, item.Url))
            .Take(Math.Clamp(take, 1, 40))
            .ToList();
    }

    // SentinelGPT's find_records call (tech-forward T1b), run as one structured search. Every
    // filter is applied after materializing (SQLite can't compare DateTimeOffset), newest first.
    public async Task<IReadOnlyList<SuiteSearchResult>> FindAsync(SuiteSearchRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var since = request.WithinDays is { } days ? now.AddDays(-days) : (DateTimeOffset?)null;
        bool Recent(DateTimeOffset createdAt) => since is null || createdAt >= since;
        bool StatusIs(string? status) => request.Status is null || string.Equals(status, request.Status, StringComparison.OrdinalIgnoreCase);

        // Tickets, invoices and deals belong to a contact: the text also matches the contact's name
        // (models often put "Dana" in text for "invoices for Dana"), and contact narrows to them.
        var names = new Dictionary<Guid, string>();
        HashSet<Guid>? contactIds = null;
        if (request.Kind is "tickets" or "invoices" or "deals")
        {
            names = (await db.Contacts.AsNoTracking().Take(ModuleScanLimit).Select(item => new { item.Id, item.FullName }).ToListAsync(cancellationToken))
                .ToDictionary(item => item.Id, item => item.FullName);
            if (request.Contact is { Length: > 0 } contactName)
            {
                contactIds = names.Where(item => Matches(contactName, item.Value)).Select(item => item.Key).ToHashSet();
                if (contactIds.Count == 0) return [];
            }
        }
        bool Belongs(Guid contactId) => contactIds is null || contactIds.Contains(contactId);
        string NameOf(Guid contactId) => names.GetValueOrDefault(contactId, string.Empty);

        IEnumerable<SuiteSearchResult> results = request.Kind switch
        {
            "contacts" => (await db.Contacts.AsNoTracking().Where(item => item.TrashedAt == null).Take(ModuleScanLimit)
                    .Select(item => new { item.Id, item.FullName, item.Company, item.Email, item.Status, item.FollowUpDate, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.FullName, item.Company, item.Email) && StatusIs(item.Status) && Recent(item.CreatedAt)
                    && (!request.OverdueOnly || item.FollowUpDate is { } due && due <= now))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.FullName, $"{item.Status}{(item.Company is { Length: > 0 } company ? $" · {company}" : string.Empty)}",
                    SuiteSearchCategories.CrmContact, $"/admin/crm/contacts/{item.Id}", "bi bi-person-lines-fill")),
            "deals" => (await db.Deals.AsNoTracking().Take(ModuleScanLimit)
                    .Select(item => new { item.Title, item.Stage, item.ContactId, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.Title, NameOf(item.ContactId)) && StatusIs(item.Stage) && Recent(item.CreatedAt) && Belongs(item.ContactId))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.Title, $"Stage: {item.Stage}", SuiteSearchCategories.CrmDeal, "/admin/crm", "bi bi-graph-up-arrow")),
            "tickets" => (await db.SupportTickets.AsNoTracking().Take(ModuleScanLimit)
                    .Select(item => new { item.Id, item.Subject, item.Status, item.Priority, item.ContactId, item.ResolutionDueAt, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.Subject, NameOf(item.ContactId)) && StatusIs(item.Status) && Recent(item.CreatedAt) && Belongs(item.ContactId)
                    && (request.Priority is null || string.Equals(item.Priority, request.Priority, StringComparison.OrdinalIgnoreCase))
                    && (!request.OverdueOnly || item.Status is "Open" or "Pending" && item.ResolutionDueAt is { } due && due < now))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.Subject, $"{item.Status} · {item.Priority}", SuiteSearchCategories.SupportTicket, $"/admin/support?ticket={item.Id}", "bi bi-life-preserver")),
            "invoices" => (await db.Invoices.AsNoTracking().Take(ModuleScanLimit)
                    .Select(item => new { item.Id, item.Title, item.Status, item.ContactId, item.DueDate, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.Title, NameOf(item.ContactId)) && StatusIs(item.Status) && Recent(item.CreatedAt) && Belongs(item.ContactId)
                    && (!request.OverdueOnly || item.Status == "Sent" && item.DueDate is { } due && due < now))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.Title, $"{item.Status} invoice", SuiteSearchCategories.BillingInvoice, $"/admin/billing?invoice={item.Id}", "bi bi-receipt")),
            "posts" => (await db.Articles.AsNoTracking().Where(item => item.TrashedAt == null).Take(ModuleScanLimit)
                    .Select(item => new { item.Id, item.Title, item.Topic, item.Status, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.Title, item.Topic) && StatusIs(item.Status) && Recent(item.CreatedAt))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.Title, $"{item.Status} post", SuiteSearchCategories.Article, $"/admin/article-editor/{item.Id}", "bi bi-newspaper")),
            "pages" => (await db.CmsPages.AsNoTracking().Where(item => item.TrashedAt == null).Take(ModuleScanLimit)
                    .Select(item => new { item.Id, item.Title, item.Slug, item.Status, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.Title, item.Slug) && StatusIs(item.Status) && Recent(item.CreatedAt))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.Title, $"{item.Status} · /{item.Slug}", SuiteSearchCategories.CmsPage, $"/admin/pages/edit/{item.Id}", "bi bi-file-earmark-richtext")),
            "workflows" => (await db.AutomationWorkflows.AsNoTracking().Take(ModuleScanLimit)
                    .Select(item => new { item.Id, item.Name, item.Description, item.Status, item.CreatedAt }).ToListAsync(cancellationToken))
                .Where(item => Matches(request.Text, item.Name, item.Description) && StatusIs(item.Status) && Recent(item.CreatedAt))
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new SuiteSearchResult(item.Name, $"{item.Status} workflow", SuiteSearchCategories.Automation, $"/admin/automation/{item.Id}", "bi bi-diagram-3-fill")),
            _ => []
        };
        return results.Take(FilteredCategoryLimit).ToList();
    }

    // A filter with no text lists the newest records first (sorted here: SQLite can't order
    // DateTimeOffset); a text search keeps the scan's order.
    private static IEnumerable<T> Newest<T>(IEnumerable<T> items, Func<T, DateTimeOffset> createdAt, string text) =>
        text.Length == 0 ? items.OrderByDescending(createdAt) : items;

    private static bool Matches(string query, params string?[] values) =>
        query.Length == 0 || values.Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));
}
