using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Settings wrapper so FederalCivicFeedService can be constructor-injected with its API key
// the same way DependencyInjection.cs already resolves other per-feature config (see
// LiveShow:RecordingsPath) - a full IOptions<T> class would be ceremony for one string.
// api.congress.gov (an api.data.gov-hosted API, unlike www.congress.gov which fronts
// scraping attempts with a Cloudflare challenge - see GovernmentIntelligenceService's
// federal StatusNote) accepts the public "DEMO_KEY" without signup, rate-limited but
// sufficient for one fetch per hour; set CongressApi:ApiKey in configuration to use a real
// key instead.
public sealed record CongressApiSettings(string ApiKey);

public sealed class FederalCivicFeedService(
    HttpClient http,
    IMemoryCache cache,
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    CongressApiSettings settings,
    ILogger<FederalCivicFeedService> logger) : IFederalCivicFeedService
{
    private const string SenateNewsCacheKey = "federal-civic:news:senate";
    private const string HouseNewsCacheKey = "federal-civic:news:house";
    private const string SenateFloorCacheKey = "federal-civic:floor:senate";
    private const string HouseFloorCacheKey = "federal-civic:floor:house";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private const int MaxNewsItemsPerChamber = 8;

    // In-session is approximated from how recently the official Congressional Record was
    // published for a chamber - Congress doesn't sit every day (weekends, recesses), so a
    // short gap is normal and doesn't mean "out of session"; a longer gap does.
    private static readonly TimeSpan RecentSessionWindow = TimeSpan.FromDays(3);

    // C-SPAN was tried first (both its own page and Brightcove's minimal player embed) but
    // its live channel requires cable/satellite TV-provider sign-in (Brightcove's catalog API
    // returns error_code SOURCES_RESTRICTED / TVE_AUTH) - confirmed with a real browser, not
    // fixable by any embed technique. The Clerk of the House's own live-video API below is
    // the real replacement: official, CORS-open, no auth wall, verified working end to end.
    // No equivalent was found for the Senate (their old floor.senate.gov webcast domain no
    // longer resolves), so the Senate panel never gets a video - see RefreshFloorStatusAsync.
    private const string HouseLatestFloorUrl = "https://liveproxy-azapp-prod-eastus2-003.azurewebsites.net/latest/floor";
    private const string HouseBroadcastEventsBaseUrl = "https://liveproxy-azapp-prod-eastus2-003.azurewebsites.net/broadcastevents/";

    public IReadOnlyList<FederalNewsItem> GetCachedSenateNewsOrEmpty() =>
        cache.TryGetValue(SenateNewsCacheKey, out IReadOnlyList<FederalNewsItem>? cached) && cached is not null ? cached : [];

    public IReadOnlyList<FederalNewsItem> GetCachedHouseNewsOrEmpty() =>
        cache.TryGetValue(HouseNewsCacheKey, out IReadOnlyList<FederalNewsItem>? cached) && cached is not null ? cached : [];

    public FloorStatus GetCachedSenateFloorOrEmpty() =>
        cache.TryGetValue(SenateFloorCacheKey, out FloorStatus? cached) && cached is not null ? cached : EmptyFloorStatus;

    public FloorStatus GetCachedHouseFloorOrEmpty() =>
        cache.TryGetValue(HouseFloorCacheKey, out FloorStatus? cached) && cached is not null ? cached : EmptyFloorStatus;

    private static readonly FloorStatus EmptyFloorStatus = new(false, string.Empty, null, null);

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var newsTask = RefreshNewsAsync(ct);
        var floorTask = RefreshFloorStatusAsync(ct);
        await Task.WhenAll(newsTask, floorTask);
    }

    private async Task RefreshNewsAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.congress.gov/v3/bill?api_key={Uri.EscapeDataString(settings.ApiKey)}&sort=updateDate+desc&limit=40&format=json";
            var response = await http.GetFromJsonAsync<BillListResponse>(url, JsonOptions, ct);
            var bills = response?.Bills ?? [];

            var senate = bills
                .Where(b => string.Equals(b.OriginChamber, "Senate", StringComparison.OrdinalIgnoreCase))
                .Take(MaxNewsItemsPerChamber)
                .Select(ToNewsItem)
                .ToList();
            var house = bills
                .Where(b => string.Equals(b.OriginChamber, "House", StringComparison.OrdinalIgnoreCase))
                .Take(MaxNewsItemsPerChamber)
                .Select(ToNewsItem)
                .ToList();

            cache.Set(SenateNewsCacheKey, (IReadOnlyList<FederalNewsItem>)senate, CacheDuration);
            cache.Set(HouseNewsCacheKey, (IReadOnlyList<FederalNewsItem>)house, CacheDuration);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Federal Civic Feed: news refresh failed");
        }
    }

    private static FederalNewsItem ToNewsItem(BillListItem bill)
    {
        var chamberSlug = string.Equals(bill.OriginChamber, "Senate", StringComparison.OrdinalIgnoreCase) ? "senate-bill" : "house-bill";
        var typeSlug = bill.Type?.ToLowerInvariant() switch
        {
            "hres" => "house-resolution",
            "hjres" => "house-joint-resolution",
            "hconres" => "house-concurrent-resolution",
            "sres" => "senate-resolution",
            "sjres" => "senate-joint-resolution",
            "sconres" => "senate-concurrent-resolution",
            _ => chamberSlug
        };
        var url = $"https://www.congress.gov/bill/{bill.Congress}th-congress/{typeSlug}/{bill.Number}";
        DateTimeOffset? publishedAt = DateOnly.TryParse(bill.LatestAction?.ActionDate, out var d)
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;

        return new FederalNewsItem(
            bill.Title ?? $"{bill.Type} {bill.Number}",
            url,
            bill.LatestAction?.Text,
            publishedAt,
            "Congress.gov");
    }

    // Congress.gov's Congressional Record API only publishes PDF links, not extracted text
    // (there is no HTML/plain-text edition and www.congress.gov itself sits behind a
    // Cloudflare challenge for server-side clients - see GovernmentIntelligenceService),
    // so what's archived here is an honest description of the official issue plus a direct
    // link to the authoritative PDF, not fabricated transcript text.
    private async Task RefreshFloorStatusAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.congress.gov/v3/congressional-record?api_key={Uri.EscapeDataString(settings.ApiKey)}&limit=5&format=json";
            var response = await http.GetFromJsonAsync<CongressionalRecordResponse>(url, JsonOptions, ct);
            var latestIssue = response?.Results?.Issues?.FirstOrDefault();
            if (latestIssue is null || !DateOnly.TryParse(latestIssue.PublishDate, out var sessionDate))
            {
                cache.Set(SenateFloorCacheKey, EmptyFloorStatus, CacheDuration);
                cache.Set(HouseFloorCacheKey, EmptyFloorStatus, CacheDuration);
                return;
            }

            var inSession = DateTime.UtcNow.Date - sessionDate.ToDateTime(TimeOnly.MinValue) <= RecentSessionWindow;

            var senatePdf = latestIssue.Links?.Senate?.Pdf?.FirstOrDefault()?.Url;
            var housePdf = latestIssue.Links?.House?.Pdf?.FirstOrDefault()?.Url;

            var senateSummary = senatePdf is null ? null : await UpsertTranscriptAsync("Senate", sessionDate, senatePdf, latestIssue, ct);
            var houseSummary = housePdf is null ? null : await UpsertTranscriptAsync("House", sessionDate, housePdf, latestIssue, ct);

            // Senate has no confirmed working live-video source (see the constants comment
            // above), so its status leans entirely on the Congressional-Record-recency
            // heuristic and never gets a LiveEmbedUrl.
            var senateStatus = new FloorStatus(
                inSession,
                inSession
                    ? $"Senate floor proceedings on record for {sessionDate:MMMM d, yyyy} (Congressional Record Issue {latestIssue.Issue})."
                    : "The Senate is not currently in session.",
                null,
                senateSummary);

            // House gets a more precise, authoritative in-session signal from its own live
            // broadcast API (see GetHouseLiveVideoAsync) instead of the Record-recency
            // heuristic - it can tell us "live right now", not just "recently sat".
            var (houseIsLive, houseHlsUrl) = await GetHouseLiveVideoAsync(ct);
            var houseStatus = new FloorStatus(
                houseIsLive,
                houseIsLive
                    ? "The House is currently live on the floor."
                    : "The House is not currently in session.",
                houseHlsUrl,
                houseSummary);

            cache.Set(SenateFloorCacheKey, senateStatus, CacheDuration);
            cache.Set(HouseFloorCacheKey, houseStatus, CacheDuration);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Federal Civic Feed: floor status refresh failed");
        }
    }

    // Two calls: /latest/floor resolves to the most recent legislative day (so we don't have
    // to guess "today" vs. "last session day" ourselves), then /broadcastevents/{day} carries
    // the actual isLiveBroadcast flag plus HLS/DASH stream URLs for that day's broadcast.
    // Both are official Clerk-of-the-House endpoints, unauthenticated, CORS-open on the
    // stream URLs themselves (verified) - this is what live.house.gov's own player calls.
    private async Task<(bool IsLive, string? HlsUrl)> GetHouseLiveVideoAsync(CancellationToken ct)
    {
        try
        {
            var floor = await http.GetFromJsonAsync<HouseFloorDay>(HouseLatestFloorUrl, JsonOptions, ct);
            if (string.IsNullOrWhiteSpace(floor?.Id))
            {
                return (false, null);
            }

            var events = await http.GetFromJsonAsync<List<HouseBroadcastEvent>>(HouseBroadcastEventsBaseUrl + floor.Id, JsonOptions, ct);
            var latest = events?.FirstOrDefault();
            var isLive = string.Equals(latest?.IsLiveBroadcast, "True", StringComparison.OrdinalIgnoreCase);
            if (!isLive)
            {
                return (false, null);
            }

            var hlsUrl = latest?.Asset?.Files?.FirstOrDefault(f =>
                string.Equals(f.Type, "HLS", StringComparison.OrdinalIgnoreCase) &&
                f.Url is not null && f.Url.Contains("/east/", StringComparison.OrdinalIgnoreCase))?.Url;
            return (true, hlsUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Federal Civic Feed: House live video check failed");
            return (false, null);
        }
    }

    private async Task<CongressionalTranscriptSummary> UpsertTranscriptAsync(
        string chamber, DateOnly sessionDate, string pdfUrl, CongressionalRecordIssue issue, CancellationToken ct)
    {
        var sessionDateUtc = new DateTimeOffset(sessionDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var description =
            $"Official Congressional Record, {chamber} Section — Congress {issue.Congress}, Session {issue.Session}, " +
            $"Volume {issue.Volume}, Issue {issue.Issue}, published {sessionDate:MMMM d, yyyy}. " +
            "Full proceedings text is published as PDF by Congress.gov; the link below opens the official document.";

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var existing = await db.CongressionalFloorTranscripts
            .Where(t => t.Chamber == chamber)
            .ToListAsync(ct);
        var row = existing.FirstOrDefault(t => t.SessionDate.Date == sessionDateUtc.Date);

        if (row is null)
        {
            row = new CongressionalFloorTranscript
            {
                Chamber = chamber,
                SessionDate = sessionDateUtc,
                SourceUrl = pdfUrl,
                FullText = description,
                Excerpt = description,
                FetchedAt = DateTimeOffset.UtcNow,
                CreatedBy = "federal-civic-feed"
            };
            db.CongressionalFloorTranscripts.Add(row);
        }
        else
        {
            row.SourceUrl = pdfUrl;
            row.FullText = description;
            row.Excerpt = description;
            row.FetchedAt = DateTimeOffset.UtcNow;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = "federal-civic-feed";
        }

        await db.SaveChangesAsync(ct);

        return new CongressionalTranscriptSummary(row.Id, row.Chamber, sessionDate, row.SourceUrl, row.Excerpt, row.FetchedAt);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record BillListResponse([property: JsonPropertyName("bills")] List<BillListItem>? Bills);

    private sealed record BillListItem(
        [property: JsonPropertyName("congress")] int Congress,
        [property: JsonPropertyName("number")] string? Number,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("originChamber")] string? OriginChamber,
        [property: JsonPropertyName("latestAction")] BillLatestAction? LatestAction);

    private sealed record BillLatestAction(
        [property: JsonPropertyName("actionDate")] string? ActionDate,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record CongressionalRecordResponse([property: JsonPropertyName("Results")] CongressionalRecordResults? Results);

    private sealed record CongressionalRecordResults([property: JsonPropertyName("Issues")] List<CongressionalRecordIssue>? Issues);

    private sealed record CongressionalRecordIssue(
        [property: JsonPropertyName("Congress")] string? Congress,
        [property: JsonPropertyName("Session")] string? Session,
        [property: JsonPropertyName("Volume")] string? Volume,
        [property: JsonPropertyName("Issue")] string? Issue,
        [property: JsonPropertyName("PublishDate")] string? PublishDate,
        [property: JsonPropertyName("Links")] CongressionalRecordLinks? Links);

    private sealed record CongressionalRecordLinks(
        [property: JsonPropertyName("House")] CongressionalRecordSection? House,
        [property: JsonPropertyName("Senate")] CongressionalRecordSection? Senate);

    private sealed record CongressionalRecordSection([property: JsonPropertyName("PDF")] List<CongressionalRecordPdf>? Pdf);

    private sealed record CongressionalRecordPdf([property: JsonPropertyName("Url")] string? Url);

    private sealed record HouseFloorDay([property: JsonPropertyName("_id")] string? Id);

    private sealed record HouseBroadcastEvent(
        [property: JsonPropertyName("isLiveBroadcast")] string? IsLiveBroadcast,
        [property: JsonPropertyName("asset")] HouseBroadcastAsset? Asset);

    private sealed record HouseBroadcastAsset([property: JsonPropertyName("files")] List<HouseBroadcastFile>? Files);

    private sealed record HouseBroadcastFile(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("url")] string? Url);
}
