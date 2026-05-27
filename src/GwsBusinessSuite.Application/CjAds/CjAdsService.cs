using GwsBusinessSuite.Application.Abstractions;
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

        var staleRows = existing
            .Where(x => !incomingAdvertiserIds.Contains(NormalizeAdvertiserId(x.AdvertiserId, x.AdvertiserName)))
            .ToList();

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

                var catalogOfferCount = group.Count(IsCatalogOffer);

                return new CjPartnerView
                {
                    AdvertiserId = representative.AdvertiserId,
                    AdvertiserName = representative.AdvertiserName,
                    RelationshipStatus = representative.RelationshipStatus ?? string.Empty,
                    Country = string.Empty,
                    PrimaryCategory = representative.Category ?? string.Empty,
                    OfferCount = catalogOfferCount > 0 ? catalogOfferCount : group.Count(),
                    DetailsUrl = representative.TrackingUrl ?? string.Empty,
                    UpdatedAt = group.Max(x => x.UpdatedAt ?? x.CreatedAt)
                };
            })
            .Where(partner =>
                string.IsNullOrWhiteSpace(relationshipStatus) ||
                string.Equals(relationshipStatus, "All", StringComparison.OrdinalIgnoreCase) ||
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
