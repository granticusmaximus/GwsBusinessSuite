using GwsBusinessSuite.Application.AffiliateAnalytics;

namespace GwsBusinessSuite.Application.CjAds;

public interface ICjAdsService
{
    Task<CjConnectionTestResult> TestConnectionAsync(CjPartnerSyncRequest request, CancellationToken cancellationToken = default);
    Task<CjPartnerSyncResult> SyncPartnersAsync(CjPartnerSyncRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CjPartnerView>> ListPartnersAsync(string relationshipStatus = "All", string search = "", CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CjAffiliateOfferView>> GetOffersForAdvertiserAsync(string advertiserId, string advertiserName, CancellationToken cancellationToken = default);
    Task<CjOfferImportResult> ImportOffersAsync(CjOfferImportRequest request, CancellationToken cancellationToken = default);
    Task<CjLinkSyncResult> SyncLinksForAdvertiserAsync(string advertiserId, string advertiserName, CancellationToken cancellationToken = default);
    Task<CjBulkLinkSyncResult> SyncAllLinksAsync(CancellationToken cancellationToken = default);
    Task<CommissionSyncResult> SyncCommissionsAsync(CancellationToken cancellationToken = default);
    Task<CjConnectorSettingsView?> GetConnectorSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveConnectorSettingsAsync(CjConnectorSettingsView settings, CancellationToken cancellationToken = default);
    Task SetAutomaticArticleRotationEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
