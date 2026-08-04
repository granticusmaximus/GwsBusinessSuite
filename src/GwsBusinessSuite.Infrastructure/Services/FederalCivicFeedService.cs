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

    private const string CSpanHouseUrl = "https://www.c-span.org/networks/?chan=house";
    private const string CSpanSenateUrl = "https://www.c-span.org/networks/?chan=senate";

    public IReadOnlyList<FederalNewsItem> GetCachedSenateNewsOrEmpty() =>
        cache.TryGetValue(SenateNewsCacheKey, out IReadOnlyList<FederalNewsItem>? cached) && cached is not null ? cached : [];

    public IReadOnlyList<FederalNewsItem> GetCachedHouseNewsOrEmpty() =>
        cache.TryGetValue(HouseNewsCacheKey, out IReadOnlyList<FederalNewsItem>? cached) && cached is not null ? cached : [];

    public FloorStatus GetCachedSenateFloorOrEmpty() =>
        cache.TryGetValue(SenateFloorCacheKey, out FloorStatus? cached) && cached is not null ? cached : EmptyFloorStatus;

    public FloorStatus GetCachedHouseFloorOrEmpty() =>
        cache.TryGetValue(HouseFloorCacheKey, out FloorStatus? cached) && cached is not null ? cached : EmptyFloorStatus;

    private static readonly FloorStatus EmptyFloorStatus = new(false, string.Empty, null, null, null);

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

            var senateStatus = new FloorStatus(
                inSession,
                senatePdf is null
                    ? "No recent Senate floor activity on record."
                    : $"Senate floor proceedings on record for {sessionDate:MMMM d, yyyy} (Congressional Record Issue {latestIssue.Issue}).",
                CSpanSenateUrl,
                CSpanSenateUrl,
                senateSummary);
            var houseStatus = new FloorStatus(
                inSession,
                housePdf is null
                    ? "No recent House floor activity on record."
                    : $"House floor proceedings on record for {sessionDate:MMMM d, yyyy} (Congressional Record Issue {latestIssue.Issue}).",
                CSpanHouseUrl,
                CSpanHouseUrl,
                houseSummary);

            cache.Set(SenateFloorCacheKey, senateStatus, CacheDuration);
            cache.Set(HouseFloorCacheKey, houseStatus, CacheDuration);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Federal Civic Feed: floor status refresh failed");
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
}
