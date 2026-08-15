namespace GwsBusinessSuite.Application.DeveloperApi;

public sealed record DeveloperApiPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public interface IDeveloperApiResource
{
    Guid Id { get; }
}

public sealed record DeveloperApiContact(
    Guid Id,
    string FullName,
    string? Email,
    string? Company,
    string Status,
    DateTimeOffset? FollowUpDate,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt) : IDeveloperApiResource;

public sealed class DeveloperApiContactInput
{
    public string FullName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Company { get; set; }
    public string Status { get; set; } = "Lead";
    public DateTimeOffset? FollowUpDate { get; set; }
}

public sealed record DeveloperApiDeal(
    Guid Id,
    Guid ContactId,
    string Title,
    string Stage,
    decimal ValueUsd,
    DateTimeOffset? ExpectedCloseDate,
    DateTimeOffset? ClosedAt,
    string Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt) : IDeveloperApiResource;

public sealed class DeveloperApiDealInput
{
    public Guid ContactId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Stage { get; set; } = "Lead";
    public decimal ValueUsd { get; set; }
    public DateTimeOffset? ExpectedCloseDate { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed record DeveloperApiCmsPage(
    Guid Id,
    Guid SiteId,
    Guid? ParentPageId,
    Guid? CategoryId,
    string Title,
    string Slug,
    string BlocksJson,
    string MetaTitle,
    string MetaDescription,
    string OgImageUrl,
    string CanonicalUrl,
    string Tags,
    string CustomCss,
    IReadOnlyDictionary<Guid, string> PropertyValues,
    string Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt) : IDeveloperApiResource;

public sealed class DeveloperApiCmsPageInput
{
    public Guid SiteId { get; set; }
    public Guid? ParentPageId { get; set; }
    public Guid? CategoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string BlocksJson { get; set; } = "[]";
    public string MetaTitle { get; set; } = string.Empty;
    public string MetaDescription { get; set; } = string.Empty;
    public string OgImageUrl { get; set; } = string.Empty;
    public string CanonicalUrl { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string CustomCss { get; set; } = string.Empty;
    public Dictionary<Guid, string> PropertyValues { get; set; } = [];
    public string Status { get; set; } = "Draft";
    public DateTimeOffset? PublishedAt { get; set; }
}

public interface IDeveloperApiResourceService
{
    Task<DeveloperApiPage<DeveloperApiContact>> ListContactsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<DeveloperApiContact?> GetContactAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DeveloperApiContact> CreateContactAsync(DeveloperApiContactInput input, string actor, CancellationToken cancellationToken = default);
    Task<DeveloperApiContact?> UpdateContactAsync(Guid id, DeveloperApiContactInput input, string actor, CancellationToken cancellationToken = default);

    Task<DeveloperApiPage<DeveloperApiDeal>> ListDealsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<DeveloperApiDeal?> GetDealAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DeveloperApiDeal> CreateDealAsync(DeveloperApiDealInput input, string actor, CancellationToken cancellationToken = default);
    Task<DeveloperApiDeal?> UpdateDealAsync(Guid id, DeveloperApiDealInput input, string actor, CancellationToken cancellationToken = default);

    Task<DeveloperApiPage<DeveloperApiCmsPage>> ListCmsPagesAsync(int page, int pageSize, Guid? siteId, CancellationToken cancellationToken = default);
    Task<DeveloperApiCmsPage?> GetCmsPageAsync(Guid id, CancellationToken cancellationToken = default);
    Task<DeveloperApiCmsPage> CreateCmsPageAsync(DeveloperApiCmsPageInput input, string actor, CancellationToken cancellationToken = default);
    Task<DeveloperApiCmsPage?> UpdateCmsPageAsync(Guid id, DeveloperApiCmsPageInput input, string actor, CancellationToken cancellationToken = default);
}
