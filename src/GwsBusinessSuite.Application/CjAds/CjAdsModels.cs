namespace GwsBusinessSuite.Application.CjAds;

public sealed class CjPartnerSyncRequest
{
    public string DeveloperKey { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public string WebsiteId { get; init; } = string.Empty;
    public string EndpointUrl { get; init; } = "https://commissions.api.cj.com/query";
    public int MaxResults { get; init; } = 100;
}

public sealed class CjPartnerView
{
    public string AdvertiserId { get; init; } = string.Empty;
    public string AdvertiserName { get; init; } = string.Empty;
    public string RelationshipStatus { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string PrimaryCategory { get; init; } = string.Empty;
    public string DetailsUrl { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CjPartnerSyncResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public int TotalReceived { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public IReadOnlyCollection<CjPartnerView> Partners { get; init; } = Array.Empty<CjPartnerView>();
}

public sealed class CjConnectionTestResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = string.Empty;
    public int PartnerCountPreview { get; init; }
}

public sealed class CjConnectorSettingsView
{
    public string DeveloperKey { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public string WebsiteId { get; init; } = string.Empty;
    public string EndpointUrl { get; init; } = "https://commissions.api.cj.com/query";
    public int MaxResults { get; init; } = 100;
}
