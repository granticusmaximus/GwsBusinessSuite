using System.Text;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Geocoding;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Application.RoadTraffic;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Something in the news or on Overwatch that may affect a client (19-11-26).
public sealed record ClientImpact(Guid ContactId, string Client, string Kind, string Title, string Detail, double? Miles, string? Link);
// Where a person or company turns up in the app: Sentinel pages and World News stories (19-11-26).
public sealed record ClientMention(string Kind, string Title, string Detail, string Link);

public static class ClientImpactKinds
{
    public const string Hazard = "Hazard nearby";
    public const string Weather = "Weather alert";
    public const string News = "News nearby";
    public const string Named = "Named in the news";
}

// Client impact alerts and the people-and-companies view (19-11-26). A client with a location gets
// the hazards, NWS alerts and city-placed World News stories within 50 miles; any client whose company
// name a recent story's headline names gets that story. Mentions are Sentinel pages linked to the
// contact or naming them (only those the person viewing may open: ISentinelAccessService) and World
// News stories naming them.
public sealed partial class ClientRadarService(
    IAppDbContextFactory contexts,
    IGeocodingService geocoder,
    HazardLayerService hazards,
    INwsAlertsService weather,
    WorldNewsMapService map,
    WorldNewsService news,
    ISentinelAccessService access,
    IOllamaService ollama,
    IOptions<ContentStudioOptions> options,
    TimeProvider time,
    ILogger<ClientRadarService> logger)
{
    public const double NearbyMiles = 50;
    public const int MaxClients = 60;
    private static readonly TimeSpan NewsWindow = TimeSpan.FromDays(2);

    // Returns a problem to show, or null when saved (an empty location clears it).
    public async Task<string?> SetLocationAsync(Guid contactId, string? location, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return "That contact is gone.";
        var text = location?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            (contact.Location, contact.Latitude, contact.Longitude) = (null, null, null);
        }
        else
        {
            var found = await geocoder.GeocodeAsync(text, ct);
            if (found is null) return $"Couldn't find \"{text}\" on the map. Try a city and state or country.";
            (contact.Location, contact.Latitude, contact.Longitude) = (text, found.Latitude, found.Longitude);
        }
        contact.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<IReadOnlyList<ClientImpact>> ImpactsAsync(Contact contact, CancellationToken ct = default) =>
        Match(contact, await NearbyAsync(contact, ct), RecentHeadlines(), time.GetUtcNow());

    // Every located or named client with something to report, most affected first.
    public async Task<IReadOnlyList<(Contact Client, IReadOnlyList<ClientImpact> Impacts)>> AllImpactsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var clients = (await db.Contacts.AsNoTracking().Where(c => c.TrashedAt == null && (c.Latitude != null || c.Company != null)).ToListAsync(ct))
            .OrderByDescending(c => c.Latitude is not null).ThenBy(c => c.FullName).Take(MaxClients).ToList();
        var headlines = RecentHeadlines();
        var now = time.GetUtcNow();
        var results = new List<(Contact, IReadOnlyList<ClientImpact>)>();
        foreach (var client in clients)
        {
            var impacts = Match(client, await NearbyAsync(client, ct), headlines, now);
            if (impacts.Count > 0) results.Add((client, impacts));
        }
        return results.OrderByDescending(r => r.Item2.Count).ToList();
    }

    public sealed record Nearby(IReadOnlyList<HazardFeature> Hazards, IReadOnlyList<WeatherAlert> Alerts, IReadOnlyList<WorldNewsMapSignal> Stories);

    private async Task<Nearby> NearbyAsync(Contact contact, CancellationToken ct)
    {
        if (contact.Latitude is not { } lat || contact.Longitude is not { } lon) return new([], [], []);
        var bounds = Around(lat, lon, NearbyMiles);
        var hazardList = await TryAsync("hazards", async () => (await hazards.GetHazardsAsync(bounds, ct)).Features) ?? [];
        var alertList = await TryAsync("weather alerts", () => weather.GetActiveAlertsAsync(bounds, ct)) ?? [];
        var now = time.GetUtcNow();
        var stories = await TryAsync("news", () => Task.Run(() => map.SignalsAt(bounds, now - NewsWindow, null, SituationKinds.Operational), ct)) ?? [];
        return new(hazardList, alertList, stories);
    }

    private IReadOnlyList<EventOverview> RecentHeadlines() =>
        news.Store.TopEvents(time.GetUtcNow() - NewsWindow, 1500);

    // Pure, for tests: what near or named news means for one client.
    public static IReadOnlyList<ClientImpact> Match(Contact contact, Nearby nearby, IReadOnlyList<EventOverview> headlines, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(contact.Company) ? contact.FullName : $"{contact.FullName} ({contact.Company})";
        var impacts = new List<ClientImpact>();
        if (contact.Latitude is { } lat && contact.Longitude is { } lon)
        {
            foreach (var hazard in nearby.Hazards)
            {
                var miles = RoadTrafficSummary.Miles(lat, lon, hazard.Latitude, hazard.Longitude);
                if (miles <= NearbyMiles)
                    impacts.Add(new(contact.Id, name, ClientImpactKinds.Hazard, hazard.Title, $"{hazard.Kind}, {hazard.Severity}", miles, string.IsNullOrWhiteSpace(hazard.Url) ? null : hazard.Url));
            }
            foreach (var alert in nearby.Alerts)
                impacts.Add(new(contact.Id, name, ClientImpactKinds.Weather, alert.Event, alert.AreaDescription, null, null));
            foreach (var story in nearby.Stories.Where(s => s.Location.City is not null))
            {
                var miles = RoadTrafficSummary.Miles(lat, lon, story.Latitude, story.Longitude);
                if (miles <= NearbyMiles)
                    impacts.Add(new(contact.Id, name, ClientImpactKinds.News, story.Title, $"{story.Location.City}, {story.OutletCount} outlets", miles,
                        $"admin/world-news?situation={Uri.EscapeDataString(story.SituationId)}"));
            }
        }
        if (CompanyPattern(contact.Company) is { } pattern)
            foreach (var story in headlines.Where(e => pattern.IsMatch(e.Headline ?? e.Title)).Take(5))
                impacts.Add(new(contact.Id, name, ClientImpactKinds.Named, story.Headline ?? story.Title, $"{story.OutletCount} outlets", null,
                    $"admin/world-news/events/{Uri.EscapeDataString(story.Id)}"));
        return impacts.OrderBy(i => i.Miles ?? double.MaxValue).ToList();
    }

    // A company name as a whole word, without "Inc", "LLC" and the like; too short or generic names
    // ("Apple" would still match fruit, so names under 4 letters are left out entirely) give none.
    public static Regex? CompanyPattern(string? company)
    {
        if (string.IsNullOrWhiteSpace(company)) return null;
        var core = Suffix().Replace(company.Trim(), "").Trim(' ', ',', '.');
        return core.Length < 4 ? null : new Regex($@"\b{Regex.Escape(core)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public async Task<IReadOnlyList<ClientMention>> MentionsAsync(Contact contact, string username, CancellationToken ct = default)
    {
        var mentions = new List<ClientMention>();
        var name = contact.FullName.Trim();
        var company = contact.Company?.Trim();
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var pages = await db.WikiPages.AsNoTracking()
                .Where(p => p.TrashedAt == null && (p.LinkedContactId == contact.Id || p.Title.Contains(name) || p.Markdown.Contains(name)
                    || (company != null && company.Length >= 4 && (p.Title.Contains(company) || p.Markdown.Contains(company)))))
                .OrderBy(p => p.Id).Take(200)
                .Select(p => new { p.Id, p.Title, p.Slug, p.LinkedContactId })
                .ToListAsync(ct);
            var allowed = await access.GetAccessibleTargetsAsync(pages.Select(p => new SentinelAccessTarget(p.Id, false)).ToList(), username, SentinelAccessLevels.View, ct);
            foreach (var page in pages.Where(p => allowed.Contains(new SentinelAccessTarget(p.Id, false))).Take(25))
                mentions.Add(new("Sentinel", page.Title, page.LinkedContactId == contact.Id ? "Linked to this contact" : "Names them", $"admin/sentinel/{page.Slug}"));
        }
        var people = new Regex($@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var firm = CompanyPattern(company);
        foreach (var story in news.Store.TopEvents(time.GetUtcNow().AddDays(-30), 3000)
                     .Where(e => people.IsMatch(e.Headline ?? e.Title) || firm?.IsMatch(e.Headline ?? e.Title) == true).Take(15))
            mentions.Add(new("World News", story.Headline ?? story.Title, $"{story.OutletCount} outlets, last report {story.LastSeen.ToLocalGwsDate()}",
                $"admin/world-news/events/{Uri.EscapeDataString(story.Id)}"));
        return mentions;
    }

    public const string SummarySystemPrompt =
        "You brief a small-business owner on one client before a call. From the records given, write 3 to 5 plain sentences: " +
        "where the relationship stands, money owed or expected, open problems, and anything in the news worth raising. " +
        "Use only what you are given. No headings, no lists, no markdown.";

    // SentinelGPT's short brief on the client, Mac first. Throws when no model answers.
    public async Task<string?> SummaryAsync(Guid contactId, IReadOnlyList<ClientMention> mentions, IReadOnlyList<ClientImpact> impacts, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null) return null;
        var deals = await db.Deals.AsNoTracking().Where(d => d.ContactId == contactId).ToListAsync(ct);
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.LineItems).Where(i => i.ContactId == contactId).ToListAsync(ct);
        var tickets = await db.SupportTickets.AsNoTracking().Where(t => t.ContactId == contactId).ToListAsync(ct);
        var notes = (await db.ContactActivities.AsNoTracking().Where(a => a.ContactId == contactId).ToListAsync(ct))
            .OrderByDescending(a => a.CreatedAt).Take(8).ToList();
        var prompt = new StringBuilder($"Client: {contact.FullName}{(string.IsNullOrWhiteSpace(contact.Company) ? "" : $", {contact.Company}")}; status {contact.Status}.\n");
        foreach (var d in deals) prompt.AppendLine($"Deal: {d.Title}, {d.Stage}, {GwsMoney.Usd(d.ValueUsd)}");
        foreach (var i in invoices) prompt.AppendLine($"Invoice: {i.Title}, {i.Status}, {GwsMoney.Usd(i.LineItems.Sum(x => x.Quantity * x.UnitPriceUsd))}, due {i.DueDate?.ToGwsDate() ?? "-"}");
        foreach (var t in tickets) prompt.AppendLine($"Ticket: {t.Subject}, {t.Status}");
        foreach (var n in notes) prompt.AppendLine($"Note {n.CreatedAt.ToGwsDate()}: {Trim(n.Note, 200)}");
        foreach (var m in mentions.Take(10)) prompt.AppendLine($"{m.Kind}: {m.Title}");
        foreach (var x in impacts.Take(8)) prompt.AppendLine($"{x.Kind}: {x.Title}");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        var text = await ollama.GenerateAsync(options.Value.Model, SummarySystemPrompt, prompt.ToString(), limit.Token);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static BoundingBox Around(double lat, double lon, double miles)
    {
        var dLat = miles / 69.0;
        var dLon = miles / (69.0 * Math.Max(0.1, Math.Cos(lat * Math.PI / 180)));
        return new BoundingBox(Math.Min(90, lat + dLat), Math.Max(-90, lat - dLat), Math.Min(180, lon + dLon), Math.Max(-180, lon - dLon));
    }

    private async Task<T?> TryAsync<T>(string what, Func<Task<T>> load) where T : class
    {
        try { return await load(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Client radar: {What} couldn't be read", what);
            return null;
        }
    }

    [GeneratedRegex(@"[,\s]+(inc|llc|ltd|limited|corp|corporation|co|plc|gmbh|group|company)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffix();
}
