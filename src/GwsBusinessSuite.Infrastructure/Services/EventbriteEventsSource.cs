using System.Globalization;
using System.Text.Json;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public interface IEventbriteEventsSource
{
    Task<IReadOnlyList<CivicEvent>> FetchAsync(CancellationToken ct = default);
}

// Eventbrite's public event-search API was withdrawn - /v3/events/search/ now returns 404, and
// what remains of the API only serves events belonging to organisations you own, which is no use
// for discovering what is on around Middle Georgia. Their public browse pages, however, embed the
// same listings as schema.org Event objects inside a window.__SERVER_DATA__ blob, published for
// search-engine crawlers. That is what this reads: plain HTTP and JSON, no browser, no API key,
// and no logged-in session (unlike Facebook/Instagram/Nextdoor, whose event data sits behind an
// auth wall their terms forbid automating against).
public sealed class EventbriteEventsSource(
    IHttpClientFactory httpClientFactory,
    ILogger<EventbriteEventsSource> logger) : IEventbriteEventsSource
{
    // Eventbrite's own browse slugs. Kathleen and Bonaire have no slug of their own - they are
    // unincorporated and Eventbrite folds them into the surrounding towns - so they are covered
    // by the Warner Robins and Perry pulls plus the distance filter.
    private static readonly (string Slug, string City)[] Locations =
    [
        ("ga--warner-robins", CivicPlaces.WarnerRobins),
        ("ga--perry", CivicPlaces.Perry),
        ("ga--macon", CivicPlaces.Macon),
        ("ga--byron", CivicPlaces.Byron),
        ("ga--centerville", CivicPlaces.Centerville),
    ];

    private const string OnlineAttendance = "OnlineEventAttendanceMode";

    public async Task<IReadOnlyList<CivicEvent>> FetchAsync(CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(nameof(EventbriteEventsSource));
        var all = new List<CivicEvent>();

        foreach (var (slug, city) in Locations)
        {
            try
            {
                all.AddRange(await FetchLocationAsync(client, slug, city, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One town's page failing must not lose the others - each is an independent pull.
                logger.LogWarning(ex, "Eventbrite: {Slug} failed", slug);
            }
        }

        // The same event is listed under more than one nearby town, so de-duplicate on the
        // listing URL rather than the title (two venues genuinely run "Trivia Night").
        return all
            .GroupBy(e => e.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.StartAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    private async Task<IReadOnlyList<CivicEvent>> FetchLocationAsync(
        HttpClient client, string slug, string fallbackCity, CancellationToken ct)
    {
        var html = await client.GetStringAsync($"https://www.eventbrite.com/d/{slug}/events/", ct);

        // Same extractor the civic.extractEvents workflow node uses, so a fix to either the
        // schema.org handling or the state-blob scan benefits both rather than drifting apart.
        var extracted = SchemaOrgEventExtractor.Extract(html);
        if (extracted.Count == 0)
        {
            logger.LogWarning("Eventbrite: {Slug} yielded no schema.org events", slug);
        }

        return extracted
            // Webinars are listed under a town but happen nowhere near it, and a listing with no
            // venue at all cannot be placed - neither belongs in a "near me" feed.
            .Where(e => !e.IsOnline && !string.IsNullOrWhiteSpace(e.Venue))
            .Select(e => new CivicEvent(
                e.Title,
                e.Url,
                e.StartAt,
                e.EndAt,
                e.Venue,
                "Eventbrite",
                e.ImageUrl,
                string.IsNullOrWhiteSpace(e.City) ? fallbackCity : e.City,
                e.MilesFromHome ?? CivicPlaces.MilesFor(fallbackCity)))
            .ToList();
    }
}
