using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AffiliateAnalytics;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GwsBusinessSuite.Application.CjAds;

public sealed class CjAdsService(
    IAppDbContext db,
    ICjAffiliateService cjAffiliateService,
    ISecretProtector secretProtector,
    ILogger<CjAdsService> logger) : ICjAdsService
{
    private const string NetworkName = "CJ";
    private static readonly string[] SupportedImportFormats = ["Auto", "CSV", "TSV", "JSON"];

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

        var filteredPartners = FilterToJoinedPartnersWhenAvailable(fetched.Partners);

        var now = DateTimeOffset.UtcNow;
        var incomingById = filteredPartners
            .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserId) || !string.IsNullOrWhiteSpace(x.AdvertiserName))
            .GroupBy(x => BuildPartnerKey(x.AdvertiserId, x.AdvertiserName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var existing = await db.AffiliateOffers
            .Where(x => x.Network == NetworkName)
            .ToListAsync(cancellationToken);

        var incomingAdvertiserIds = incomingById
            .Select(x => NormalizeAdvertiserId(x.AdvertiserId, x.AdvertiserName))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var staleRows = fetched.IsCompleteRoster
            ? existing
                .Where(x => !incomingAdvertiserIds.Contains(NormalizeAdvertiserId(x.AdvertiserId, x.AdvertiserName)))
                .ToList()
            : [];

        if (staleRows.Count > 0)
        {
            db.AffiliateOffers.RemoveRange(staleRows);
            existing = existing.Except(staleRows).ToList();
        }

        var existingByKey = existing
            .Where(x => x.LinkName == x.AdvertiserId)
            .ToDictionary(
            x => BuildPartnerKey(x.AdvertiserId, x.AdvertiserName),
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var created = 0;
        var updated = 0;

        foreach (var partner in incomingById)
        {
            var partnerKey = BuildPartnerKey(partner.AdvertiserId, partner.AdvertiserName);
            var normalizedAdvertiserId = NormalizeAdvertiserId(partner.AdvertiserId, partner.AdvertiserName);
            if (existingByKey.TryGetValue(partnerKey, out var row))
            {
                row.AdvertiserId = normalizedAdvertiserId;
                row.AdvertiserName = partner.AdvertiserName;
                row.RelationshipStatus = partner.RelationshipStatus;
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
                AdvertiserId = normalizedAdvertiserId,
                AdvertiserName = partner.AdvertiserName,
                LinkName = normalizedAdvertiserId,
                RelationshipStatus = partner.RelationshipStatus,
                Category = partner.PrimaryCategory,
                TrackingUrl = partner.DetailsUrl,
                CreatedBy = "cj-sync"
            });
            created += 1;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "CJ partner sync completed. Received {Total}, created {Created}, updated {Updated}, deleted {Deleted}.",
            incomingById.Length,
            created,
            updated,
            staleRows.Count);

        var list = await ListPartnersAsync(cancellationToken: cancellationToken);

        return new CjPartnerSyncResult
        {
            IsSuccess = true,
            Message = BuildSyncMessage(fetched.Message, filteredPartners.Count, incomingById.Length, staleRows.Count),
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
        var offers = await db.AffiliateOffers
            .AsNoTracking()
            .Where(x => x.Network == NetworkName)
            .ToListAsync(cancellationToken);

        var partners = offers
            .GroupBy(x => NormalizeAdvertiserId(x.AdvertiserId, x.AdvertiserName), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var representative = group
                    .OrderByDescending(x => string.Equals(x.LinkName, x.AdvertiserId, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(x => x.UpdatedAt ?? x.CreatedAt)
                    .First();

                var activeCatalogOfferCount = group.Count(offer => IsCatalogOffer(offer) && IsActiveOffer(offer));
                var activeOfferCount = activeCatalogOfferCount > 0
                    ? activeCatalogOfferCount
                    : group.Count(IsActiveOffer);

                return new CjPartnerView
                {
                    AdvertiserId = representative.AdvertiserId,
                    AdvertiserName = representative.AdvertiserName,
                    RelationshipStatus = representative.RelationshipStatus ?? string.Empty,
                    Country = string.Empty,
                    PrimaryCategory = representative.Category ?? string.Empty,
                    OfferCount = activeOfferCount,
                    DetailsUrl = representative.TrackingUrl ?? string.Empty,
                    UpdatedAt = group.Max(x => x.UpdatedAt ?? x.CreatedAt)
                };
            })
            .Where(partner =>
                string.IsNullOrWhiteSpace(relationshipStatus) ||
                string.Equals(relationshipStatus, "All", StringComparison.OrdinalIgnoreCase) ||
                (IsJoinedRelationship(relationshipStatus) && IsJoinedRelationship(partner.RelationshipStatus)) ||
                string.Equals(partner.RelationshipStatus, relationshipStatus, StringComparison.OrdinalIgnoreCase))
            .Where(partner =>
                string.IsNullOrWhiteSpace(search) ||
                partner.AdvertiserName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ||
                partner.AdvertiserId.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(partner => partner.AdvertiserName)
            .ToList();

        return partners;
    }

    public async Task<IReadOnlyList<CjAffiliateOfferView>> GetOffersForAdvertiserAsync(
        string advertiserId,
        string advertiserName,
        CancellationToken cancellationToken = default)
    {
        var normalizedAdvertiserId = advertiserId.Trim();
        var normalizedAdvertiserName = advertiserName.Trim();

        var offers = await db.AffiliateOffers
            .AsNoTracking()
            .Where(x => x.Network == NetworkName)
            .Where(x =>
                x.AdvertiserId == normalizedAdvertiserId ||
                x.AdvertiserName == normalizedAdvertiserName ||
                x.AdvertiserId == normalizedAdvertiserName)
            .ToListAsync(cancellationToken);

        var catalogOffers = offers.Where(IsCatalogOffer).ToList();
        var selectedOffers = catalogOffers.Count > 0 ? catalogOffers : offers;

        return selectedOffers
            .Where(IsActiveOffer)
            .OrderByDescending(x => x.UpdatedAt ?? x.CreatedAt)
            .ThenBy(x => x.LinkName)
            .Select(x => new CjAffiliateOfferView
            {
                AdvertiserId = x.AdvertiserId,
                AdvertiserName = x.AdvertiserName,
                LinkName = x.LinkName,
                Category = x.Category ?? string.Empty,
                TrackingUrl = x.TrackingUrl ?? string.Empty,
                PromotionEndsAt = x.PromotionEndsAt,
                IsImportedCatalogOffer = IsCatalogOffer(x),
                UpdatedAt = x.UpdatedAt ?? x.CreatedAt
            })
            .ToList();
    }

    public async Task<CjOfferImportResult> ImportOffersAsync(CjOfferImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdvertiserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdvertiserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Payload);

        if (!SupportedImportFormats.Contains(request.Format, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported CJ import format '{request.Format}'. Use Auto, CSV, TSV, or JSON.");
        }

        var advertiserId = NormalizeAdvertiserId(request.AdvertiserId, request.AdvertiserName);
        var advertiserName = request.AdvertiserName.Trim();
        var importedRows = ParseImportRows(request.Payload, request.Format)
            .Where(row => !string.IsNullOrWhiteSpace(row.OfferName) && !string.IsNullOrWhiteSpace(row.TrackingUrl))
            .ToList();

        if (importedRows.Count == 0)
        {
            throw new InvalidOperationException("No valid CJ offer rows were found. Include at least an offer name and tracking URL.");
        }

        var existingCatalogOffers = await db.AffiliateOffers
            .Where(x => x.Network == NetworkName && x.AdvertiserId == advertiserId && x.LinkName != x.AdvertiserId)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        var imported = 0;
        var updated = 0;
        var now = DateTimeOffset.UtcNow;

        if (request.ReplaceExistingOffers && existingCatalogOffers.Count > 0)
        {
            deleted = existingCatalogOffers.Count;
            db.AffiliateOffers.RemoveRange(existingCatalogOffers);
            existingCatalogOffers = new List<AffiliateOffer>();
        }

        var existingByKey = existingCatalogOffers.ToDictionary(BuildOfferKey, x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var row in importedRows)
        {
            var key = BuildOfferKey(row.OfferName, row.TrackingUrl);
            if (existingByKey.TryGetValue(key, out var existingRow))
            {
                existingRow.Category = row.Category;
                existingRow.TrackingUrl = row.TrackingUrl;
                existingRow.PromotionEndsAt = row.PromotionEndsAt;
                existingRow.UpdatedAt = now;
                existingRow.UpdatedBy = "cj-import";
                updated += 1;
                continue;
            }

            await db.AffiliateOffers.AddAsync(new AffiliateOffer
            {
                Network = NetworkName,
                AdvertiserId = advertiserId,
                AdvertiserName = advertiserName,
                LinkName = row.OfferName,
                Category = row.Category,
                TrackingUrl = row.TrackingUrl,
                PromotionEndsAt = row.PromotionEndsAt,
                CreatedAt = now,
                CreatedBy = "cj-import"
            }, cancellationToken);
            imported += 1;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new CjOfferImportResult
        {
            Imported = imported,
            Updated = updated,
            Deleted = deleted,
            Offers = await GetOffersForAdvertiserAsync(advertiserId, advertiserName, cancellationToken)
        };
    }

    // Replaces the old "export a CSV from CJ's Get Links tool and paste it in" workflow with
    // a live call to CJ's Link Search API for a single advertiser. Always a full
    // replace-and-reinsert of that advertiser's catalog offers (not a merge) so stale/expired
    // links CJ no longer returns don't linger - the roster placeholder row (LinkName ==
    // AdvertiserId, written by SyncPartnersAsync) is untouched since IsCatalogOffer excludes it.
    public async Task<CjLinkSyncResult> SyncLinksForAdvertiserAsync(
        string advertiserId,
        string advertiserName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(advertiserId);

        var settings = await GetConnectorSettingsAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.DeveloperKey))
        {
            throw new InvalidOperationException("Connect your CJ account before syncing links (Developer Key is missing).");
        }

        if (string.IsNullOrWhiteSpace(settings.WebsiteId))
        {
            throw new InvalidOperationException(
                "Website ID is required to sync links (CJ generates tracking links per-site). Set it in the CJ connector settings.");
        }

        var fetched = await cjAffiliateService.FetchLinksAsync(new CjLinkFetchRequest(
            DeveloperKey: settings.DeveloperKey.Trim(),
            PublisherId: settings.PublisherId.Trim(),
            WebsiteId: settings.WebsiteId.Trim(),
            AdvertiserId: advertiserId.Trim(),
            MaxResults: settings.MaxResults), cancellationToken);

        var normalizedAdvertiserId = NormalizeAdvertiserId(advertiserId, advertiserName);
        var existingCatalogOffers = await db.AffiliateOffers
            .Where(x => x.Network == NetworkName && x.AdvertiserId == normalizedAdvertiserId && x.LinkName != x.AdvertiserId)
            .ToListAsync(cancellationToken);

        if (existingCatalogOffers.Count > 0)
        {
            db.AffiliateOffers.RemoveRange(existingCatalogOffers);
        }

        var now = DateTimeOffset.UtcNow;
        var imported = 0;

        foreach (var link in fetched.Links)
        {
            var trackingUrl = string.IsNullOrWhiteSpace(link.ClickUrl) ? link.DestinationUrl : link.ClickUrl;
            if (string.IsNullOrWhiteSpace(trackingUrl))
            {
                continue;
            }

            await db.AffiliateOffers.AddAsync(new AffiliateOffer
            {
                Network = NetworkName,
                AdvertiserId = normalizedAdvertiserId,
                AdvertiserName = advertiserName,
                LinkName = string.IsNullOrWhiteSpace(link.LinkName) ? "CJ Link" : link.LinkName,
                Category = link.PromotionType,
                TrackingUrl = trackingUrl,
                PromotionEndsAt = link.PromotionEndDate,
                CreatedAt = now,
                CreatedBy = "cj-link-sync"
            }, cancellationToken);
            imported += 1;
        }

        await db.SaveChangesAsync(cancellationToken);

        return new CjLinkSyncResult
        {
            IsSuccess = true,
            Message = imported > 0
                ? $"Synced {imported} links for {advertiserName}."
                : fetched.Message,
            Imported = imported,
            Updated = 0,
            Offers = await GetOffersForAdvertiserAsync(normalizedAdvertiserId, advertiserName, cancellationToken)
        };
    }

    // Loops every advertiser currently in "My Advertisers" (see SyncPartnersAsync) and syncs
    // real links for each. This is a lot of sequential CJ API calls for a large roster (dozens
    // to low hundreds), so it's deliberately resilient per-advertiser - one advertiser
    // returning an error (e.g. link search unsupported for that program) doesn't abort the
    // whole run, it's just recorded and skipped. A small delay between calls is a courtesy to
    // CJ's API rather than a documented rate limit requirement.
    public async Task<CjBulkLinkSyncResult> SyncAllLinksAsync(CancellationToken cancellationToken = default)
    {
        var partners = await ListPartnersAsync(cancellationToken: cancellationToken);

        // Validate shared connector settings once. Previously the same missing Website ID
        // exception was caught once per advertiser, producing a huge and misleading failure
        // list even though none of the advertiser calls had started.
        if (partners.Count > 0)
        {
            var settings = await GetConnectorSettingsAsync(cancellationToken);
            var configurationError = settings is null || string.IsNullOrWhiteSpace(settings.DeveloperKey)
                ? "Connect your CJ account before syncing links (Developer Key is missing)."
                : string.IsNullOrWhiteSpace(settings.WebsiteId)
                    ? "Website ID is required for CJ tracking links. Open Connector, enter the Website ID assigned to your site in CJ, and save it before syncing."
                    : string.Empty;

            if (!string.IsNullOrWhiteSpace(configurationError))
            {
                return new CjBulkLinkSyncResult
                {
                    IsSuccess = false,
                    Message = configurationError,
                    ConfigurationError = configurationError
                };
            }
        }

        var processed = 0;
        var failed = 0;
        var totalImported = 0;
        var failures = new List<string>();

        foreach (var partner in partners)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await SyncLinksForAdvertiserAsync(partner.AdvertiserId, partner.AdvertiserName, cancellationToken);
                processed += 1;
                totalImported += result.Imported;
            }
            catch (Exception ex)
            {
                failed += 1;
                failures.Add($"{partner.AdvertiserName}: {ex.Message}");
                logger.LogWarning(ex, "CJ link sync failed for advertiser {AdvertiserId} ({AdvertiserName}).", partner.AdvertiserId, partner.AdvertiserName);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }

        return new CjBulkLinkSyncResult
        {
            IsSuccess = failed == 0,
            Message = $"Synced links for {processed} of {partners.Count} advertisers ({totalImported} total links imported)." +
                       (failed > 0 ? $" {failed} advertiser(s) failed - see details." : string.Empty),
            AdvertisersProcessed = processed,
            AdvertisersFailed = failed,
            TotalLinksImported = totalImported,
            FailureMessages = failures
        };
    }

    // Upserts by CJ's own commission id (ExternalId) so re-running this doesn't duplicate
    // rows and naturally picks up status changes (e.g. Pending -> Closed) on re-sync.
    public async Task<CommissionSyncResult> SyncCommissionsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetConnectorSettingsAsync(cancellationToken);
        if (settings is null || string.IsNullOrWhiteSpace(settings.DeveloperKey))
        {
            return new CommissionSyncResult(false, "Connect your CJ account before syncing commissions (Developer Key is missing).", 0);
        }

        var fetched = await cjAffiliateService.FetchCommissionsAsync(
            new CjConnectionRequest(
                settings.DeveloperKey.Trim(),
                settings.PublisherId.Trim(),
                settings.EndpointUrl.Trim(),
                settings.MaxResults,
                string.IsNullOrWhiteSpace(settings.WebsiteId) ? null : settings.WebsiteId.Trim()),
            cancellationToken);

        if (fetched.Commissions.Count == 0)
        {
            return new CommissionSyncResult(true, fetched.Message, 0);
        }

        // Dedupe by ExternalId within this batch first - if CJ's API ever returns an
        // overlapping/duplicate record (e.g. pagination overlap), keeping only the last
        // occurrence avoids inserting two rows with the same ExternalId and hitting the
        // unique index in SaveChangesAsync below (which would otherwise fail the entire
        // sync with no partial progress, every run, until fixed).
        var dedupedCommissions = fetched.Commissions
            .GroupBy(c => c.ExternalId)
            .Select(group => group.Last())
            .ToList();

        var externalIds = dedupedCommissions.Select(c => c.ExternalId).ToList();
        var existingByExternalId = await db.CjCommissionRecords
            .Where(r => externalIds.Contains(r.ExternalId))
            .ToDictionaryAsync(r => r.ExternalId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var imported = 0;
        foreach (var record in dedupedCommissions)
        {
            if (existingByExternalId.TryGetValue(record.ExternalId, out var existing))
            {
                existing.AdvertiserId = record.AdvertiserId;
                existing.AdvertiserName = record.AdvertiserName;
                existing.OrderId = record.OrderId;
                existing.ActionStatus = record.ActionStatus;
                existing.SaleAmount = record.SaleAmount;
                existing.CommissionAmount = record.CommissionAmount;
                existing.Currency = record.Currency;
                existing.EventDate = record.EventDate;
                existing.PostingDate = record.PostingDate;
                existing.UpdatedAt = now;
                existing.UpdatedBy = "cj-commission-sync";
            }
            else
            {
                await db.CjCommissionRecords.AddAsync(new CjCommissionRecord
                {
                    ExternalId = record.ExternalId,
                    AdvertiserId = record.AdvertiserId,
                    AdvertiserName = record.AdvertiserName,
                    OrderId = record.OrderId,
                    ActionStatus = record.ActionStatus,
                    SaleAmount = record.SaleAmount,
                    CommissionAmount = record.CommissionAmount,
                    Currency = record.Currency,
                    EventDate = record.EventDate,
                    PostingDate = record.PostingDate,
                    CreatedBy = "cj-commission-sync"
                }, cancellationToken);
            }

            imported += 1;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CommissionSyncResult(true, fetched.Message, imported);
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

    private static string NormalizeAdvertiserId(string advertiserId, string advertiserName)
    {
        return BuildPartnerKey(advertiserId, advertiserName);
    }

    private static bool IsCatalogOffer(AffiliateOffer offer)
    {
        return !string.Equals(offer.LinkName, offer.AdvertiserId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActiveOffer(AffiliateOffer offer)
    {
        if (string.IsNullOrWhiteSpace(offer.TrackingUrl))
        {
            return false;
        }

        return offer.PromotionEndsAt is null || offer.PromotionEndsAt.Value >= DateTimeOffset.UtcNow;
    }

    private static IReadOnlyCollection<CjPartnerRecord> FilterToJoinedPartnersWhenAvailable(IReadOnlyCollection<CjPartnerRecord> partners)
    {
        var joinedPartners = partners
            .Where(partner => IsJoinedRelationship(partner.RelationshipStatus))
            .ToArray();

        return joinedPartners.Length > 0 ? joinedPartners : partners;
    }

    private static bool IsJoinedRelationship(string relationshipStatus)
    {
        if (string.IsNullOrWhiteSpace(relationshipStatus))
        {
            return false;
        }

        return relationshipStatus.Trim().Equals("joined", StringComparison.OrdinalIgnoreCase)
            || relationshipStatus.Trim().Equals("active", StringComparison.OrdinalIgnoreCase)
            || relationshipStatus.Trim().Equals("approved", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSyncMessage(string originalMessage, int fetchedCount, int dedupedCount, int deletedCount)
    {
        if (deletedCount <= 0 && fetchedCount == dedupedCount)
        {
            return originalMessage;
        }

        return $"{originalMessage} Using {dedupedCount} joined advertisers from {fetchedCount} fetched records. Removed {deletedCount} stale local advertisers.";
    }

    private static string BuildOfferKey(AffiliateOffer offer)
    {
        return BuildOfferKey(offer.LinkName, offer.TrackingUrl);
    }

    private static string BuildOfferKey(string offerName, string? trackingUrl)
    {
        var normalizedName = offerName.Trim();
        var normalizedUrl = trackingUrl?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalizedUrl)
            ? normalizedName
            : $"{normalizedName}|{normalizedUrl}";
    }

    private static IReadOnlyList<ImportedOfferRow> ParseImportRows(string payload, string format)
    {
        var normalizedPayload = payload.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPayload))
        {
            return Array.Empty<ImportedOfferRow>();
        }

        var effectiveFormat = format.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? DetectFormat(normalizedPayload)
            : format;

        return effectiveFormat.ToUpperInvariant() switch
        {
            "JSON" => ParseJsonRows(normalizedPayload),
            "TSV" => ParseDelimitedRows(normalizedPayload, '\t'),
            _ => ParseDelimitedRows(normalizedPayload, ',')
        };
    }

    private static string DetectFormat(string payload)
    {
        if (payload.StartsWith('[') || payload.StartsWith('{'))
        {
            return "JSON";
        }

        return payload.Contains('\t') ? "TSV" : "CSV";
    }

    private static IReadOnlyList<ImportedOfferRow> ParseJsonRows(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Select(ParseJsonRow).Where(x => x is not null).Cast<ImportedOfferRow>().ToList();
        }

        var single = ParseJsonRow(root);
        return single is null ? Array.Empty<ImportedOfferRow>() : [single];
    }

    private static ImportedOfferRow? ParseJsonRow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ImportedOfferRow(
            OfferName: ReadString(element, "offerName", "name", "title", "linkName"),
            Category: ReadString(element, "category", "segment", "type"),
            TrackingUrl: ReadString(element, "trackingUrl", "url", "link", "destinationUrl"),
            PromotionEndsAt: ReadDate(element, "promotionEndsAt", "expiresAt", "endDate"));
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null)
            {
                return property.GetString()?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static IReadOnlyList<ImportedOfferRow> ParseDelimitedRows(string payload, char delimiter)
    {
        var lines = payload
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (lines.Count == 0)
        {
            return Array.Empty<ImportedOfferRow>();
        }

        var firstTokens = SplitDelimitedLine(lines[0], delimiter);
        var hasHeader = IsHeaderRow(firstTokens);
        var columnMap = BuildColumnMap(firstTokens, hasHeader);

        return lines
            .Skip(hasHeader ? 1 : 0)
            .Select(line => ParseDelimitedRow(SplitDelimitedLine(line, delimiter), columnMap))
            .Where(row => row is not null)
            .Cast<ImportedOfferRow>()
            .ToList();
    }

    private static ImportedOfferRow? ParseDelimitedRow(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, int> columnMap)
    {
        var offerName = GetToken(tokens, columnMap, "offer")
            ?? GetToken(tokens, columnMap, "offername")
            ?? GetToken(tokens, columnMap, "name")
            ?? GetToken(tokens, columnMap, "title")
            ?? string.Empty;
        var category = GetToken(tokens, columnMap, "category") ?? string.Empty;
        var trackingUrl = GetToken(tokens, columnMap, "tracking")
            ?? GetToken(tokens, columnMap, "trackingurl")
            ?? GetToken(tokens, columnMap, "url")
            ?? GetToken(tokens, columnMap, "link")
            ?? string.Empty;
        var promotionValue = GetToken(tokens, columnMap, "promotionend")
            ?? GetToken(tokens, columnMap, "promotionendsat")
            ?? GetToken(tokens, columnMap, "expires")
            ?? GetToken(tokens, columnMap, "enddate");

        DateTimeOffset? promotionEndsAt = null;
        if (!string.IsNullOrWhiteSpace(promotionValue) && DateTimeOffset.TryParse(promotionValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            promotionEndsAt = parsed;
        }

        if (string.IsNullOrWhiteSpace(offerName) && string.IsNullOrWhiteSpace(trackingUrl))
        {
            return null;
        }

        return new ImportedOfferRow(offerName.Trim(), category.Trim(), trackingUrl.Trim(), promotionEndsAt);
    }

    private static IReadOnlyDictionary<string, int> BuildColumnMap(IReadOnlyList<string> tokens, bool hasHeader)
    {
        if (hasHeader)
        {
            return tokens
                .Select((value, index) => new { Key = NormalizeHeader(value), Index = index })
                .Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().Index, StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["offer"] = 0,
            ["category"] = 1,
            ["tracking"] = 2,
            ["promotionend"] = 3
        };
    }

    private static bool IsHeaderRow(IReadOnlyList<string> tokens)
    {
        return tokens.Select(NormalizeHeader).Any(token =>
            token is "offer" or "offername" or "name" or "title" or "category" or "tracking" or "trackingurl" or "url" or "link" or "promotionend" or "promotionendsat" or "expires" or "enddate");
    }

    private static string NormalizeHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string? GetToken(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, int> columnMap, string key)
    {
        return columnMap.TryGetValue(key, out var index) && index < tokens.Count
            ? tokens[index]
            : null;
    }

    private static IReadOnlyList<string> SplitDelimitedLine(string line, char delimiter)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index += 1;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (character == delimiter && !inQuotes)
            {
                values.Add(builder.ToString().Trim());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        values.Add(builder.ToString().Trim());
        return values;
    }

    private sealed record ImportedOfferRow(
        string OfferName,
        string Category,
        string TrackingUrl,
        DateTimeOffset? PromotionEndsAt);

    public async Task<CjConnectorSettingsView?> GetConnectorSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.CjConnectorSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var (developerKey, isUnreadable) = UnprotectDeveloperKey(row.DeveloperKey);

        return new CjConnectorSettingsView
        {
            DeveloperKey = developerKey,
            PublisherId = row.PublisherId,
            WebsiteId = row.WebsiteId,
            EndpointUrl = row.EndpointUrl,
            MaxResults = row.MaxResults,
            AutomaticArticleRotationEnabled = row.AutomaticArticleRotationEnabled,
            DeveloperKeyUnreadable = isUnreadable
        };
    }

    public async Task SaveConnectorSettingsAsync(CjConnectorSettingsView settings, CancellationToken cancellationToken = default)
    {
        var row = await db.CjConnectorSettings
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            row = new CjConnectorSettings { Id = CjConnectorSettings.WellKnownId };
            db.CjConnectorSettings.Add(row);
        }

        row.DeveloperKey = ProtectDeveloperKey(settings.DeveloperKey);
        row.PublisherId = settings.PublisherId;
        row.WebsiteId = settings.WebsiteId;
        row.EndpointUrl = settings.EndpointUrl;
        row.MaxResults = settings.MaxResults;
        row.AutomaticArticleRotationEnabled = settings.AutomaticArticleRotationEnabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = "user";

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetAutomaticArticleRotationEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var row = await db.CjConnectorSettings.FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new CjConnectorSettings { Id = CjConnectorSettings.WellKnownId };
            db.CjConnectorSettings.Add(row);
        }

        row.AutomaticArticleRotationEnabled = enabled;
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

    private (string DeveloperKey, bool IsUnreadable) UnprotectDeveloperKey(string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return (string.Empty, false);
        }

        try
        {
            return (secretProtector.Unprotect(storedValue), false);
        }
        catch (Exception ex)
        {
            // The Data Protection key ring that encrypted this value no longer exists
            // (e.g. rotated keys). The stored ciphertext can never be decrypted again -
            // surface that clearly instead of returning the unreadable ciphertext as if
            // it were a usable key.
            logger.LogWarning(ex, "Unable to decrypt stored CJ developer key. The key ring may have changed since it was saved.");
            return (string.Empty, true);
        }
    }
}
