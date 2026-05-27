using GwsBusinessSuite.Application.ContentStudio;

namespace GwsBusinessSuite.Application.Abstractions;

public interface ICjAffiliateService
{
    Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default);
    Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default);
}
public interface ISanityPublisher
{
    Task<SanityPublishResult> PublishDraftAsync(ArticleGenerationResult draft, CancellationToken ct = default);
}
public interface ICloudflareService { Task<string> CreateSubdomainRouteAsync(string subdomain, int port, CancellationToken ct = default); }
public interface IDigitalOceanService { Task<string> GetDropletsAsync(CancellationToken ct = default); }
public interface IOllamaService
{
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default);
    Task<OllamaImageGenerationResult> GenerateImageAsync(OllamaImageGenerationRequest request, CancellationToken ct = default);
}
public interface IDockerDeploymentService { Task<string> DeployAsync(string appName, string dockerfilePath, CancellationToken ct = default); }

public sealed record OllamaImageGenerationRequest(
    string Model,
    string Prompt,
    int Width,
    int Height,
    int Steps);

public sealed record OllamaImageGenerationResult(
    string DataUri,
    string MimeType,
    string Model);

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

public sealed record SanityPublishResult(
    bool IsSuccess,
    string Message,
    string DocumentId,
    string Revision,
    string DocumentUrl);
