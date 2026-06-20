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
    public int OfferCount { get; init; }
    public string DetailsUrl { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CjAffiliateOfferView
{
    public string AdvertiserId { get; init; } = string.Empty;
    public string AdvertiserName { get; init; } = string.Empty;
    public string LinkName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string TrackingUrl { get; init; } = string.Empty;
    public DateTimeOffset? PromotionEndsAt { get; init; }
    public bool IsImportedCatalogOffer { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CjOfferImportRequest
{
    public string AdvertiserId { get; init; } = string.Empty;
    public string AdvertiserName { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public string Format { get; init; } = "Auto";
    public bool ReplaceExistingOffers { get; init; } = true;
}

public sealed class CjOfferImportResult
{
    public int Imported { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public IReadOnlyList<CjAffiliateOfferView> Offers { get; init; } = Array.Empty<CjAffiliateOfferView>();
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

    /// <summary>
    /// True when a developer key is stored but could not be decrypted (e.g. the Data
    /// Protection key ring changed since it was saved). <see cref="DeveloperKey"/> is left
    /// empty in this case rather than exposing the unreadable ciphertext.
    /// </summary>
    public bool DeveloperKeyUnreadable { get; init; }
}
