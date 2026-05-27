namespace GwsBusinessSuite.Application.CjAds;

public interface ICjAdsService
{
    Task<CjConnectionTestResult> TestConnectionAsync(CjPartnerSyncRequest request, CancellationToken cancellationToken = default);
    Task<CjPartnerSyncResult> SyncPartnersAsync(CjPartnerSyncRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CjPartnerView>> ListPartnersAsync(string relationshipStatus = "All", string search = "", CancellationToken cancellationToken = default);
    Task<CjConnectorSettingsView?> GetConnectorSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveConnectorSettingsAsync(CjConnectorSettingsView settings, CancellationToken cancellationToken = default);
}
