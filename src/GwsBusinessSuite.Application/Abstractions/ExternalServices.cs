namespace GwsBusinessSuite.Application.Abstractions;

public interface ICjAffiliateService
{
    Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default);
    Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default);
    Task<CjLinkFetchResult> FetchLinksAsync(CjLinkFetchRequest request, CancellationToken ct = default);

    // Best-effort: queries the same commissions.api.cj.com GraphQL endpoint already used
    // for partner discovery, requesting additional commission-amount fields. CJ's exact
    // field names for these amounts aren't independently verified against live API docs
    // here, so parsing is defensive - a schema mismatch yields an empty result rather
    // than throwing, see CjAffiliateService.FetchCommissionsAsync.
    Task<CjCommissionFetchResult> FetchCommissionsAsync(CjConnectionRequest request, CancellationToken ct = default);
}
public interface IOllamaService
{
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default);
    Task PullModelAsync(string model, CancellationToken ct = default);
    Task DeleteModelAsync(string model, CancellationToken ct = default);

    // Requires a model with image-generation capability (e.g. an installed Z-Image
    // Turbo / FLUX build) - returns raw base64 PNG bytes, no data: URI prefix.
    Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default);
}
public interface IDockerDeploymentService { Task<string> DeployAsync(string appName, string dockerfilePath, CancellationToken ct = default); }

public sealed record CjConnectionRequest(
    string DeveloperKey,
    string PublisherId,
    string EndpointUrl,
    int MaxResults = 100,
    string? WebsiteId = null);

public sealed record CjConnectionValidationResult(
    bool IsSuccess,
    string Message,
    int PartnerCountPreview);

public sealed record CjPartnerFetchResult(
    IReadOnlyCollection<CjPartnerRecord> Partners,
    string Message,
    bool IsCompleteRoster = false);

public sealed record CjPartnerRecord(
    string AdvertiserId,
    string AdvertiserName,
    string RelationshipStatus,
    string Country,
    string PrimaryCategory,
    string DetailsUrl);

public sealed record CjLinkFetchRequest(
    string DeveloperKey,
    string PublisherId,
    string WebsiteId,
    string AdvertiserId,
    int MaxResults = 100);

public sealed record CjLinkFetchResult(
    IReadOnlyCollection<CjLinkRecord> Links,
    string Message);

public sealed record CjLinkRecord(
    string LinkId,
    string AdvertiserId,
    string AdvertiserName,
    string LinkName,
    string LinkType,
    string Description,
    string ClickUrl,
    string DestinationUrl,
    string PromotionType,
    DateTimeOffset? PromotionEndDate,
    string? ImageUrl = null);

public sealed record CjCommissionFetchResult(
    IReadOnlyCollection<CjCommissionFetchRecord> Commissions,
    string Message);

public sealed record CjCommissionFetchRecord(
    string ExternalId,
    string AdvertiserId,
    string AdvertiserName,
    string OrderId,
    string ActionStatus,
    decimal SaleAmount,
    decimal CommissionAmount,
    string Currency,
    DateTimeOffset? EventDate,
    DateTimeOffset? PostingDate);
