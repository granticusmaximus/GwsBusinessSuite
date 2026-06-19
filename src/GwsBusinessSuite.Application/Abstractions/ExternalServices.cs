namespace GwsBusinessSuite.Application.Abstractions;

public interface ICjAffiliateService
{
    Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default);
    Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default);
}
public interface ICloudflareService { Task<string> CreateSubdomainRouteAsync(string subdomain, int port, CancellationToken ct = default); }
public interface IDigitalOceanService { Task<string> GetDropletsAsync(CancellationToken ct = default); }
public interface IOllamaService
{
    Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default);
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
