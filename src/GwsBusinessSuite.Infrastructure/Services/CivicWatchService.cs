using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using UglyToad.PdfPig;

namespace GwsBusinessSuite.Infrastructure.Services;

// Every source here was verified live (2026-10-07) and needs no key: the Census geocoder,
// the congress-legislators dataset, the Georgia General Assembly API (same token handshake as
// GovernmentIntelligenceService), api.congress.gov (DEMO_KEY unless CongressApi:ApiKey is set),
// the Federal Register API, Grants.gov search2, and the Houston County website.
public sealed partial class CivicWatchService(
    IAppDbContextFactory dbContextFactory,
    HttpClient http,
    IMemoryCache cache,
    CongressApiSettings congressApi,
    IOllamaService ollama,
    OllamaWorkloadScheduler ollamaWorkloads,
    IOptions<ContentStudioOptions> studioOptions,
    IMailTransport mailTransport,
    IOptions<GrowthReportEmailOptions> smtpOptions,
    TimeProvider timeProvider,
    ILogger<CivicWatchService> logger,
    IAutomationTriggerService? automationTriggers = null) : ICivicWatchService
{
    private const string CensusCoordinatesUrl = "https://geocoding.geo.census.gov/geocoder/geographies/coordinates";
    private const string CensusAddressUrl = "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress";
    private const string LegislatorsUrl = "https://unitedstates.github.io/congress-legislators/legislators-current.json";
    private const string GeorgiaApiBaseUrl = "https://www.legis.ga.gov/api/";
    private const string GeorgiaLegislationPageUrl = "https://www.legis.ga.gov/legislation/";
    private const string CongressApiBaseUrl = "https://api.congress.gov/v3/";
    private const string FederalRegisterUrl = "https://www.federalregister.gov/api/v1/documents.json";
    private const string GrantsSearchUrl = "https://api.grants.gov/v1/api/search2";
    private const string GrantDetailUrl = "https://www.grants.gov/search-results-detail/";
    private const string CountyBaseUrl = "https://www.houstoncountyga.gov/";
    private const string CountyMinutesUrl = "https://www.houstoncountyga.gov/commissioner/meeting-minutes.cms";
    private const string CountyElectionsUrl = "https://www.houstoncountyga.gov/residents/board-of-elections.cms";
    private const string CountyElectionDatesUrl = "https://www.houstoncountyga.gov/residents/election-dates-municipal-elections.cms";

    private const string LegislatorsCacheKey = "civic-watch:legislators";
    private const string GeorgiaTokenCacheKey = "civic-watch:georgia:token";
    private const string GeorgiaSessionCacheKey = "civic-watch:georgia:session";
    private const string ElectionsCacheKey = "civic-watch:elections";
    private static readonly TimeSpan FeedCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromMinutes(2);
    private const int FederalRegisterPerKeyword = 6;
    private const int GrantsPerKeyword = 6;
    private const int MaxMeetingDocuments = 12;
    private const int MaxPdfBytes = 8 * 1024 * 1024;
    private const int MaxSummaryInputChars = 6000;
    private const int MaxSummaryAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static int CurrentCongressNumber(DateTimeOffset now) => 118 + (now.Year - 2023) / 2;

    // ================================================================ Settings / location

    public async Task<CivicWatchSettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.CivicWatchSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new CivicWatchSettings();
        return ToView(row);
    }

    public async Task<CivicWatchSettingsView> SaveSettingsAsync(CivicWatchSettingsView settings, string username, CancellationToken ct = default)
    {
        var label = (settings.HomeLabel ?? string.Empty).Trim();
        if (label.Length == 0) throw new ArgumentException("Give your home area a name.");
        if (label.Length > 120) throw new ArgumentException("Keep the area name under 120 characters.");
        var recipient = settings.AlertRecipient?.Trim();
        if (settings.AlertsEnabled && !NewsWatchService.IsEmailAddress(recipient))
            throw new ArgumentException("Enter a valid email address for bill alerts.");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.CivicWatchSettings.FirstOrDefaultAsync(ct);
        var isNew = row is null;
        row ??= new CivicWatchSettings { CreatedBy = username };

        var address = string.IsNullOrWhiteSpace(settings.HomeAddress) ? null : settings.HomeAddress.Trim();
        double latitude = settings.HomeLatitude, longitude = settings.HomeLongitude;
        if (address is not null && !string.Equals(address, row.HomeAddress, StringComparison.OrdinalIgnoreCase))
        {
            var point = await GeocodeAddressAsync(address, ct)
                        ?? throw new ArgumentException("The Census geocoder couldn't find that address. Check the spelling, or clear it and enter coordinates.");
            (latitude, longitude) = point;
        }
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ArgumentException("Coordinates are out of range.");

        var moved = isNew || Math.Abs(latitude - row.HomeLatitude) > 0.00001 || Math.Abs(longitude - row.HomeLongitude) > 0.00001;
        row.HomeLabel = label;
        row.HomeAddress = address;
        row.HomeLatitude = latitude;
        row.HomeLongitude = longitude;
        row.FederalRegisterKeywords = string.Join('\n', CivicKeywords.Split(settings.FederalRegisterKeywords));
        row.GrantKeywords = string.Join('\n', CivicKeywords.Split(settings.GrantKeywords));
        row.AlertsEnabled = settings.AlertsEnabled;
        row.AlertRecipient = string.IsNullOrWhiteSpace(recipient) ? null : recipient;
        row.UpdatedAt = timeProvider.GetUtcNow();
        row.UpdatedBy = username;

        if (moved || row.DistrictsResolvedAt is null)
        {
            var districts = await LookupDistrictsAsync(latitude, longitude, ct);
            if (districts is not null)
            {
                row.StateCode = districts.StateCode;
                row.CountyName = districts.County;
                row.CongressionalDistrict = districts.Congressional;
                row.StateSenateDistrict = districts.StateSenate;
                row.StateHouseDistrict = districts.StateHouse;
                row.DistrictsResolvedAt = timeProvider.GetUtcNow();
            }
            else if (moved)
            {
                // A stale district from the old location would be worse than none.
                row.StateCode = row.CountyName = null;
                row.CongressionalDistrict = row.StateSenateDistrict = row.StateHouseDistrict = null;
                row.DistrictsResolvedAt = null;
            }
        }

        if (isNew) db.CivicWatchSettings.Add(row);
        await db.SaveChangesAsync(ct);
        return ToView(row);
    }

    private static CivicWatchSettingsView ToView(CivicWatchSettings row) => new(
        row.HomeLabel, row.HomeLatitude, row.HomeLongitude, row.HomeAddress, row.StateCode, row.CountyName,
        row.CongressionalDistrict, row.StateSenateDistrict, row.StateHouseDistrict, row.DistrictsResolvedAt,
        row.FederalRegisterKeywords, row.GrantKeywords, row.AlertsEnabled, row.AlertRecipient);

    private async Task<(double Latitude, double Longitude)?> GeocodeAddressAsync(string address, CancellationToken ct)
    {
        var json = await GetStringOrNullAsync(
            $"{CensusAddressUrl}?address={Uri.EscapeDataString(address)}&benchmark=Public_AR_Current&format=json", ct);
        return json is null ? null : ParseGeocodedPoint(json);
    }

    public static (double Latitude, double Longitude)? ParseGeocodedPoint(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || !result.TryGetProperty("addressMatches", out var matches)
                || matches.ValueKind != JsonValueKind.Array || matches.GetArrayLength() == 0)
                return null;
            var coordinates = matches[0].GetProperty("coordinates");
            return (coordinates.GetProperty("y").GetDouble(), coordinates.GetProperty("x").GetDouble());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public sealed record ResolvedDistricts(string StateCode, string? County, int? Congressional, int? StateSenate, int? StateHouse);

    private async Task<ResolvedDistricts?> LookupDistrictsAsync(double latitude, double longitude, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{CensusCoordinatesUrl}?x={longitude}&y={latitude}&benchmark=Public_AR_Current&vintage=Current_Current&format=json");
        var json = await GetStringOrNullAsync(url, ct);
        return json is null ? null : ParseDistricts(json);
    }

    // Layer names carry the Congress / redistricting year ("120th Congressional Districts",
    // "2026 State Legislative Districts - Upper"), so they are matched by suffix, not exact name.
    public static ResolvedDistricts? ParseDistricts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var geographies = doc.RootElement.GetProperty("result").GetProperty("geographies");
            string? state = null, county = null;
            int? congressional = null, upper = null, lower = null;
            foreach (var layer in geographies.EnumerateObject())
            {
                if (layer.Value.ValueKind != JsonValueKind.Array || layer.Value.GetArrayLength() == 0) continue;
                var first = layer.Value[0];
                var name = layer.Name;
                if (name == "States") state = Str(first, "STUSAB");
                else if (name == "Counties") county = Str(first, "NAME");
                else if (name.EndsWith("Congressional Districts", StringComparison.OrdinalIgnoreCase)) congressional = DistrictNumber(first);
                else if (name.EndsWith("Legislative Districts - Upper", StringComparison.OrdinalIgnoreCase)) upper = DistrictNumber(first);
                else if (name.EndsWith("Legislative Districts - Lower", StringComparison.OrdinalIgnoreCase)) lower = DistrictNumber(first);
            }
            return string.IsNullOrWhiteSpace(state) ? null : new ResolvedDistricts(state, county, congressional, upper, lower);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // BASENAME is "8" for a numbered district; at-large states use "0"/"(at Large)" - treat as 0.
    private static int? DistrictNumber(JsonElement geography)
    {
        var basename = Str(geography, "BASENAME");
        if (string.IsNullOrWhiteSpace(basename)) return null;
        var digits = new string(basename.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : basename.Contains("large", StringComparison.OrdinalIgnoreCase) ? 0 : null;
    }

    // ================================================================ Representatives

    public async Task<CivicRepresentatives> GetRepresentativesAsync(CancellationToken ct = default)
    {
        var settings = await GetSettingsAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.StateCode))
            return new([], "Set your home location in Civic Watch settings to see your representatives.");

        var members = new List<CivicRepresentative>();
        var notes = new List<string>();

        var legislators = await GetLegislatorsJsonAsync(ct);
        if (legislators is null) notes.Add("The congress-legislators directory couldn't be reached.");
        else members.AddRange(ParseFederalRepresentatives(legislators, settings.StateCode, settings.CongressionalDistrict));

        // Only Georgia's legislature has a feed here; other states get federal members only.
        if (string.Equals(settings.StateCode, "GA", StringComparison.OrdinalIgnoreCase))
        {
            var state = await LoadGeorgiaLegislatorsAsync(settings.StateSenateDistrict, settings.StateHouseDistrict, ct);
            if (state is null) notes.Add("The Georgia General Assembly member list couldn't be reached.");
            else members.AddRange(state);
        }
        else
        {
            notes.Add($"State legislators are only looked up for Georgia; {settings.StateCode} shows federal members only.");
        }

        return new(members, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private async Task<string?> GetLegislatorsJsonAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(LegislatorsCacheKey, out string? cached) && cached is not null) return cached;
        var json = await GetStringOrNullAsync(LegislatorsUrl, ct);
        if (json is not null) cache.Set(LegislatorsCacheKey, json, TimeSpan.FromHours(24));
        return json;
    }

    public static IReadOnlyList<CivicRepresentative> ParseFederalRepresentatives(string legislatorsJson, string stateCode, int? congressionalDistrict)
    {
        var results = new List<CivicRepresentative>();
        using var doc = JsonDocument.Parse(legislatorsJson);
        foreach (var person in doc.RootElement.EnumerateArray())
        {
            if (!person.TryGetProperty("terms", out var terms) || terms.GetArrayLength() == 0) continue;
            var term = terms[terms.GetArrayLength() - 1];
            if (!string.Equals(Str(term, "state"), stateCode, StringComparison.OrdinalIgnoreCase)) continue;
            var type = Str(term, "type");
            var ids = person.GetProperty("id");
            var name = person.GetProperty("name");
            var displayName = Str(name, "official_full") ?? $"{Str(name, "first")} {Str(name, "last")}".Trim();
            if (type == "sen")
            {
                var lis = Str(ids, "lis");
                if (lis is null) continue;
                results.Add(new("federal", "Senate", displayName, Str(term, "party") ?? string.Empty, stateCode.ToUpperInvariant(), lis, Str(term, "url"), Str(term, "phone")));
            }
            else if (type == "rep" && congressionalDistrict is { } district
                     && term.TryGetProperty("district", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt32() == district)
            {
                var bioguide = Str(ids, "bioguide");
                if (bioguide is null) continue;
                results.Add(new("federal", "House", displayName, Str(term, "party") ?? string.Empty,
                    $"{stateCode.ToUpperInvariant()}-{district:00}", bioguide, Str(term, "url"), Str(term, "phone")));
            }
        }
        return results.OrderBy(r => r.Chamber == "Senate" ? 0 : 1).ThenBy(r => r.Name).ToList();
    }

    private async Task<IReadOnlyList<CivicRepresentative>?> LoadGeorgiaLegislatorsAsync(int? senateDistrict, int? houseDistrict, CancellationToken ct)
    {
        if (senateDistrict is null && houseDistrict is null) return [];
        var token = await GetGeorgiaTokenAsync(ct);
        var session = token is null ? 0 : await GetGeorgiaSessionIdAsync(token, ct);
        if (token is null || session <= 0) return null;

        var results = new List<CivicRepresentative>();
        foreach (var (chamberCode, chamber, district) in new[] { (2, "Senate", senateDistrict), (1, "House", houseDistrict) })
        {
            if (district is null) continue;
            var json = await GeorgiaGetAsync($"members/list/{session}?chamber={chamberCode}", token, ct);
            if (json is null) return null;
            var member = PickGeorgiaMember(json, district.Value);
            if (member is not null) results.Add(member with { Chamber = chamber });
        }
        return results;
    }

    // A seat can list a vacated member next to the current one (special elections), so the one
    // without dateVacated wins.
    public static CivicRepresentative? PickGeorgiaMember(string membersJson, int district)
    {
        using var doc = JsonDocument.Parse(membersJson);
        JsonElement? best = null;
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            if (!m.TryGetProperty("districtNumber", out var dn) || dn.ValueKind != JsonValueKind.Number || dn.GetInt32() != district) continue;
            var vacated = m.TryGetProperty("dateVacated", out var v) && v.ValueKind == JsonValueKind.String;
            if (!vacated) { best = m; break; }
            best ??= m;
        }
        if (best is not { } member) return null;
        var party = member.TryGetProperty("party", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() switch { 0 => "Democrat", 1 => "Republican", _ => "Other" }
            : string.Empty;
        var id = member.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture);
        return new("state", string.Empty, Str(member, "fullName") ?? string.Empty, party,
            district.ToString(CultureInfo.InvariantCulture), id, null, null);
    }

    // ================================================================ Keyword feeds

    public async Task<CivicKeywordFeed<FederalRegisterDocument>> GetFederalRegisterAsync(bool refresh = false, CancellationToken ct = default)
    {
        var keywords = (await GetSettingsAsync(ct)).FederalRegisterKeywordList;
        if (keywords.Count == 0) return new([], keywords, null, null);
        var cacheKey = "civic-watch:federal-register:" + string.Join('|', keywords).ToLowerInvariant();
        if (!refresh && cache.TryGetValue(cacheKey, out CivicKeywordFeed<FederalRegisterDocument>? cached) && cached is not null) return cached;

        var since = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var items = new List<FederalRegisterDocument>();
        var failures = 0;
        foreach (var keyword in keywords)
        {
            var url = $"{FederalRegisterUrl}?per_page={FederalRegisterPerKeyword}&order=relevance"
                      + $"&conditions%5Bterm%5D={Uri.EscapeDataString(keyword)}"
                      + $"&conditions%5Bpublication_date%5D%5Bgte%5D={since}"
                      + "&conditions%5Btype%5D%5B%5D=RULE&conditions%5Btype%5D%5B%5D=PRORULE&conditions%5Btype%5D%5B%5D=NOTICE"
                      + "&fields%5B%5D=document_number&fields%5B%5D=title&fields%5B%5D=type&fields%5B%5D=html_url"
                      + "&fields%5B%5D=publication_date&fields%5B%5D=comments_close_on&fields%5B%5D=agencies&fields%5B%5D=abstract";
            var json = await GetStringOrNullAsync(url, ct);
            if (json is null) { failures++; continue; }
            items.AddRange(ParseFederalRegister(json, keyword));
        }

        var merged = items.GroupBy(i => i.DocumentNumber).Select(g => g.First())
            .OrderByDescending(i => i.CommentsCloseOn is not null)
            .ThenByDescending(i => i.PublishedOn)
            .ToList();
        var feed = new CivicKeywordFeed<FederalRegisterDocument>(merged, keywords, timeProvider.GetUtcNow(),
            failures == keywords.Count ? "The Federal Register couldn't be reached." : null);
        if (feed.Error is null) cache.Set(cacheKey, feed, FeedCacheDuration);
        return feed;
    }

    public static IReadOnlyList<FederalRegisterDocument> ParseFederalRegister(string json, string keyword)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var list = new List<FederalRegisterDocument>();
        foreach (var r in results.EnumerateArray())
        {
            var number = Str(r, "document_number");
            var title = Str(r, "title");
            var url = Str(r, "html_url");
            if (number is null || title is null || url is null) continue;
            var agencies = r.TryGetProperty("agencies", out var a) && a.ValueKind == JsonValueKind.Array
                ? string.Join(", ", a.EnumerateArray().Select(x => Str(x, "name") ?? Str(x, "raw_name")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                : string.Empty;
            list.Add(new(number, title, Str(r, "type") ?? string.Empty, url, ParseIsoDate(Str(r, "publication_date")),
                ParseIsoDate(Str(r, "comments_close_on")), agencies, Str(r, "abstract"), keyword));
        }
        return list;
    }

    public async Task<CivicKeywordFeed<GrantOpportunity>> GetGrantOpportunitiesAsync(bool refresh = false, CancellationToken ct = default)
    {
        var keywords = (await GetSettingsAsync(ct)).GrantKeywordList;
        if (keywords.Count == 0) return new([], keywords, null, null);
        var cacheKey = "civic-watch:grants:" + string.Join('|', keywords).ToLowerInvariant();
        if (!refresh && cache.TryGetValue(cacheKey, out CivicKeywordFeed<GrantOpportunity>? cached) && cached is not null) return cached;

        var items = new List<GrantOpportunity>();
        var failures = 0;
        foreach (var keyword in keywords)
        {
            try
            {
                var body = JsonSerializer.Serialize(new { keyword, rows = GrantsPerKeyword, oppStatuses = "forecasted|posted" });
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(GrantsSearchUrl, content, ct);
                if (!response.IsSuccessStatusCode) { failures++; continue; }
                items.AddRange(ParseGrants(await response.Content.ReadAsStringAsync(ct), keyword));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning(ex, "Civic Watch: Grants.gov search failed for {Keyword}", keyword);
                failures++;
            }
        }

        var merged = items.GroupBy(i => i.Id).Select(g => g.First())
            .OrderBy(i => i.CloseDate is null)
            .ThenBy(i => i.CloseDate)
            .ToList();
        var feed = new CivicKeywordFeed<GrantOpportunity>(merged, keywords, timeProvider.GetUtcNow(),
            failures == keywords.Count ? "Grants.gov couldn't be reached." : null);
        if (feed.Error is null) cache.Set(cacheKey, feed, FeedCacheDuration);
        return feed;
    }

    public static IReadOnlyList<GrantOpportunity> ParseGrants(string json, string keyword)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("oppHits", out var hits)
            || hits.ValueKind != JsonValueKind.Array) return [];
        var list = new List<GrantOpportunity>();
        foreach (var h in hits.EnumerateArray())
        {
            var id = Str(h, "id");
            var title = Str(h, "title");
            if (id is null || title is null) continue;
            list.Add(new(id, Str(h, "number") ?? string.Empty, WebUtility.HtmlDecode(title), Str(h, "agency") ?? Str(h, "agencyCode") ?? string.Empty,
                Str(h, "oppStatus") ?? string.Empty, ParseUsDate(Str(h, "openDate")), ParseUsDate(Str(h, "closeDate")),
                GrantDetailUrl + Uri.EscapeDataString(id), keyword));
        }
        return list;
    }

    // ================================================================ Bill watchlist

    public async Task<IReadOnlyList<WatchedBillView>> ListWatchedBillsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.WatchedBills.AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(b => b.Jurisdiction).ThenBy(b => b.Label).Select(ToView).ToList();
    }

    private static WatchedBillView ToView(WatchedBill b) => new(b.Id, b.Jurisdiction, b.Label, b.Title, b.LatestStatus,
        b.LatestStatusDate, b.OfficialUrl, b.LastCheckedAt, b.LastChangedAt, b.LastError);

    public sealed record ParsedBill(string Jurisdiction, string BillType, int Number, string Label);

    [GeneratedRegex(@"^(?<type>HB|SB|HR|SR)(?<n>\d{1,5})$")]
    private static partial Regex GeorgiaBillRegex();

    [GeneratedRegex(@"^(?<type>HR|S|HJRES|SJRES|HCONRES|SCONRES|HRES|SRES)(?<n>\d{1,5})$")]
    private static partial Regex FederalBillRegex();

    private static readonly IReadOnlyDictionary<string, string> FederalLabels = new Dictionary<string, string>
    {
        ["hr"] = "H.R.", ["s"] = "S.", ["hjres"] = "H.J.Res.", ["sjres"] = "S.J.Res.",
        ["hconres"] = "H.Con.Res.", ["sconres"] = "S.Con.Res.", ["hres"] = "H.Res.", ["sres"] = "S.Res."
    };

    // "H.B. 68", "hb68", "HB 68" -> HB 68; "H.R. 1", "hr1" -> H.R. 1. Georgia's HR is a House
    // Resolution, the federal HR a House bill, so the jurisdiction decides.
    public static ParsedBill? ParseBill(string jurisdiction, string text)
    {
        var compact = new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (jurisdiction == WatchedBillJurisdictions.Georgia)
        {
            var m = GeorgiaBillRegex().Match(compact);
            if (!m.Success) return null;
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            return n <= 0 ? null : new(jurisdiction, m.Groups["type"].Value, n, $"{m.Groups["type"].Value} {n}");
        }
        if (jurisdiction == WatchedBillJurisdictions.Federal)
        {
            var m = FederalBillRegex().Match(compact);
            if (!m.Success) return null;
            var type = m.Groups["type"].Value.ToLowerInvariant();
            var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            return n <= 0 ? null : new(jurisdiction, type, n, $"{FederalLabels[type]} {n}");
        }
        return null;
    }

    private sealed record BillStatus(string Title, string Status, string? StatusDate, string Url);

    // Status is null either because the bill doesn't exist (SourceFailed false) or because the
    // source didn't answer - congress.gov's shared DEMO_KEY is limited to ~30 requests an hour
    // per IP and answers 429 once the hourly hearings fetch has used it up.
    private sealed record BillLookup(BillStatus? Status, bool SourceFailed, string? FailureReason = null);

    private const string CongressRateLimited =
        "Congress.gov is rate-limiting this server right now (the shared DEMO_KEY allows about 30 requests an hour). Try again later, or set CongressApi:ApiKey to a free api.data.gov key.";

    public async Task<WatchedBillView> AddWatchedBillAsync(string jurisdiction, string billText, string username, CancellationToken ct = default)
    {
        var parsed = ParseBill(jurisdiction, billText) ?? throw new ArgumentException(jurisdiction == WatchedBillJurisdictions.Georgia
            ? "Enter a Georgia bill like \"HB 68\", \"SB 1\", \"HR 12\" or \"SR 4\"."
            : "Enter a federal bill like \"H.R. 1\", \"S. 25\" or \"H.J.Res. 7\".");
        var congress = parsed.Jurisdiction == WatchedBillJurisdictions.Federal ? CurrentCongressNumber(timeProvider.GetUtcNow()) : (int?)null;

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        if (await db.WatchedBills.AnyAsync(b => b.Jurisdiction == parsed.Jurisdiction && b.BillType == parsed.BillType
                                                && b.Number == parsed.Number && b.Congress == congress, ct))
            throw new ArgumentException($"{parsed.Label} is already on your watchlist.");

        var lookup = await LookupBillAsync(parsed, congress, ct);
        if (lookup.SourceFailed)
            throw new ArgumentException(lookup.FailureReason ?? $"{(parsed.Jurisdiction == WatchedBillJurisdictions.Georgia ? "The Georgia General Assembly" : "Congress.gov")} didn't answer. Try again in a few minutes.");
        var status = lookup.Status ?? throw new ArgumentException($"Couldn't find {parsed.Label} in the current session. Check the number.");
        var now = timeProvider.GetUtcNow();
        var row = new WatchedBill
        {
            Jurisdiction = parsed.Jurisdiction, BillType = parsed.BillType, Number = parsed.Number, Congress = congress,
            Label = parsed.Label, Title = status.Title, LatestStatus = status.Status, LatestStatusDate = status.StatusDate,
            OfficialUrl = status.Url, LastCheckedAt = now, CreatedAt = now, CreatedBy = username
        };
        db.WatchedBills.Add(row);
        await db.SaveChangesAsync(ct);
        return ToView(row);
    }

    public async Task RemoveWatchedBillAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.WatchedBills.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<WatchedBillChange>> CheckWatchedBillsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var bills = await db.WatchedBills.ToListAsync(ct);
        var changes = new List<WatchedBillChange>();
        foreach (var bill in bills)
        {
            ct.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            var lookup = await LookupBillAsync(new(bill.Jurisdiction, bill.BillType, bill.Number, bill.Label), bill.Congress, ct);
            bill.LastCheckedAt = now;
            if (lookup.Status is not { } status)
            {
                bill.LastError = lookup.SourceFailed
                    ? lookup.FailureReason is null ? "The source didn't answer on the last check." : "Congress.gov was rate-limiting on the last check."
                    : "The source no longer lists this bill.";
                continue;
            }
            bill.LastError = null;
            if (!string.IsNullOrWhiteSpace(status.Title)) bill.Title = status.Title;
            if (!string.IsNullOrWhiteSpace(status.Url)) bill.OfficialUrl = status.Url;
            if (!string.Equals(Normalize(status.Status), Normalize(bill.LatestStatus), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(status.StatusDate, bill.LatestStatusDate, StringComparison.Ordinal))
            {
                changes.Add(new(bill.Id, bill.Jurisdiction, bill.Label, bill.Title, bill.LatestStatus, status.Status, status.StatusDate, bill.OfficialUrl));
                bill.LatestStatus = status.Status;
                bill.LatestStatusDate = status.StatusDate;
                bill.LastChangedAt = now;
            }
        }
        await db.SaveChangesAsync(ct);

        if (changes.Count > 0)
        {
            await SendBillAlertAsync(changes, ct);
            await FireBillTriggersAsync(changes, ct);
        }
        return changes;
    }

    private static string Normalize(string? value) => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");

    private async Task<BillLookup> LookupBillAsync(ParsedBill bill, int? congress, CancellationToken ct) =>
        bill.Jurisdiction == WatchedBillJurisdictions.Georgia
            ? await LookupGeorgiaBillAsync(bill, ct)
            : await LookupFederalBillAsync(bill, congress ?? CurrentCongressNumber(timeProvider.GetUtcNow()), ct);

    private async Task<BillLookup> LookupGeorgiaBillAsync(ParsedBill bill, CancellationToken ct)
    {
        var token = await GetGeorgiaTokenAsync(ct);
        if (token is null) return new(null, true);
        var json = await GeorgiaPostAsync($"Legislation/searchquery/20/0?query={Uri.EscapeDataString(bill.Label)}", token, ct);
        if (json is null) return new(null, true);
        return PickGeorgiaBill(json, bill) is { } hit
            ? new(new BillStatus(hit.Title, hit.Status, hit.StatusDate, GeorgiaLegislationPageUrl + hit.LegislationId.ToString(CultureInfo.InvariantCulture)), false)
            : new(null, false);
    }

    public sealed record GeorgiaBillHit(int LegislationId, string Title, string Status, string? StatusDate);

    // HB/SB are documentType 1 (bill), HR/SR documentType 2 (resolution); chamberType 1 House,
    // 2 Senate. The search is fuzzy, so the exact type/chamber/number match is picked, current
    // session first.
    public static GeorgiaBillHit? PickGeorgiaBill(string searchJson, ParsedBill bill)
    {
        var documentType = bill.BillType is "HB" or "SB" ? 1 : 2;
        var chamberType = bill.BillType.StartsWith('H') ? 1 : 2;
        using var doc = JsonDocument.Parse(searchJson);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        GeorgiaBillHit? fallback = null;
        foreach (var r in results.EnumerateArray())
        {
            if (Int(r, "documentType") != documentType || Int(r, "chamberType") != chamberType || Int(r, "number") != bill.Number) continue;
            var date = Str(r, "statusDate");
            var hit = new GeorgiaBillHit(Int(r, "legislationId") ?? 0, Normalize(Str(r, "caption")), Normalize(Str(r, "status")),
                date is { Length: >= 10 } ? date[..10] : date);
            var isCurrent = r.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.Object
                            && s.TryGetProperty("isCurrent", out var c) && c.ValueKind == JsonValueKind.True;
            if (isCurrent) return hit;
            fallback ??= hit;
        }
        return fallback;
    }

    private async Task<BillLookup> LookupFederalBillAsync(ParsedBill bill, int congress, CancellationToken ct)
    {
        var url = $"{CongressApiBaseUrl}bill/{congress}/{bill.BillType}/{bill.Number}?api_key={Uri.EscapeDataString(congressApi.ApiKey)}&format=json";
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(null, false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new(null, true, CongressRateLimited);
            if (!response.IsSuccessStatusCode) return new(null, true);
            var parsed = ParseFederalBill(await response.Content.ReadAsStringAsync(ct), bill, congress);
            return parsed is null ? new(null, false) : new(new BillStatus(parsed.Title, parsed.Status, parsed.StatusDate, parsed.Url), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Civic Watch: congress.gov lookup failed for {Bill}", bill.Label);
            return new(null, true);
        }
    }

    public sealed record FederalBillStatus(string Title, string Status, string? StatusDate, string Url);

    public static FederalBillStatus? ParseFederalBill(string json, ParsedBill bill, int congress)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("bill", out var b)) return null;
        var latest = b.TryGetProperty("latestAction", out var la) ? la : default;
        var status = latest.ValueKind == JsonValueKind.Object ? Str(latest, "text") ?? string.Empty : string.Empty;
        var date = latest.ValueKind == JsonValueKind.Object ? Str(latest, "actionDate") : null;
        var url = Str(b, "legislationUrl") ?? $"https://www.congress.gov/bill/{congress}th-congress/{bill.BillType}/{bill.Number}";
        return new(Normalize(Str(b, "title")), Normalize(status), date, url);
    }

    private async Task SendBillAlertAsync(IReadOnlyList<WatchedBillChange> changes, CancellationToken ct)
    {
        try
        {
            var settings = await GetSettingsAsync(ct);
            if (!settings.AlertsEnabled || !MailboxAddress.TryParse(settings.AlertRecipient, out var to)) return;
            var smtp = smtpOptions.Value;
            if (!mailTransport.Describe(smtp).CanSend || !MailboxAddress.TryParse(smtp.FromAddress, out var from))
            {
                logger.LogWarning("Civic Watch: {Count} bill change(s) found but email delivery isn't set up (Settings > Email).", changes.Count);
                return;
            }
            var message = new MimeMessage();
            message.From.Add(from);
            message.To.Add(to);
            message.Subject = changes.Count == 1
                ? $"[Civic Watch] {changes[0].Label} - {changes[0].Status}"
                : $"[Civic Watch] {changes.Count} watched bills changed status";
            message.Body = new BodyBuilder { TextBody = BillAlertText(changes), HtmlBody = BillAlertHtml(changes) }.ToMessageBody();
            await mailTransport.SendAsync(message, smtp, "civic-watch-bill-alert", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Civic Watch: bill alert email failed");
        }
    }

    public static string BillAlertText(IReadOnlyList<WatchedBillChange> changes)
    {
        var text = new StringBuilder("Watched bills with a new status:\n\n");
        foreach (var c in changes)
        {
            text.Append(c.Label).Append(" - ").AppendLine(c.Title);
            text.Append("  Was: ").AppendLine(string.IsNullOrWhiteSpace(c.PreviousStatus) ? "(none)" : c.PreviousStatus);
            text.Append("  Now: ").Append(c.Status);
            if (!string.IsNullOrWhiteSpace(c.StatusDate)) text.Append(" (").Append(c.StatusDate).Append(')');
            text.AppendLine().Append("  ").AppendLine(c.Url).AppendLine();
        }
        return text.ToString();
    }

    public static string BillAlertHtml(IReadOnlyList<WatchedBillChange> changes)
    {
        var html = new StringBuilder("<h2 style=\"font-family:sans-serif\">Watched bills with a new status</h2>");
        foreach (var c in changes)
        {
            html.Append("<div style=\"font-family:sans-serif;margin:0 0 16px\"><a href=\"").Append(WebUtility.HtmlEncode(c.Url)).Append("\"><strong>")
                .Append(WebUtility.HtmlEncode(c.Label)).Append("</strong></a> ").Append(WebUtility.HtmlEncode(c.Title))
                .Append("<br><span style=\"color:#666\">Was: ").Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(c.PreviousStatus) ? "(none)" : c.PreviousStatus))
                .Append("</span><br>Now: <strong>").Append(WebUtility.HtmlEncode(c.Status)).Append("</strong>");
            if (!string.IsNullOrWhiteSpace(c.StatusDate)) html.Append(" (").Append(WebUtility.HtmlEncode(c.StatusDate)).Append(')');
            html.Append("</div>");
        }
        return html.ToString();
    }

    private async Task FireBillTriggersAsync(IReadOnlyList<WatchedBillChange> changes, CancellationToken ct)
    {
        if (automationTriggers is null) return;
        foreach (var c in changes)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    bill = c.Label, jurisdiction = c.Jurisdiction, title = c.Title, previousStatus = c.PreviousStatus,
                    status = c.Status, statusDate = c.StatusDate, url = c.Url
                });
                await automationTriggers.TriggerCivicBillStatusChangedAsync(c.Label, payload, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Civic Watch: bill-status automation trigger failed for {Bill}", c.Label);
            }
        }
    }

    // ================================================================ What changed

    public static IEnumerable<(string Key, string Desk, string Kind, string Title, string Url)> SnapshotItems(GovernmentIntelligenceSnapshot s)
    {
        foreach (var a in s.Community.Announcements) yield return (CivicItemKeys.For("community", a.Url), "community", "Announcement", a.Title, a.Url);
        foreach (var m in s.Community.Meetings) yield return (CivicItemKeys.For("community", m.Url), "community", "Meeting", m.Title, m.Url);
        foreach (var l in s.State.SignedLegislation) yield return (CivicItemKeys.For("state", l.Url), "state", "Signed law", $"{l.DocumentNumber} · {l.Title}", l.Url);
        foreach (var v in s.State.HouseVotes.Concat(s.State.SenateVotes))
            yield return (CivicItemKeys.For("state", v.DetailUrl + "#" + v.Chamber + v.RollCallNumber), "state", $"Georgia {v.Chamber} vote", $"{v.Measure} · {v.Title}", v.DetailUrl);
        foreach (var v in s.Federal.SenateVotes.Concat(s.Federal.HouseVotes))
            yield return (CivicItemKeys.For("federal", v.DetailUrl), "federal", $"U.S. {v.Chamber} roll call", string.IsNullOrWhiteSpace(v.Measure) ? v.Title : $"{v.Measure} · {v.Title}", v.DetailUrl);
    }

    public async Task RecordSightingsAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default)
    {
        var items = SnapshotItems(snapshot).Where(i => !string.IsNullOrWhiteSpace(i.Url))
            .GroupBy(i => i.Key).Select(g => g.First()).ToList();
        if (items.Count == 0) return;

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var isBaseline = !await db.CivicItemSightings.AnyAsync(ct);
        var keys = items.Select(i => i.Key).ToList();
        var known = (await db.CivicItemSightings.AsNoTracking().Where(x => keys.Contains(x.ItemKey)).Select(x => x.ItemKey).ToListAsync(ct)).ToHashSet();
        var now = timeProvider.GetUtcNow();
        foreach (var item in items.Where(i => !known.Contains(i.Key)))
        {
            db.CivicItemSightings.Add(new CivicItemSighting
            {
                ItemKey = item.Key, Desk = item.Desk, Kind = item.Kind, Title = Truncate(item.Title, 400), Url = item.Url,
                FirstSeenAt = now, IsBaseline = isBaseline, CreatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);

        // Sightings only matter while an item can still be on the page; keep 120 days.
        var cutoff = now.AddDays(-120);
        var stale = (await db.CivicItemSightings.AsNoTracking().Select(x => new { x.Id, x.FirstSeenAt }).ToListAsync(ct))
            .Where(x => x.FirstSeenAt < cutoff).Select(x => x.Id).ToList();
        if (stale.Count > 0) await db.CivicItemSightings.Where(x => stale.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<CivicChanges> GetChangesAsync(TimeSpan window, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var since = timeProvider.GetUtcNow() - window;
        // SQLite can't compare DateTimeOffset in SQL here, so filter after loading.
        var rows = (await db.CivicItemSightings.AsNoTracking().Where(x => !x.IsBaseline).ToListAsync(ct))
            .Where(x => x.FirstSeenAt >= since)
            .OrderByDescending(x => x.FirstSeenAt)
            .ToList();
        return new(rows.Select(x => new CivicChangedItem(x.Desk, x.Kind, x.Title, x.Url, x.FirstSeenAt)).ToList(),
            rows.Select(x => x.ItemKey).ToHashSet());
    }

    // ================================================================ County agendas & minutes

    public async Task<IReadOnlyList<CivicMeetingDocumentView>> ListMeetingDocumentsAsync(int take = 8, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.CivicMeetingDocuments.AsNoTracking().ToListAsync(ct);
        return rows
            .Select(r => (Row: r, Date: DateOnly.TryParseExact(r.MeetingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateOnly.MinValue))
            .OrderByDescending(x => x.Date).ThenBy(x => x.Row.Kind)
            .Take(take)
            .Select(x => new CivicMeetingDocumentView(x.Row.Id, x.Date, x.Row.Kind, x.Row.Title, x.Row.Url, x.Row.AiSummary, x.Row.LastError))
            .ToList();
    }

    public sealed record MeetingDocumentLink(string Url, DateOnly MeetingDate, string Kind);

    [GeneratedRegex("""<a\s[^>]*href="(?<href>/minutes/[^"]+?\.pdf)"[^>]*>\s*(?<label>Meeting\s+(?:Agenda|Minutes))""", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingDocumentRegex();

    [GeneratedRegex(@"(?<date>\d{4}-\d{2}-\d{2})")]
    private static partial Regex IsoDateRegex();

    public static IReadOnlyList<MeetingDocumentLink> ParseMeetingDocuments(string html)
    {
        var results = new List<MeetingDocumentLink>();
        foreach (Match m in MeetingDocumentRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            var date = IsoDateRegex().Match(href);
            if (!date.Success || !DateOnly.TryParseExact(date.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var meetingDate)) continue;
            var kind = m.Groups["label"].Value.Contains("Minutes", StringComparison.OrdinalIgnoreCase) ? "Minutes" : "Agenda";
            results.Add(new(new Uri(new Uri(CountyBaseUrl), href).AbsoluteUri, meetingDate, kind));
        }
        return results.GroupBy(r => r.Url).Select(g => g.First()).ToList();
    }

    public async Task RefreshMeetingDocumentsAsync(int maxSummaries = 2, CancellationToken ct = default)
    {
        var html = await GetStringOrNullAsync(CountyMinutesUrl, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        if (html is not null)
        {
            var links = ParseMeetingDocuments(html).OrderByDescending(l => l.MeetingDate).Take(MaxMeetingDocuments).ToList();
            var urls = links.Select(l => l.Url).ToList();
            var existing = (await db.CivicMeetingDocuments.Where(d => urls.Contains(d.Url)).Select(d => d.Url).ToListAsync(ct)).ToHashSet();
            var now = timeProvider.GetUtcNow();
            foreach (var link in links.Where(l => !existing.Contains(l.Url)))
            {
                db.CivicMeetingDocuments.Add(new CivicMeetingDocument
                {
                    Url = link.Url, MeetingDate = link.MeetingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Kind = link.Kind,
                    Title = $"County Commission {link.Kind.ToLowerInvariant()} - {link.MeetingDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}",
                    CreatedAt = now
                });
            }
            await db.SaveChangesAsync(ct);
        }

        var pending = (await db.CivicMeetingDocuments.Where(d => d.AiSummary == null && d.SummaryAttempts < MaxSummaryAttempts).ToListAsync(ct))
            .OrderByDescending(d => d.MeetingDate, StringComparer.Ordinal)
            .Take(Math.Max(0, maxSummaries))
            .ToList();
        if (pending.Count == 0) return;

        using var priority = ollamaWorkloads.UseBackgroundPriority();
        foreach (var document in pending)
        {
            ct.ThrowIfCancellationRequested();
            document.SummaryAttempts++;
            var text = await ExtractPdfTextAsync(document.Url, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                document.LastError = "The PDF had no readable text.";
                await db.SaveChangesAsync(ct);
                continue;
            }
            try
            {
                var system = document.Kind == "Minutes"
                    ? "You summarize county commission meeting minutes for residents. Reply with 3-5 short plain-text bullet lines starting with \"- \", covering what was approved, denied or discussed. No intro, no markdown headings."
                    : "You summarize a county commission meeting agenda for residents. Reply with 3-5 short plain-text bullet lines starting with \"- \", covering the decisions on the agenda that affect residents. No intro, no markdown headings.";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SummaryTimeout);
                var summary = (await ollama.GenerateAsync(studioOptions.Value.Model, system, Truncate(text, MaxSummaryInputChars), timeout.Token)).Trim();
                document.AiSummary = string.IsNullOrWhiteSpace(summary) ? null : summary;
                document.SummarizedAt = document.AiSummary is null ? null : timeProvider.GetUtcNow();
                document.LastError = document.AiSummary is null ? "SentinelGPT returned an empty summary." : null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Civic Watch: summary failed for {Url}", document.Url);
                document.LastError = "SentinelGPT was unavailable; it will retry.";
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<string?> ExtractPdfTextAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxPdfBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxPdfBytes) return null;
            return ExtractPdfText(bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Civic Watch: couldn't read PDF {Url}", url);
            return null;
        }
    }

    public static string ExtractPdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        var text = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            text.AppendLine(string.Join(' ', page.GetWords().Select(w => w.Text)));
            if (text.Length > MaxSummaryInputChars) break;
        }
        return text.ToString().Trim();
    }

    // ================================================================ Elections

    public async Task<CivicElectionInfo> GetElectionsAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && cache.TryGetValue(ElectionsCacheKey, out CivicElectionInfo? cached) && cached is not null) return cached;
        var datesTask = GetStringOrNullAsync(CountyElectionDatesUrl, ct);
        var boardTask = GetStringOrNullAsync(CountyElectionsUrl, ct);
        await Task.WhenAll(datesTask, boardTask);

        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var elections = datesTask.Result is { } datesHtml ? ParseElectionDates(datesHtml).Where(e => e.ElectionDate >= today).ToList() : [];
        var documents = boardTask.Result is { } boardHtml ? ParseElectionDocuments(boardHtml) : [];
        var info = new CivicElectionInfo(elections, documents, timeProvider.GetUtcNow(),
            datesTask.Result is null && boardTask.Result is null ? "The Houston County Board of Elections site couldn't be reached." : null);
        if (info.Error is null) cache.Set(ElectionsCacheKey, info, TimeSpan.FromHours(6));
        return info;
    }

    [GeneratedRegex(@"<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    [GeneratedRegex(@"<td[^>]*>(?<cell>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableCellRegex();

    [GeneratedRegex(@"<p[^>]*>(?<p>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ParagraphRegex();

    // Rows are: election name | election date ("November 03, 2026") | registration deadline. A
    // second deadline marked with "*" is the federal-contest deadline.
    public static IReadOnlyList<CivicElection> ParseElectionDates(string html)
    {
        var results = new List<CivicElection>();
        foreach (Match row in TableRowRegex().Matches(html))
        {
            var cells = TableCellRegex().Matches(row.Groups["row"].Value).Select(c => c.Groups["cell"].Value).ToList();
            if (cells.Count < 2) continue;
            var name = CleanHtml(cells[0]);
            var dateText = CleanHtml(cells[1]);
            if (!DateTime.TryParseExact(dateText, ["MMMM d, yyyy", "MMMM dd, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)) continue;
            var deadline = string.Empty;
            if (cells.Count >= 3)
            {
                var parts = ParagraphRegex().Matches(cells[2]).Select(p => CleanHtml(p.Groups["p"].Value)).Where(p => p.Length > 0).ToList();
                if (parts.Count == 0) parts.Add(CleanHtml(cells[2]));
                deadline = string.Join("; ", parts.Select(p => p.StartsWith('*') ? $"{p.TrimStart('*').Trim()} (federal contests)" : p));
            }
            results.Add(new(name, DateOnly.FromDateTime(date), deadline));
        }
        return results.OrderBy(r => r.ElectionDate).ToList();
    }

    [GeneratedRegex("""<a\s[^>]*href="(?<href>[^"]+)"[^>]*>(?<text>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"election|ballot|voting|polling", RegexOptions.IgnoreCase)]
    private static partial Regex ElectionWordsRegex();

    // The board's page posts the current cycle's PDFs (sample ballot, early-voting calendar,
    // notices) next to its sub-page navigation; both are useful, the navigation is deduplicated.
    public static IReadOnlyList<CivicResourceLink> ParseElectionDocuments(string html)
    {
        var links = new List<CivicResourceLink>();
        foreach (Match m in AnchorRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value).Trim();
            var text = CleanHtml(m.Groups["text"].Value);
            var isPdf = href.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (isPdf && href.Contains("Sample", StringComparison.OrdinalIgnoreCase) && href.Contains("Ballot", StringComparison.OrdinalIgnoreCase))
                text = "Sample ballot";
            if (string.IsNullOrWhiteSpace(text) || text.Equals("Click Here", StringComparison.OrdinalIgnoreCase)) continue;
            var isResidentPage = href.StartsWith("/residents/", StringComparison.OrdinalIgnoreCase)
                                 && (href.Contains("voting", StringComparison.OrdinalIgnoreCase) || href.Contains("polling", StringComparison.OrdinalIgnoreCase)
                                     || href.Contains("election-dates", StringComparison.OrdinalIgnoreCase));
            if (!(isPdf && ElectionWordsRegex().IsMatch(text + " " + href)) && !isResidentPage) continue;
            if (href.Contains("totals", StringComparison.OrdinalIgnoreCase)) continue;
            var absolute = new Uri(new Uri(CountyBaseUrl), href).AbsoluteUri;
            links.Add(new(text, absolute, isPdf ? "PDF from the Houston County Board of Elections." : "Houston County Board of Elections page."));
        }
        return links.GroupBy(l => l.Url).Select(g => g.First()).Take(14).ToList();
    }

    // ================================================================ Georgia API plumbing

    private async Task<string?> GetGeorgiaTokenAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(GeorgiaTokenCacheKey, out string? cached) && cached is not null) return cached;
        var timestamp = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var key = GovernmentIntelligenceService.ComputeGeorgiaAuthenticationKey(timestamp);
        var raw = await GetStringOrNullAsync(
            $"{GeorgiaApiBaseUrl}authentication/token?key={Uri.EscapeDataString(key)}&ms={timestamp.ToString(CultureInfo.InvariantCulture)}", ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var token = raw.Trim().Trim('"');
        cache.Set(GeorgiaTokenCacheKey, token, TimeSpan.FromMinutes(4));
        return token;
    }

    private async Task<int> GetGeorgiaSessionIdAsync(string token, CancellationToken ct)
    {
        if (cache.TryGetValue(GeorgiaSessionCacheKey, out int cached) && cached > 0) return cached;
        var json = await GeorgiaGetAsync("sessions", token, ct);
        if (json is null) return 0;
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.EnumerateArray()
            .OrderByDescending(s => s.TryGetProperty("isCurrent", out var c) && c.ValueKind == JsonValueKind.True)
            .ThenByDescending(s => Int(s, "id") ?? 0)
            .Select(s => Int(s, "id") ?? 0)
            .FirstOrDefault();
        if (id > 0) cache.Set(GeorgiaSessionCacheKey, id, TimeSpan.FromHours(6));
        return id;
    }

    private Task<string?> GeorgiaGetAsync(string relativeUrl, string token, CancellationToken ct) =>
        GeorgiaSendAsync(HttpMethod.Get, relativeUrl, token, ct);

    private Task<string?> GeorgiaPostAsync(string relativeUrl, string token, CancellationToken ct) =>
        GeorgiaSendAsync(HttpMethod.Post, relativeUrl, token, ct);

    private async Task<string?> GeorgiaSendAsync(HttpMethod method, string relativeUrl, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(GeorgiaApiBaseUrl), relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        if (method == HttpMethod.Post) request.Content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Civic Watch: Georgia API {Url} answered {Status}", request.RequestUri, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Civic Watch: Georgia API {Url} failed", request.RequestUri);
            return null;
        }
    }

    // ================================================================ Helpers

    private async Task<string?> GetStringOrNullAsync(string url, CancellationToken ct)
    {
        try
        {
            return await http.GetStringAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Civic Watch fetch failed for {Url}", url);
            return null;
        }
    }

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null
        };
    }

    private static DateOnly? ParseIsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateOnly? ParseUsDate(string? value) =>
        DateOnly.TryParseExact(value, ["MM/dd/yyyy", "M/d/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    private static string CleanHtml(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(TagRegex().Replace(html, " ")).Replace(' ', ' '), @"\s+", " ").Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
