using GwsBusinessSuite.Application.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class CjAffiliateService(HttpClient http) : ICjAffiliateService
{
    private const string CommissionsApiHost = "commissions.api.cj.com";
    private const int AdvertiserLookupPageSize = 100;
    private static readonly string[] AdvertiserLookupKeywordSeeds =
    [
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9"
    ];

    public async Task<CjConnectionValidationResult> ValidateConnectionAsync(CjConnectionRequest request, CancellationToken ct = default)
    {
        try
        {
            var fetchResult = await FetchPartnersAsync(request, ct);
            return new CjConnectionValidationResult(
                // Reaching this point means connectivity/auth succeeded; partner count may still be zero.
                IsSuccess: true,
                Message: fetchResult.Message,
                PartnerCountPreview: fetchResult.Partners.Count);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == HttpStatusCode.BadRequest &&
            ex.Message.Contains("CJ advertiser lookup failed", StringComparison.OrdinalIgnoreCase))
        {
            // Validation should confirm auth/connectivity first. Advertiser lookup is a secondary fallback
            // and may reject some parameter combinations even when commissions auth is valid.
            return new CjConnectionValidationResult(
                IsSuccess: true,
                Message: "Connected to CJ commissions API. Advertiser lookup fallback returned HTTP 400, so active advertiser preview is unavailable during test.",
                PartnerCountPreview: 0);
        }
    }

    public async Task<CjPartnerFetchResult> FetchPartnersAsync(CjConnectionRequest request, CancellationToken ct = default)
    {
        var endpoint = NormalizeEndpointUrl(request.EndpointUrl);
        var maxResults = request.MaxResults <= 0 ? 100 : request.MaxResults;
        var publisherId = request.PublisherId.Trim();
        var websiteId = request.WebsiteId?.Trim();

        if (IsCommissionsApiEndpoint(endpoint))
        {
            return await FetchPartnersViaGraphQlAsync(endpoint, request.DeveloperKey.Trim(), publisherId, websiteId, maxResults, ct);
        }

        websiteId ??= publisherId;

        var requestAttempts = new[]
        {
            BuildPartnerQuery(websiteId, maxResults, includeWebsiteId: true, joinedOnly: false),
            BuildPartnerQuery(websiteId, maxResults, includeWebsiteId: true, joinedOnly: true),
            BuildPartnerQuery(websiteId, maxResults, includeWebsiteId: false, joinedOnly: false),
            BuildPartnerQuery(websiteId, maxResults, includeWebsiteId: false, joinedOnly: true)
        };

        string? lastBody = null;
        HttpStatusCode? lastStatusCode = null;
        string? lastReason = null;

        foreach (var query in requestAttempts)
        {
            var attempt = await ExecuteFetchAttemptAsync(
                BuildQueryUrl(endpoint, query),
                request.DeveloperKey.Trim(),
                ct);

            lastBody = attempt.Body;
            lastStatusCode = attempt.StatusCode;
            lastReason = attempt.ReasonPhrase;

            if (attempt.IsAuthorizationError)
            {
                throw new HttpRequestException(
                    $"CJ API authorization failed with HTTP {(int)attempt.StatusCode} ({attempt.ReasonPhrase}). Check your Developer Key permissions.",
                    null,
                    attempt.StatusCode);
            }

            if (attempt.IsLikelyGraphiQlUi)
            {
                throw new InvalidOperationException(
                    $"CJ API returned an HTML/GraphiQL page instead of partner data. Endpoint is reachable, but the credential may be invalid for this API or the authorization format may be rejected. Ensure your CJ Personal Access Token has API permissions. Endpoint: {endpoint}");
            }

            if (attempt.Partners.Count > 0)
            {
                return new CjPartnerFetchResult(
                    Partners: attempt.Partners,
                    Message: $"Fetched {attempt.Partners.Count} CJ partners.");
            }
        }

        if (lastStatusCode is not null && (int)lastStatusCode.Value >= 400)
        {
            throw new HttpRequestException(
            $"CJ API call failed with HTTP {(int)lastStatusCode.Value} ({lastReason}). Body: {lastBody}",
                null,
            lastStatusCode.Value);
        }

        return new CjPartnerFetchResult(
            Partners: Array.Empty<CjPartnerRecord>(),
            Message: $"Connected to CJ API but no partner records were returned by any partner query strategy. Last CJ response preview: {CreateBodyPreview(lastBody)}");
    }

    private async Task<CjPartnerFetchResult> FetchPartnersViaGraphQlAsync(
        string endpoint,
        string developerKey,
        string publisherId,
        string? websiteId,
        int maxResults,
        CancellationToken ct)
    {
        var graphQlQuery = """
            query PartnerSnapshot($publisherIds:[String!]!, $websiteIds:[String!]) {
              publisherCommissions(forPublishers:$publisherIds, websiteIds:$websiteIds) {
                count
                records {
                  advertiserId
                  advertiserName
                  country
                  actionStatus
                }
              }
            }
            """;

        var payload = BuildGraphQlPayload(graphQlQuery, publisherId, websiteId);

        var attempt = await ExecuteGraphQlFetchAttemptAsync(endpoint, developerKey, payload, ct);
        if (attempt.IsAuthorizationError)
        {
            throw new HttpRequestException(
                $"CJ API authorization failed with HTTP {(int)attempt.StatusCode} ({attempt.ReasonPhrase}). Ensure your CJ Personal Access Token has commissions API access and matches the provided publisher/site identifier.",
                null,
                attempt.StatusCode);
        }

        if (attempt.ErrorMessages.Count > 0)
        {
            var error = string.Join(" | ", attempt.ErrorMessages);
            if (attempt.ErrorMessages.Any(x => x.Contains("No authorized publisher ids were specified", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "CJ GraphQL query failed: No authorized publisher ids were specified in the query. Enter your CJ Publisher ID (CID) in the Publisher ID field, not your Website ID.");
            }

            throw new InvalidOperationException($"CJ GraphQL query failed: {error}");
        }

        if (attempt.Partners.Count == 0 && !string.IsNullOrWhiteSpace(websiteId))
        {
            var fallbackPayload = BuildGraphQlPayload(graphQlQuery, publisherId, websiteId: null);
            var fallbackAttempt = await ExecuteGraphQlFetchAttemptAsync(endpoint, developerKey, fallbackPayload, ct);

            if (fallbackAttempt.IsAuthorizationError)
            {
                throw new HttpRequestException(
                    $"CJ API authorization failed with HTTP {(int)fallbackAttempt.StatusCode} ({fallbackAttempt.ReasonPhrase}). Ensure your CJ Personal Access Token has commissions API access and matches the provided publisher/site identifier.",
                    null,
                    fallbackAttempt.StatusCode);
            }

            if (fallbackAttempt.ErrorMessages.Count > 0)
            {
                var fallbackError = string.Join(" | ", fallbackAttempt.ErrorMessages);
                throw new InvalidOperationException($"CJ GraphQL query failed: {fallbackError}");
            }

            if (fallbackAttempt.Partners.Count > 0)
            {
                var fallbackDeduped = fallbackAttempt.Partners
                    .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserId) || !string.IsNullOrWhiteSpace(x.AdvertiserName))
                    .GroupBy(x => BuildPartnerKey(x.AdvertiserId, x.AdvertiserName), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(maxResults)
                    .ToArray();

                return new CjPartnerFetchResult(
                    Partners: fallbackDeduped,
                    Message: $"Fetched {fallbackDeduped.Length} CJ advertisers from commissions data after retrying without Website ID filter.");
            }
        }

        if (attempt.Partners.Count > 0 && maxResults >= AdvertiserLookupPageSize)
        {
            try
            {
                var advertiserLookupFallback = await FetchPartnersViaAdvertiserLookupAsync(
                    requestDeveloperKey: developerKey,
                    publisherId: publisherId,
                    maxResults: maxResults,
                    ct: ct);

                if (advertiserLookupFallback.Partners.Count > 0)
                {
                    var merged = attempt.Partners
                        .Concat(advertiserLookupFallback.Partners)
                        .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserId) || !string.IsNullOrWhiteSpace(x.AdvertiserName))
                        .GroupBy(x => BuildPartnerKey(x.AdvertiserId, x.AdvertiserName), StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .Take(maxResults)
                        .ToArray();

                    return new CjPartnerFetchResult(
                        Partners: merged,
                        Message: $"Fetched {merged.Length} CJ advertisers by combining commissions data with active advertiser lookup.");
                }
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == HttpStatusCode.BadRequest &&
                ex.Message.Contains("CJ advertiser lookup failed", StringComparison.OrdinalIgnoreCase))
            {
                // Ignore lookup failures here; commissions data is still usable and keeps validation stable.
            }
        }

        if (attempt.Partners.Count == 0)
        {
            CjPartnerFetchResult advertiserLookupFallback;
            try
            {
                advertiserLookupFallback = await FetchPartnersViaAdvertiserLookupAsync(
                    requestDeveloperKey: developerKey,
                    publisherId: publisherId,
                    maxResults: maxResults,
                    ct: ct);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == HttpStatusCode.BadRequest &&
                ex.Message.Contains("CJ advertiser lookup failed", StringComparison.OrdinalIgnoreCase))
            {
                advertiserLookupFallback = new CjPartnerFetchResult(
                    Partners: Array.Empty<CjPartnerRecord>(),
                    Message: "Connected to CJ commissions API, but advertiser lookup fallback returned HTTP 400. No partner records were returned.");
            }

            if (advertiserLookupFallback.Partners.Count > 0)
            {
                return advertiserLookupFallback;
            }

            return new CjPartnerFetchResult(
                Partners: Array.Empty<CjPartnerRecord>(),
                Message: "Connected to CJ commissions API, but no commission records were returned. This can be expected when the publisher has no recent commissions for the selected Website ID (or none at all). Try leaving Website ID blank to widen the query.");
        }

        var deduped = attempt.Partners
            .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserId) || !string.IsNullOrWhiteSpace(x.AdvertiserName))
            .GroupBy(x => BuildPartnerKey(x.AdvertiserId, x.AdvertiserName), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(maxResults)
            .ToArray();

        return new CjPartnerFetchResult(
            Partners: deduped,
            Message: $"Fetched {deduped.Length} CJ advertisers from commissions data.");
    }

    private async Task<CjPartnerFetchResult> FetchPartnersViaAdvertiserLookupAsync(
        string requestDeveloperKey,
        string publisherId,
        int maxResults,
        CancellationToken ct)
    {
        var unique = new Dictionary<string, CjPartnerRecord>(StringComparer.OrdinalIgnoreCase);
        HttpStatusCode? lastStatusCode = null;
        string? lastReason = null;
        string? lastBody = null;

        // Strategy 1: broad request by requestor CID only.
        for (var page = 1; ; page += 1)
        {
            var attempt = await ExecuteFetchAttemptAsync(
                BuildAdvertiserLookupUrl(publisherId, page, keywords: null),
                requestDeveloperKey,
                ct);

            lastStatusCode = attempt.StatusCode;
            lastReason = attempt.ReasonPhrase;
            lastBody = attempt.Body;

            if (attempt.IsAuthorizationError)
            {
                throw new HttpRequestException(
                    $"CJ advertiser lookup authorization failed with HTTP {(int)attempt.StatusCode} ({attempt.ReasonPhrase}). Verify your CJ Personal Access Token has advertiser lookup access.",
                    null,
                    attempt.StatusCode);
            }

            if ((int)attempt.StatusCode >= 400)
            {
                break;
            }

            if (attempt.Partners.Count == 0)
            {
                break;
            }

            foreach (var partner in attempt.Partners)
            {
                if (string.IsNullOrWhiteSpace(partner.AdvertiserId) && string.IsNullOrWhiteSpace(partner.AdvertiserName))
                {
                    continue;
                }

                var key = BuildPartnerKey(partner.AdvertiserId, partner.AdvertiserName);
                unique.TryAdd(key, partner);
            }

            if (attempt.Partners.Count < AdvertiserLookupPageSize || unique.Count >= maxResults)
            {
                break;
            }
        }

        // Strategy 2: keyword crawl. Some CJ advertiser-lookup setups return sparse/empty results
        // without an explicit keyword filter even for valid requestor CID values.
        if (unique.Count < maxResults)
        {
            foreach (var keyword in AdvertiserLookupKeywordSeeds)
            {
                for (var page = 1; ; page += 1)
                {
                    var attempt = await ExecuteFetchAttemptAsync(
                        BuildAdvertiserLookupUrl(publisherId, page, keyword),
                        requestDeveloperKey,
                        ct);

                    lastStatusCode = attempt.StatusCode;
                    lastReason = attempt.ReasonPhrase;
                    lastBody = attempt.Body;

                    if (attempt.IsAuthorizationError)
                    {
                        throw new HttpRequestException(
                            $"CJ advertiser lookup authorization failed with HTTP {(int)attempt.StatusCode} ({attempt.ReasonPhrase}). Verify your CJ Personal Access Token has advertiser lookup access.",
                            null,
                            attempt.StatusCode);
                    }

                    if ((int)attempt.StatusCode >= 400)
                    {
                        break;
                    }

                    if (attempt.Partners.Count == 0)
                    {
                        break;
                    }

                    foreach (var partner in attempt.Partners)
                    {
                        if (string.IsNullOrWhiteSpace(partner.AdvertiserId) && string.IsNullOrWhiteSpace(partner.AdvertiserName))
                        {
                            continue;
                        }

                        var key = BuildPartnerKey(partner.AdvertiserId, partner.AdvertiserName);
                        unique.TryAdd(key, partner);
                    }

                    if (attempt.Partners.Count < AdvertiserLookupPageSize || unique.Count >= maxResults)
                    {
                        break;
                    }
                }

                if (unique.Count >= maxResults)
                {
                    break;
                }
            }
        }

        var final = unique.Values.Take(maxResults).ToArray();

        if (final.Length > 0)
        {
            return new CjPartnerFetchResult(
                Partners: final,
                Message: $"Fetched {final.Length} active CJ advertisers.");
        }

        if (lastStatusCode is not null && (int)lastStatusCode.Value >= 400)
        {
            throw new HttpRequestException(
                $"CJ advertiser lookup failed with HTTP {(int)lastStatusCode.Value} ({lastReason}). Body: {lastBody}",
                null,
                lastStatusCode.Value);
        }

        return new CjPartnerFetchResult(
            Partners: Array.Empty<CjPartnerRecord>(),
            Message: "Connected to CJ APIs, but no active advertisers were returned for this publisher. Verify your CID is correct and you have joined advertisers in your CJ account.");
    }

    private static string BuildAdvertiserLookupUrl(string publisherId, int pageNumber, string? keywords)
    {
        var cid = Uri.EscapeDataString(publisherId);
        var keywordSegment = string.IsNullOrWhiteSpace(keywords)
            ? string.Empty
            : $"&keywords={Uri.EscapeDataString(keywords)}";

        return $"https://advertiser-lookup.api.cj.com/v3/advertiser-lookup?requestor-cid={cid}&records-per-page={AdvertiserLookupPageSize}&page-number={pageNumber}{keywordSegment}";
    }

    private static object BuildGraphQlPayload(string graphQlQuery, string publisherId, string? websiteId)
    {
        var variables = new Dictionary<string, object?>
        {
            ["publisherIds"] = new[] { publisherId }
        };

        if (!string.IsNullOrWhiteSpace(websiteId))
        {
            variables["websiteIds"] = new[] { websiteId };
        }

        return new
        {
            query = graphQlQuery,
            variables
        };
    }

    private async Task<CjFetchAttemptResult> ExecuteFetchAttemptAsync(string url, string developerKey, CancellationToken ct)
    {
        // CJ has had multiple auth conventions across APIs; try raw token first, then Bearer.
        var rawAttempt = await SendFetchAttemptAsync(url, developerKey, useBearerScheme: false, ct);
        if (rawAttempt.Partners.Count > 0)
        {
            return rawAttempt;
        }

        var bearerAttempt = await SendFetchAttemptAsync(url, developerKey, useBearerScheme: true, ct);
        if (bearerAttempt.Partners.Count > 0)
        {
            return bearerAttempt;
        }

        if (rawAttempt.IsAuthorizationError && !bearerAttempt.IsAuthorizationError)
        {
            return bearerAttempt;
        }

        if (!rawAttempt.IsAuthorizationError && bearerAttempt.IsAuthorizationError)
        {
            return rawAttempt;
        }

        if (rawAttempt.IsLikelyGraphiQlUi && !bearerAttempt.IsLikelyGraphiQlUi)
        {
            return bearerAttempt;
        }

        if (!rawAttempt.IsLikelyGraphiQlUi && bearerAttempt.IsLikelyGraphiQlUi)
        {
            return rawAttempt;
        }

        return bearerAttempt;
    }

    private async Task<CjFetchAttemptResult> SendFetchAttemptAsync(string url, string developerKey, bool useBearerScheme, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (useBearerScheme)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", developerKey);
        }
        else
        {
            httpRequest.Headers.TryAddWithoutValidation("Authorization", developerKey);
        }

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        using var response = await http.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        IReadOnlyCollection<CjPartnerRecord> partners = Array.Empty<CjPartnerRecord>();
        if (response.IsSuccessStatusCode)
        {
            partners = TryParseJson(body);
            if (partners.Count == 0)
            {
                partners = TryParseXml(body);
            }
        }

        return new CjFetchAttemptResult(
            StatusCode: response.StatusCode,
            ReasonPhrase: response.ReasonPhrase,
            Body: body,
            Partners: partners);
    }

    private async Task<CjGraphQlFetchAttemptResult> ExecuteGraphQlFetchAttemptAsync(
        string endpoint,
        string developerKey,
        object payload,
        CancellationToken ct)
    {
        var rawAttempt = await SendGraphQlAttemptAsync(endpoint, developerKey, payload, useBearerScheme: false, ct);
        if (rawAttempt.Partners.Count > 0 && rawAttempt.ErrorMessages.Count == 0)
        {
            return rawAttempt;
        }

        var bearerAttempt = await SendGraphQlAttemptAsync(endpoint, developerKey, payload, useBearerScheme: true, ct);
        if (bearerAttempt.Partners.Count > 0 && bearerAttempt.ErrorMessages.Count == 0)
        {
            return bearerAttempt;
        }

        if (rawAttempt.IsAuthorizationError && !bearerAttempt.IsAuthorizationError)
        {
            return bearerAttempt;
        }

        if (!rawAttempt.IsAuthorizationError && bearerAttempt.IsAuthorizationError)
        {
            return rawAttempt;
        }

        if (rawAttempt.ErrorMessages.Count > 0 && bearerAttempt.ErrorMessages.Count == 0)
        {
            return rawAttempt;
        }

        if (bearerAttempt.ErrorMessages.Count > 0)
        {
            return bearerAttempt;
        }

        return rawAttempt;
    }

    private async Task<CjGraphQlFetchAttemptResult> SendGraphQlAttemptAsync(
        string endpoint,
        string developerKey,
        object payload,
        bool useBearerScheme,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        if (useBearerScheme)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", developerKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", developerKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var partners = TryParseGraphQlPartners(body);
        var errors = TryParseGraphQlErrors(body);

        return new CjGraphQlFetchAttemptResult(
            StatusCode: response.StatusCode,
            ReasonPhrase: response.ReasonPhrase,
            Body: body,
            Partners: partners,
            ErrorMessages: errors);
    }

    private static string BuildQueryUrl(string endpoint, string query)
    {
        var separator = endpoint.Contains('?') ? "&" : "?";
        return $"{endpoint}{separator}{query}";
    }

    private static bool IsCommissionsApiEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals(CommissionsApiHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEndpointUrl(string endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new ArgumentException("CJ endpoint URL is required.", nameof(endpointUrl));
        }

        var trimmed = endpointUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("CJ endpoint URL must be a valid absolute URL.", nameof(endpointUrl));
        }

        if (!uri.Host.Equals("commissions.api.cj.com", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var path = uri.AbsolutePath.Trim();
        if (path is "/" or "")
        {
            var builder = new UriBuilder(uri)
            {
                Path = "/query"
            };

            return builder.Uri.ToString().TrimEnd('/');
        }

        return trimmed;
    }

    private static string BuildPartnerQuery(string websiteId, int maxResults, bool includeWebsiteId, bool joinedOnly)
    {
        var sql = joinedOnly
            ? "SELECT advertiser-id, advertiser-name, relationship-status, primary-category, country, advertiser-url FROM advertisers WHERE relationship-status = 'joined'"
            : "SELECT advertiser-id, advertiser-name, relationship-status, primary-category, country, advertiser-url FROM advertisers";

        var common = $"records-per-page={maxResults}&q={Uri.EscapeDataString(sql)}";
        return includeWebsiteId
            ? $"website-id={Uri.EscapeDataString(websiteId)}&{common}"
            : common;
    }

    private static string CreateBodyPreview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty body)";
        }

        var sanitized = body
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        const int max = 280;
        return sanitized.Length <= max ? sanitized : sanitized[..max] + "...";
    }

    private sealed record CjFetchAttemptResult(
        HttpStatusCode StatusCode,
        string? ReasonPhrase,
        string Body,
        IReadOnlyCollection<CjPartnerRecord> Partners)
    {
        public bool IsAuthorizationError => StatusCode == HttpStatusCode.Unauthorized || StatusCode == HttpStatusCode.Forbidden;

        public bool IsLikelyGraphiQlUi =>
            Partners.Count == 0 &&
            (Body.Contains("graphiql", StringComparison.OrdinalIgnoreCase) ||
             Body.Contains("LICENSE AGREEMENT For GraphiQL software", StringComparison.OrdinalIgnoreCase) ||
             Body.Contains("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record CjGraphQlFetchAttemptResult(
        HttpStatusCode StatusCode,
        string? ReasonPhrase,
        string Body,
        IReadOnlyCollection<CjPartnerRecord> Partners,
        IReadOnlyCollection<string> ErrorMessages)
    {
        public bool IsAuthorizationError => StatusCode == HttpStatusCode.Unauthorized || StatusCode == HttpStatusCode.Forbidden;
    }

    private static IReadOnlyCollection<CjPartnerRecord> TryParseGraphQlPartners(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("publisherCommissions", out var commissions) ||
                commissions.ValueKind != JsonValueKind.Object ||
                !commissions.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<CjPartnerRecord>();
            }

            var parsed = records
                .EnumerateArray()
                .Select(record => new CjPartnerRecord(
                    AdvertiserId: ReadJsonString(record, "advertiserId"),
                    AdvertiserName: ReadJsonString(record, "advertiserName"),
                    RelationshipStatus: ReadJsonString(record, "actionStatus"),
                    Country: ReadJsonString(record, "country"),
                    PrimaryCategory: string.Empty,
                    DetailsUrl: string.Empty))
                .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserName) || !string.IsNullOrWhiteSpace(x.AdvertiserId))
                .ToArray();

            return parsed;
        }
        catch
        {
            return Array.Empty<CjPartnerRecord>();
        }
    }

    private static IReadOnlyCollection<string> TryParseGraphQlErrors(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return errors
                .EnumerateArray()
                .Select(error => ReadJsonString(error, "message"))
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind != JsonValueKind.Null)
        {
            return value.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string BuildPartnerKey(string advertiserId, string advertiserName)
    {
        var safeId = advertiserId?.Trim() ?? string.Empty;
        var safeName = advertiserName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(safeId) ? safeName : safeId;
    }

    private static IReadOnlyCollection<CjPartnerRecord> TryParseJson(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            var advertisers = new List<JsonElement>();
            CollectArrayValues(document.RootElement, "advertisers", advertisers);
            CollectArrayValues(document.RootElement, "results", advertisers);
            CollectArrayValues(document.RootElement, "records", advertisers);
            CollectArrayValues(document.RootElement, "items", advertisers);
            CollectArrayValues(document.RootElement, "data", advertisers);

            if (advertisers.Count == 0 && document.RootElement.ValueKind == JsonValueKind.Array)
            {
                advertisers.AddRange(document.RootElement.EnumerateArray());
            }

            if (advertisers.Count == 0)
            {
                CollectObjectValues(document.RootElement, advertisers);
            }

            var partners = advertisers
                .Select(MapJsonPartner)
                .Where(x => x is not null)
                .Cast<CjPartnerRecord>()
                .ToArray();

            return partners;
        }
        catch
        {
            return Array.Empty<CjPartnerRecord>();
        }
    }

    private static IReadOnlyCollection<CjPartnerRecord> TryParseXml(string payload)
    {
        try
        {
            var document = XDocument.Parse(payload);
            var advertisers = document
                .Descendants()
                .Where(x =>
                    x.Name.LocalName.Equals("advertiser", StringComparison.OrdinalIgnoreCase) ||
                    x.Name.LocalName.Equals("record", StringComparison.OrdinalIgnoreCase) ||
                    x.Descendants().Any(d =>
                        d.Name.LocalName.Equals("advertiser-id", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.LocalName.Equals("advertiser-name", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase)));

            var partners = advertisers
                .Select(advertiser => new CjPartnerRecord(
                    AdvertiserId: ReadValue(advertiser, "advertiser-id", "advertiserId", "id", "cid"),
                    AdvertiserName: ReadValue(advertiser, "advertiser-name", "advertiserName", "name"),
                    RelationshipStatus: ReadValue(advertiser, "relationship-status", "relationshipStatus", "status", "relationship"),
                    Country: ReadValue(advertiser, "country"),
                    PrimaryCategory: ReadValue(advertiser, "primary-category", "primaryCategory", "category"),
                    DetailsUrl: ReadValue(advertiser, "advertiser-url", "advertiserUrl", "detailsUrl", "programUrl")))
                .Where(x => !string.IsNullOrWhiteSpace(x.AdvertiserName))
                .ToArray();

            return partners;
        }
        catch
        {
            return Array.Empty<CjPartnerRecord>();
        }
    }

    private static void CollectArrayValues(JsonElement element, string propertyName, List<JsonElement> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    result.AddRange(property.Value.EnumerateArray());
                }
                else
                {
                    CollectArrayValues(property.Value, propertyName, result);
                }
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectArrayValues(child, propertyName, result);
            }
        }
    }

    private static void CollectObjectValues(JsonElement element, List<JsonElement> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            result.Add(element);

            foreach (var property in element.EnumerateObject())
            {
                CollectObjectValues(property.Value, result);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectObjectValues(child, result);
            }
        }
    }

    private static CjPartnerRecord? MapJsonPartner(JsonElement element)
    {
        static string Read(JsonElement parent, params string[] names)
        {
            foreach (var name in names)
            {
                if (parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    return value.ToString() ?? string.Empty;
                }
            }
            return string.Empty;
        }

        var name = Read(element, "advertiserName", "advertiser-name", "advertiser_name", "name", "companyName");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new CjPartnerRecord(
            AdvertiserId: Read(element, "advertiserId", "advertiser-id", "advertiser_id", "id", "cid"),
            AdvertiserName: name,
            RelationshipStatus: Read(element, "relationshipStatus", "relationship-status", "relationship_status", "status", "relationship"),
            Country: Read(element, "country", "countryCode"),
            PrimaryCategory: Read(element, "primaryCategory", "primary-category", "primary_category", "category"),
            DetailsUrl: Read(element, "advertiserUrl", "advertiser-url", "detailsUrl", "programUrl"));
    }

    private static string ReadValue(XElement element, params string[] localNames)
    {
        foreach (var localName in localNames)
        {
            var value = element
                .Descendants()
                .FirstOrDefault(x => x.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}

