using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.CjAds;

public sealed class CjAdsService(
    IAppDbContext db,
    ICjAffiliateService cjAffiliateService,
    ISecretProtector secretProtector,
    ILogger<CjAdsService> logger) : ICjAdsService
{
    private const string NetworkName = "CJ";

    public async Task<CjConnectionTestResult> TestConnectionAsync(CjPartnerSyncRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var result = await cjAffiliateService.ValidateConnectionAsync(
            new CjConnectionRequest(
                request.DeveloperKey.Trim(),
                request.PublisherId.Trim(),
                request.EndpointUrl.Trim(),
                request.MaxResults,
                string.IsNullOrWhiteSpace(request.WebsiteId) ? null : request.WebsiteId.Trim()),
            cancellationToken);

        return new CjConnectionTestResult
        {
            IsSuccess = result.IsSuccess,
            Message = result.Message,
            PartnerCountPreview = result.PartnerCountPreview
        };
    }

    public async Task<CjPartnerSyncResult> SyncPartnersAsync(CjPartnerSyncRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var fetched = await cjAffiliateService.FetchPartnersAsync(
            new CjConnectionRequest(
                request.DeveloperKey.Trim(),
                request.PublisherId.Trim(),
                request.EndpointUrl.Trim(),
                request.MaxResults,
                string.IsNullOrWhiteSpace(request.WebsiteId) ? null : request.WebsiteId.Trim()),
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var incomingById = fetched.Partners
            .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserId) || !string.IsNullOrWhiteSpace(x.AdvertiserName))
            .GroupBy(x => BuildPartnerKey(x.AdvertiserId, x.AdvertiserName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var existing = await db.AffiliateOffers
            .Where(x => x.Network == NetworkName)
            .ToListAsync(cancellationToken);

        var existingByKey = existing.ToDictionary(
            x => BuildPartnerKey(x.LinkName, x.AdvertiserName),
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var updated = 0;

        foreach (var partner in incomingById)
        {
            var partnerKey = BuildPartnerKey(partner.AdvertiserId, partner.AdvertiserName);
            if (existingByKey.TryGetValue(partnerKey, out var row))
            {
                row.AdvertiserName = partner.AdvertiserName;
                row.Category = partner.PrimaryCategory;
                row.TrackingUrl = partner.DetailsUrl;
                row.PromotionEndsAt = null;
                row.UpdatedAt = now;
                row.UpdatedBy = "cj-sync";
                updated += 1;
                continue;
            }

            db.AffiliateOffers.Add(new AffiliateOffer
            {
                Network = NetworkName,
                AdvertiserName = partner.AdvertiserName,
                LinkName = partner.AdvertiserId,
                Category = partner.PrimaryCategory,
                TrackingUrl = partner.DetailsUrl,
                CreatedBy = "cj-sync"
            });
            created += 1;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "CJ partner sync completed. Received {Total}, created {Created}, updated {Updated}.",
            incomingById.Length,
            created,
            updated);

        var list = await ListPartnersAsync(cancellationToken: cancellationToken);

        return new CjPartnerSyncResult
        {
            IsSuccess = true,
            Message = fetched.Message,
            TotalReceived = incomingById.Length,
            Created = created,
            Updated = updated,
            Partners = list
        };
    }

    public async Task<IReadOnlyList<CjPartnerView>> ListPartnersAsync(
        string relationshipStatus = "All",
        string search = "",
        CancellationToken cancellationToken = default)
    {
        var query = db.AffiliateOffers
            .AsNoTracking()
            .Where(x => x.Network == NetworkName);

        if (!string.IsNullOrWhiteSpace(relationshipStatus) && !string.Equals(relationshipStatus, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(x => x.Category == relationshipStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.AdvertiserName.Contains(term) || x.LinkName.Contains(term));
        }

        var partners = await query
            .OrderBy(x => x.AdvertiserName)
            .Select(x => new CjPartnerView
            {
                AdvertiserId = x.LinkName,
                AdvertiserName = x.AdvertiserName,
                RelationshipStatus = x.Category ?? string.Empty,
                Country = string.Empty,
                PrimaryCategory = x.Category ?? string.Empty,
                DetailsUrl = x.TrackingUrl ?? string.Empty,
                UpdatedAt = x.UpdatedAt ?? x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return partners;
    }

    private static void Validate(CjPartnerSyncRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeveloperKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublisherId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EndpointUrl);

        if (request.MaxResults <= 0 || request.MaxResults > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxResults), "Max results must be between 1 and 500.");
        }
    }

    private static string BuildPartnerKey(string advertiserId, string advertiserName)
    {
        var safeId = advertiserId?.Trim() ?? string.Empty;
        var safeName = advertiserName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(safeId) ? safeName : safeId;
    }

    public async Task<CjConnectorSettingsView?> GetConnectorSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.CjConnectorSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        return new CjConnectorSettingsView
        {
            DeveloperKey = UnprotectDeveloperKey(row.DeveloperKey),
            PublisherId = row.PublisherId,
            WebsiteId = row.WebsiteId,
            EndpointUrl = row.EndpointUrl,
            MaxResults = row.MaxResults
        };
    }

    public async Task SaveConnectorSettingsAsync(CjConnectorSettingsView settings, CancellationToken cancellationToken = default)
    {
        var row = await db.CjConnectorSettings
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            row = new CjConnectorSettings();
            db.CjConnectorSettings.Add(row);
        }

        row.DeveloperKey = ProtectDeveloperKey(settings.DeveloperKey);
        row.PublisherId = settings.PublisherId;
        row.WebsiteId = settings.WebsiteId;
        row.EndpointUrl = settings.EndpointUrl;
        row.MaxResults = settings.MaxResults;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = "user";

        await db.SaveChangesAsync(cancellationToken);
    }

    private string ProtectDeveloperKey(string developerKey)
    {
        return string.IsNullOrWhiteSpace(developerKey)
            ? string.Empty
            : secretProtector.Protect(developerKey.Trim());
    }

    private string UnprotectDeveloperKey(string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return string.Empty;
        }

        try
        {
            return secretProtector.Unprotect(storedValue);
        }
        catch (Exception ex)
        {
            // Backward compatibility for legacy rows saved before encryption.
            logger.LogWarning(ex, "Unable to decrypt stored CJ developer key. Treating value as legacy plaintext.");
            return storedValue;
        }
    }
}
