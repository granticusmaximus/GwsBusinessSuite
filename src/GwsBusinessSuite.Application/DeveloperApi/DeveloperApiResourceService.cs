using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.DeveloperApi;

public sealed class DeveloperApiResourceService(
    IAppDbContext db,
    ICmsBuilderService cmsBuilderService,
    TimeProvider timeProvider) : IDeveloperApiResourceService
{
    public async Task<DeveloperApiPage<DeveloperApiContact>> ListContactsAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        (page, pageSize) = ValidatePage(page, pageSize);
        var query = db.Contacts.AsNoTracking().Where(item => item.TrashedAt == null);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(item => item.FullName).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<DeveloperApiContact?> GetContactAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await db.Contacts.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.TrashedAt == null, cancellationToken)) is { } row
            ? Map(row)
            : null;

    public async Task<DeveloperApiContact> CreateContactAsync(
        DeveloperApiContactInput input, string actor, CancellationToken cancellationToken = default)
    {
        Validate(input);
        var row = new Contact
        {
            FullName = input.FullName.Trim(),
            Email = Clean(input.Email),
            Company = Clean(input.Company),
            Status = input.Status,
            FollowUpDate = input.FollowUpDate,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = actor
        };
        await db.Contacts.AddAsync(row, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<DeveloperApiContact?> UpdateContactAsync(
        Guid id, DeveloperApiContactInput input, string actor, CancellationToken cancellationToken = default)
    {
        Validate(input);
        var row = await db.Contacts.FirstOrDefaultAsync(item => item.Id == id && item.TrashedAt == null, cancellationToken);
        if (row is null) return null;
        row.FullName = input.FullName.Trim();
        row.Email = Clean(input.Email);
        row.Company = Clean(input.Company);
        row.Status = input.Status;
        row.FollowUpDate = input.FollowUpDate;
        Touch(row, actor);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<DeveloperApiPage<DeveloperApiDeal>> ListDealsAsync(
        int page, int pageSize, CancellationToken cancellationToken = default)
    {
        (page, pageSize) = ValidatePage(page, pageSize);
        var query = db.Deals.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        // DateTimeOffset ordering is unsupported by SQLite, so use stable title/id ordering.
        var rows = await query.OrderBy(item => item.Title).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<DeveloperApiDeal?> GetDealAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await db.Deals.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken)) is { } row ? Map(row) : null;

    public async Task<DeveloperApiDeal> CreateDealAsync(
        DeveloperApiDealInput input, string actor, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(input, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var row = new Deal
        {
            ContactId = input.ContactId,
            Title = input.Title.Trim(),
            Stage = input.Stage,
            ValueUsd = input.ValueUsd,
            ExpectedCloseDate = input.ExpectedCloseDate,
            ClosedAt = DealStages.Open.Contains(input.Stage) ? null : now,
            Notes = input.Notes?.Trim() ?? string.Empty,
            CreatedAt = now,
            CreatedAtUnixSeconds = now.ToUnixTimeSeconds(),
            CreatedBy = actor
        };
        await db.Deals.AddAsync(row, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<DeveloperApiDeal?> UpdateDealAsync(
        Guid id, DeveloperApiDealInput input, string actor, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(input, cancellationToken);
        var row = await db.Deals.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null) return null;
        var wasOpen = DealStages.Open.Contains(row.Stage);
        row.ContactId = input.ContactId;
        row.Title = input.Title.Trim();
        row.Stage = input.Stage;
        row.ValueUsd = input.ValueUsd;
        row.ExpectedCloseDate = input.ExpectedCloseDate;
        row.Notes = input.Notes?.Trim() ?? string.Empty;
        if (wasOpen && !DealStages.Open.Contains(input.Stage)) row.ClosedAt = timeProvider.GetUtcNow();
        if (DealStages.Open.Contains(input.Stage)) row.ClosedAt = null;
        Touch(row, actor);
        await db.SaveChangesAsync(cancellationToken);
        return Map(row);
    }

    public async Task<DeveloperApiPage<DeveloperApiCmsPage>> ListCmsPagesAsync(
        int page, int pageSize, Guid? siteId, CancellationToken cancellationToken = default)
    {
        (page, pageSize) = ValidatePage(page, pageSize);
        var query = db.CmsPages.AsNoTracking().Where(item => item.TrashedAt == null);
        if (siteId.HasValue) query = query.Where(item => item.SiteId == siteId.Value);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(item => item.Title).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new(rows.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<DeveloperApiCmsPage?> GetCmsPageAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await db.CmsPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id && item.TrashedAt == null, cancellationToken)) is { } row
            ? Map(row)
            : null;

    public async Task<DeveloperApiCmsPage> CreateCmsPageAsync(
        DeveloperApiCmsPageInput input, string actor, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(input, null, cancellationToken);
        var row = await cmsBuilderService.SavePageAsync(await ToEditorAsync(input, null, cancellationToken), actor, cancellationToken);
        return Map(row);
    }

    public async Task<DeveloperApiCmsPage?> UpdateCmsPageAsync(
        Guid id, DeveloperApiCmsPageInput input, string actor, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(input, id, cancellationToken);
        if (!await db.CmsPages.AnyAsync(item => item.Id == id && item.TrashedAt == null, cancellationToken)) return null;
        var row = await cmsBuilderService.SavePageAsync(await ToEditorAsync(input, id, cancellationToken), actor, cancellationToken);
        return Map(row);
    }

    private async Task ValidateAsync(DeveloperApiDealInput input, CancellationToken cancellationToken)
    {
        if (input.ContactId == Guid.Empty || !await db.Contacts.AnyAsync(item => item.Id == input.ContactId && item.TrashedAt == null, cancellationToken))
            throw new InvalidOperationException("contactId must identify an active contact.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 200)
            throw new InvalidOperationException("title is required and must not exceed 200 characters.");
        if (!DealStages.All.Contains(input.Stage))
            throw new InvalidOperationException($"stage must be one of: {string.Join(", ", DealStages.All)}.");
        if (input.ValueUsd < 0) throw new InvalidOperationException("valueUsd cannot be negative.");
    }

    private async Task ValidateAsync(DeveloperApiCmsPageInput input, Guid? existingId, CancellationToken cancellationToken)
    {
        if (input.SiteId == Guid.Empty || !await db.CmsSites.AnyAsync(item => item.Id == input.SiteId, cancellationToken))
            throw new InvalidOperationException("siteId must identify an existing CMS site.");
        if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 200)
            throw new InvalidOperationException("title is required and must not exceed 200 characters.");
        if (string.IsNullOrWhiteSpace(input.Slug)) throw new InvalidOperationException("slug is required.");
        var slug = NormalizeSlug(input.Slug);
        if (slug.Length is < 1 or > 200) throw new InvalidOperationException("slug is required and must not exceed 200 characters.");
        if (!CmsPageStatuses.Draft.Equals(input.Status, StringComparison.Ordinal) &&
            !CmsPageStatuses.Published.Equals(input.Status, StringComparison.Ordinal))
            throw new InvalidOperationException("status must be Draft or Published.");
        if (string.IsNullOrWhiteSpace(input.BlocksJson)) throw new InvalidOperationException("blocksJson is required.");
        if (input.BlocksJson.Length > 1_000_000) throw new InvalidOperationException("blocksJson must not exceed 1 MB.");
        try { using var _ = JsonDocument.Parse(input.BlocksJson); }
        catch (JsonException) { throw new InvalidOperationException("blocksJson must contain valid JSON."); }
        if (existingId.HasValue && input.ParentPageId == existingId) throw new InvalidOperationException("A page cannot be its own parent.");
        if (input.CategoryId is { } categoryId && !await db.CmsPageCategories.AnyAsync(
                item => item.Id == categoryId && item.SiteId == input.SiteId, cancellationToken))
            throw new InvalidOperationException("categoryId must identify a category on the selected site.");
    }

    private static void Validate(DeveloperApiContactInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Trim().Length > 200)
            throw new InvalidOperationException("fullName is required and must not exceed 200 characters.");
        if (!ContactStatuses.All.Contains(input.Status))
            throw new InvalidOperationException($"status must be one of: {string.Join(", ", ContactStatuses.All)}.");
        if (input.Email?.Length > 320) throw new InvalidOperationException("email must not exceed 320 characters.");
        if (input.Company?.Length > 200) throw new InvalidOperationException("company must not exceed 200 characters.");
    }

    private static (int Page, int PageSize) ValidatePage(int page, int pageSize)
    {
        if (page < 1) throw new InvalidOperationException("page must be at least 1.");
        if (pageSize is < 1 or > 100) throw new InvalidOperationException("pageSize must be between 1 and 100.");
        return (page, pageSize);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeSlug(string value) => value.Trim().Trim('/').ToLowerInvariant().Replace(' ', '-');

    private async Task<CmsPageEditorModel> ToEditorAsync(
        DeveloperApiCmsPageInput input,
        Guid? pageId,
        CancellationToken cancellationToken)
    {
        var categoryName = input.CategoryId is { } categoryId
            ? await db.CmsPageCategories.Where(item => item.Id == categoryId).Select(item => item.Name).SingleAsync(cancellationToken)
            : string.Empty;
        return new CmsPageEditorModel
        {
            PageId = pageId,
            SiteId = input.SiteId,
            ParentPageId = input.ParentPageId,
            Title = input.Title,
            Slug = input.Slug,
            BlocksJson = input.BlocksJson,
            MetaTitle = input.MetaTitle,
            MetaDescription = input.MetaDescription,
            OgImageUrl = input.OgImageUrl,
            CanonicalUrl = input.CanonicalUrl,
            CategoryName = categoryName,
            Tags = input.Tags,
            CustomCss = input.CustomCss,
            Status = input.Status,
            PublishedAt = input.PublishedAt,
            PropertyValues = input.PropertyValues ?? []
        };
    }

    private void Touch(GwsBusinessSuite.Domain.Common.AuditableEntity row, string actor)
    {
        row.UpdatedAt = timeProvider.GetUtcNow();
        row.UpdatedBy = actor;
    }

    private static DeveloperApiContact Map(Contact row) => new(row.Id, row.FullName, row.Email, row.Company, row.Status,
        row.FollowUpDate, row.CreatedAt, row.UpdatedAt);
    private static DeveloperApiDeal Map(Deal row) => new(row.Id, row.ContactId, row.Title, row.Stage, row.ValueUsd,
        row.ExpectedCloseDate, row.ClosedAt, row.Notes, row.CreatedAt, row.UpdatedAt);
    private static DeveloperApiCmsPage Map(CmsPage row) => new(row.Id, row.SiteId, row.ParentPageId, row.CategoryId,
        row.Title, row.Slug, row.BlocksJson, row.MetaTitle, row.MetaDescription, row.OgImageUrl, row.CanonicalUrl,
        row.Tags, row.CustomCss, ParsePropertyValues(row.PropertyValuesJson), row.Status, row.PublishedAt, row.CreatedAt, row.UpdatedAt);

    private static IReadOnlyDictionary<Guid, string> ParsePropertyValues(string json) =>
        CmsPropertyValues.ParseObject(json)
            .Where(pair => Guid.TryParse(pair.Key, out _) && pair.Value is not null)
            .ToDictionary(pair => Guid.Parse(pair.Key), pair => pair.Value!.GetValue<string>());
}
