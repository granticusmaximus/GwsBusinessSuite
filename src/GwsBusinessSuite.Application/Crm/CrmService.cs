using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Application.Crm;

public sealed class CrmService(
    IAppDbContext dbContext,
    ICurrentUserAccessor? currentUserAccessor = null,
    IMemoryCache? cache = null) : ICrmService
{
    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;
    // Falls back to a private, per-instance cache when constructed without DI (existing
    // tests new this up directly with just a db/user accessor) - same optional-dependency
    // pattern as currentUserAccessor above.
    private readonly IMemoryCache _cache = cache ?? new MemoryCache(new MemoryCacheOptions());

    // GetDashboardAsync loads every non-trashed contact on every /admin/crm page load with
    // no filtering - fine at a handful of users' worth of contacts, but a flat unindexed
    // full-table read that's worth capping. A short TTL (rather than event-driven
    // invalidation on every contact/deal write) keeps this simple: at most one page load
    // per window pays the real query cost, and staleness is bounded to a few seconds.
    private const string DashboardCacheKey = "crm:dashboard";
    private static readonly TimeSpan DashboardCacheDuration = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<Contact>> ListContactsAsync(bool includeTrashed = false, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Contacts.AsNoTracking();
        if (!includeTrashed)
        {
            query = query.Where(contact => contact.TrashedAt == null);
        }

        var contacts = await query.ToListAsync(cancellationToken);

        return contacts
            .OrderByDescending(contact => contact.UpdatedAt ?? contact.CreatedAt)
            .ThenBy(contact => contact.FullName)
            .ToList();
    }

    public async Task<IReadOnlyList<Contact>> ListTrashedContactsAsync(CancellationToken cancellationToken = default)
    {
        var contacts = await dbContext.Contacts
            .AsNoTracking()
            .Where(contact => contact.TrashedAt != null)
            .ToListAsync(cancellationToken);

        return contacts
            .OrderByDescending(contact => contact.TrashedAt)
            .ToList();
    }

    // Excludes trashed contacts - opening a stale link/tab for a contact that's since
    // been trashed should behave as "not found", not silently show/operate on it.
    public async Task<Contact?> GetContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        return await dbContext.Contacts
            .AsNoTracking()
            .FirstOrDefaultAsync(contact => contact.Id == contactId && contact.TrashedAt == null, cancellationToken);
    }

    public async Task<Contact> SaveContactAsync(ContactEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var contact = editor.ContactId is { } contactId
            ? await dbContext.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken)
            : null;

        if (contact is { TrashedAt: not null })
        {
            // A stale editor tab (opened before the contact was trashed elsewhere)
            // shouldn't be able to silently keep editing a trashed record - restore it
            // first via RestoreContactAsync.
            throw new InvalidOperationException("This contact has been moved to Trash. Restore it before saving changes.");
        }

        var isNew = contact is null;
        contact ??= new Contact
        {
            FullName = string.Empty,
            CreatedAt = now,
            CreatedBy = performedBy
        };

        contact.FullName = editor.FullName.Trim();
        contact.Email = string.IsNullOrWhiteSpace(editor.Email) ? null : editor.Email.Trim();
        contact.Company = string.IsNullOrWhiteSpace(editor.Company) ? null : editor.Company.Trim();
        contact.Status = string.IsNullOrWhiteSpace(editor.Status) ? ContactStatuses.Lead : editor.Status.Trim();
        // Normalize to UTC midnight of the picked calendar date. Blazor's InputDate binds
        // a plain "YYYY-MM-DD" string to DateTimeOffset by resolving the missing offset
        // against the server process's local timezone (not the browser user's) - taking
        // just the .Date component and re-anchoring it to TimeSpan.Zero keeps the actual
        // calendar date the user picked, but makes "due" (ListDueFollowUpsAsync) trigger
        // at a single deterministic UTC instant regardless of server timezone config.
        contact.FollowUpDate = editor.FollowUpDate is { } followUpDate
            ? new DateTimeOffset(followUpDate.Date, TimeSpan.Zero)
            : null;
        contact.UpdatedAt = now;
        contact.UpdatedBy = performedBy;

        if (isNew)
        {
            await dbContext.Contacts.AddAsync(contact, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
        return contact;
    }

    public async Task TrashContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await dbContext.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken);
        if (contact is null || contact.TrashedAt is not null)
        {
            return;
        }

        contact.TrashedAt = DateTimeOffset.UtcNow;
        contact.UpdatedAt = contact.TrashedAt;
        contact.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
    }

    public async Task RestoreContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await dbContext.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken);
        if (contact is null || contact.TrashedAt is null)
        {
            return;
        }

        contact.TrashedAt = null;
        contact.UpdatedAt = DateTimeOffset.UtcNow;
        contact.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
    }

    public async Task DeleteContactPermanentlyAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await dbContext.Contacts.FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken);
        if (contact is null)
        {
            return;
        }

        dbContext.Contacts.Remove(contact);
        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
    }

    public async Task<IReadOnlyList<ContactActivityView>> ListActivitiesAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var activities = await dbContext.ContactActivities
            .AsNoTracking()
            .Where(activity => activity.ContactId == contactId)
            .ToListAsync(cancellationToken);

        // SQLite can't translate ORDER BY on a DateTimeOffset column, so order
        // client-side after materializing (same pattern used elsewhere in this app).
        return activities
            .OrderByDescending(activity => activity.CreatedAt)
            .Select(activity => new ContactActivityView
            {
                Id = activity.Id,
                Note = activity.Note,
                CreatedAt = activity.CreatedAt,
                CreatedBy = activity.CreatedBy
            })
            .ToList();
    }

    public async Task<ContactActivityView> AddActivityAsync(Guid contactId, string note, CancellationToken cancellationToken = default)
    {
        var trimmedNote = (note ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedNote))
        {
            throw new ArgumentException("Note cannot be empty.", nameof(note));
        }

        var contactExists = await dbContext.Contacts.AnyAsync(c => c.Id == contactId && c.TrashedAt == null, cancellationToken);
        if (!contactExists)
        {
            throw new InvalidOperationException("The contact this activity belongs to no longer exists.");
        }

        var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var activity = new ContactActivity
        {
            ContactId = contactId,
            Note = trimmedNote,
            CreatedBy = performedBy
        };

        await dbContext.ContactActivities.AddAsync(activity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new ContactActivityView
        {
            Id = activity.Id,
            Note = activity.Note,
            CreatedAt = activity.CreatedAt,
            CreatedBy = activity.CreatedBy
        };
    }

    public async Task<IReadOnlyList<Contact>> ListDueFollowUpsAsync(CancellationToken cancellationToken = default)
    {
        // SQLite can't translate range comparisons (<=/>=) on DateTimeOffset columns
        // (nor ORDER BY on one) - the null check is fine server-side, but the actual
        // date-range filter and ordering both happen in memory below.
        var candidates = await dbContext.Contacts
            .AsNoTracking()
            .Where(contact => contact.TrashedAt == null && contact.FollowUpDate != null)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        return candidates
            .Where(contact => contact.FollowUpDate <= now)
            .OrderBy(contact => contact.FollowUpDate)
            .ToList();
    }

    public async Task<CrmDashboardData> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(DashboardCacheKey, out CrmDashboardData? cached) && cached is not null)
            return cached;

        var contacts = await dbContext.Contacts
            .AsNoTracking()
            .Where(contact => contact.TrashedAt == null)
            .ToListAsync(cancellationToken);

        var orderedContacts = contacts
            .OrderByDescending(contact => contact.UpdatedAt ?? contact.CreatedAt)
            .ThenBy(contact => contact.FullName)
            .ToList();
        var now = DateTimeOffset.UtcNow;
        var dueFollowUps = contacts
            .Where(contact => contact.FollowUpDate <= now)
            .OrderBy(contact => contact.FollowUpDate)
            .ToList();
        var openDeals = await ListDealsAsync(cancellationToken);

        var dashboard = new CrmDashboardData(
            orderedContacts, dueFollowUps, openDeals.Where(deal => DealStages.Open.Contains(deal.Stage)).ToList());
        _cache.Set(DashboardCacheKey, dashboard, DashboardCacheDuration);
        return dashboard;
    }

    public async Task<IReadOnlyList<DealView>> ListDealsAsync(CancellationToken cancellationToken = default)
    {
        var deals = await dbContext.Deals.AsNoTracking().ToListAsync(cancellationToken);
        if (deals.Count == 0) return [];

        var contactNames = await dbContext.Contacts.AsNoTracking()
            .Where(contact => deals.Select(deal => deal.ContactId).Contains(contact.Id))
            .Select(contact => new { contact.Id, contact.FullName })
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);

        return deals
            .OrderByDescending(deal => deal.CreatedAt)
            .Select(deal => ToDealView(deal, contactNames.GetValueOrDefault(deal.ContactId, "Unknown contact")))
            .ToList();
    }

    public async Task<IReadOnlyList<DealView>> ListDealsForContactAsync(Guid contactId, CancellationToken cancellationToken = default)
    {
        var contact = await dbContext.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == contactId, cancellationToken);
        var deals = await dbContext.Deals.AsNoTracking()
            .Where(deal => deal.ContactId == contactId)
            .ToListAsync(cancellationToken);

        return deals
            .OrderByDescending(deal => deal.CreatedAt)
            .Select(deal => ToDealView(deal, contact?.FullName ?? "Unknown contact"))
            .ToList();
    }

    public async Task<DealView> SaveDealAsync(DealEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var contact = await dbContext.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == editor.ContactId, cancellationToken)
            ?? throw new InvalidOperationException("The selected contact no longer exists.");

        var now = DateTimeOffset.UtcNow;
        var performedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var deal = editor.DealId is { } dealId
            ? await dbContext.Deals.FirstOrDefaultAsync(item => item.Id == dealId, cancellationToken)
                ?? throw new KeyNotFoundException("Deal was not found.")
            : new Deal { ContactId = editor.ContactId, Title = string.Empty, CreatedAt = now, CreatedBy = performedBy };
        var isNew = editor.DealId is null;

        deal.ContactId = editor.ContactId;
        deal.Title = editor.Title.Trim();
        deal.Stage = string.IsNullOrWhiteSpace(editor.Stage) ? DealStages.Lead : editor.Stage.Trim();
        deal.ValueUsd = editor.ValueUsd;
        deal.ExpectedCloseDate = editor.ExpectedCloseDate is { } closeDate
            ? new DateTimeOffset(closeDate.Date, TimeSpan.Zero)
            : null;
        deal.Notes = editor.Notes?.Trim() ?? string.Empty;
        deal.ClosedAt = deal.Stage is DealStages.Won or DealStages.Lost ? deal.ClosedAt ?? now : null;
        deal.UpdatedAt = now;
        deal.UpdatedBy = performedBy;

        if (isNew) await dbContext.Deals.AddAsync(deal, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
        return ToDealView(deal, contact.FullName);
    }

    public async Task<DealView> SetDealStageAsync(Guid dealId, string stage, CancellationToken cancellationToken = default)
    {
        if (!DealStages.All.Contains(stage)) throw new ArgumentException($"'{stage}' is not a recognized deal stage.", nameof(stage));

        var deal = await dbContext.Deals.FirstOrDefaultAsync(item => item.Id == dealId, cancellationToken)
            ?? throw new KeyNotFoundException("Deal was not found.");
        var contact = await dbContext.Contacts.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == deal.ContactId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        deal.Stage = stage;
        deal.ClosedAt = stage is DealStages.Won or DealStages.Lost ? now : null;
        deal.UpdatedAt = now;
        deal.UpdatedBy = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
        return ToDealView(deal, contact?.FullName ?? "Unknown contact");
    }

    public async Task DeleteDealAsync(Guid dealId, CancellationToken cancellationToken = default)
    {
        var deal = await dbContext.Deals.FirstOrDefaultAsync(item => item.Id == dealId, cancellationToken);
        if (deal is null) return;

        dbContext.Deals.Remove(deal);
        await dbContext.SaveChangesAsync(cancellationToken);
        _cache.Remove(DashboardCacheKey);
    }

    private static DealView ToDealView(Deal deal, string contactName) => new()
    {
        Id = deal.Id,
        ContactId = deal.ContactId,
        ContactName = contactName,
        Title = deal.Title,
        Stage = deal.Stage,
        ValueUsd = deal.ValueUsd,
        ExpectedCloseDate = deal.ExpectedCloseDate,
        ClosedAt = deal.ClosedAt,
        Notes = deal.Notes,
        CreatedAt = deal.CreatedAt
    };

    public async Task<int> CountDueFollowUpsAsync(CancellationToken cancellationToken = default)
    {
        var due = await ListDueFollowUpsAsync(cancellationToken);
        return due.Count;
    }
}
