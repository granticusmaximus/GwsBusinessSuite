using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Infrastructure.Services;

public interface ILocalEventsScraperService
{
    // Actually launches a headless browser and scrapes both sites; owns the cache write.
    // Only ever called by LocalEventsRefreshBackgroundService on its own hourly cadence -
    // never inline from GovernmentIntelligenceService.BuildCommunityCoverageAsync, since a
    // browser render (~5-10s per site) is far too slow for that 15-minute snapshot path.
    Task<IReadOnlyList<CivicEvent>> RefreshAsync(CancellationToken ct = default);

    // Pure cache read, no I/O, never triggers a browser launch - what
    // GovernmentIntelligenceService actually calls.
    IReadOnlyList<CivicEvent> GetCachedEventsOrEmpty();
}

// Neither of these sources renders its event data in the raw server HTML - both need a
// real (headless) browser to execute their client-side JS before the DOM contains
// anything. This is the only Playwright-based scraper in the app; every other
// GovernmentIntelligenceService source is plain HttpClient + regex because it happens to
// be server-rendered. Two other candidate sources (Warner Robins city + its CVB) actively
// block headless Chrome entirely, and two more (Visit Perry/Wix, Perry Area Chamber) are
// client-only app shells with no discoverable public event data - neither is scraped here.
public sealed class LocalEventsScraperService(
    IMemoryCache cache,
    ILogger<LocalEventsScraperService> logger) : ILocalEventsScraperService
{
    private const string CacheKey = "government-intelligence:local-events";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    private const string PerryCalendarUrl = "https://www.perry-ga.gov/calendar-all-events";
    private const string ChamberEventsUrl = "https://chamber.robinsregion.com/events/";

    private static readonly Regex TitlePunctuationRegex = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PerryTimeRegex = new(
        @"^(?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?<meridiem>[ap])$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<CivicEvent> GetCachedEventsOrEmpty() =>
        cache.TryGetValue(CacheKey, out IReadOnlyList<CivicEvent>? cached) && cached is not null
            ? cached
            : [];

    public async Task<IReadOnlyList<CivicEvent>> RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CivicEvent> events;
        try
        {
            events = await ScrapeAsync(ct);
        }
        catch (Exception ex)
        {
            // Total failure (e.g. browser install missing) - keep serving the last good
            // snapshot rather than blanking the section out.
            logger.LogWarning(ex, "Local Events: Playwright refresh failed entirely");
            events = GetCachedEventsOrEmpty();
        }

        cache.Set(CacheKey, events, CacheDuration);
        return events;
    }

    private async Task<IReadOnlyList<CivicEvent>> ScrapeAsync(CancellationToken ct)
    {
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var events = new List<CivicEvent>();

        try
        {
            await using var perryPage = await browser.NewPageAsync();
            await perryPage.GotoAsync(PerryCalendarUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            // FullCalendar finishes its client-side render a moment after the network
            // goes idle - a short settle delay avoids reading an incomplete DOM.
            await perryPage.WaitForTimeoutAsync(2000);
            events.AddRange(await ExtractPerryEventsAsync(perryPage));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local Events: Perry, GA calendar scrape failed");
        }

        try
        {
            await using var chamberPage = await browser.NewPageAsync();
            await chamberPage.GotoAsync(ChamberEventsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            events.AddRange(await ExtractChamberEventsAsync(chamberPage));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Local Events: Robins Region Chamber scrape failed");
        }

        return DeduplicateEvents(events);
    }

    // Public + operates on IPage so tests can call it directly against
    // page.SetContentAsync(fixtureHtml) - the exact same code path as production.
    public static async Task<List<CivicEvent>> ExtractPerryEventsAsync(IPage page)
    {
        var results = new List<CivicEvent>();
        foreach (var eventEl in await page.QuerySelectorAllAsync(".fc-event"))
        {
            // FullCalendar's month view renders the day-cell dates in a separate sibling
            // table (.fc-bg, one row of <td data-date="..."> per weekday) from the event
            // cells (.fc-content-skeleton, one row per stacking "lane") - the two are NOT
            // nested, they're correlated purely by column position within their shared
            // .fc-row. So the date has to be looked up via the event's column index, not
            // via a DOM-ancestor walk.
            var dateAttr = await eventEl.EvaluateAsync<string?>("""
                el => {
                    const row = el.closest('.fc-row');
                    const eventTd = el.closest('td');
                    if (!row || !eventTd) return null;
                    const colIndex = Array.from(eventTd.parentElement.children).indexOf(eventTd);
                    const bgRow = row.querySelector('.fc-bg tr');
                    const dateCell = bgRow?.children[colIndex];
                    return dateCell ? dateCell.getAttribute('data-date') : null;
                }
                """);
            if (string.IsNullOrWhiteSpace(dateAttr))
            {
                continue;
            }

            var titleEl = await eventEl.QuerySelectorAsync(".fc-title");
            var title = CleanText(titleEl is null ? string.Empty : await titleEl.InnerTextAsync());
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var timeEl = await eventEl.QuerySelectorAsync(".fc-time");
            var timeText = CleanText(timeEl is null ? string.Empty : await timeEl.InnerTextAsync());

            results.Add(new CivicEvent(
                title,
                // No per-event URL exists - clicking an event opens a JS modal
                // (.calendar-event-viewer), not a distinct page - so link back to the
                // calendar itself rather than leaving Url empty.
                PerryCalendarUrl,
                ParsePerryStartAt(dateAttr!, timeText),
                null,
                string.Empty,
                "Perry, GA City Calendar",
                null));
        }

        return results;
    }

    public static async Task<List<CivicEvent>> ExtractChamberEventsAsync(IPage page)
    {
        var results = new List<CivicEvent>();
        foreach (var card in await page.QuerySelectorAllAsync(".gz-events-card"))
        {
            var titleLink = await card.QuerySelectorAsync(".gz-card-title a");
            if (titleLink is null)
            {
                continue;
            }

            var title = CleanText(await titleLink.InnerTextAsync());
            var url = await titleLink.GetAttributeAsync("href");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var startSpan = await card.QuerySelectorAsync(".gz-card-date span[content]");
            var startAt = ParseIsoOrNull(startSpan is null ? null : await startSpan.GetAttributeAsync("content"));

            var endMeta = await card.QuerySelectorAsync(".gz-card-date meta[content]");
            var endAt = ParseIsoOrNull(endMeta is null ? null : await endMeta.GetAttributeAsync("content"));

            var imgEl = await card.QuerySelectorAsync(".gz-events-img");
            var imageUrl = imgEl is null ? null : await imgEl.GetAttributeAsync("src");

            results.Add(new CivicEvent(
                title,
                url!,
                startAt,
                endAt,
                string.Empty,
                "Robins Region Chamber of Commerce",
                imageUrl));
        }

        return results;
    }

    // Dedup key: normalized title + date-only (not time), since the same regional event
    // could plausibly be listed on both sources with slightly different phrasing or
    // timestamps. When both sources report the same key, keep the richer entry (a real
    // time-of-day and/or an image beats Perry's date-only, linkless, imageless entries).
    public static IReadOnlyList<CivicEvent> DeduplicateEvents(IEnumerable<CivicEvent> events)
    {
        var byKey = new Dictionary<string, CivicEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in events)
        {
            var key = BuildDedupKey(candidate);
            if (!byKey.TryGetValue(key, out var existing) || Score(candidate) > Score(existing))
            {
                byKey[key] = candidate;
            }
        }

        return byKey.Values
            .OrderBy(e => e.StartAt ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    private static string BuildDedupKey(CivicEvent ev)
    {
        var normalizedTitle = WhitespaceRegex.Replace(
            TitlePunctuationRegex.Replace(ev.Title.ToLowerInvariant(), string.Empty), " ").Trim();
        var dateKey = ev.StartAt?.Date.ToString("yyyy-MM-dd") ?? "unknown";
        return $"{normalizedTitle}|{dateKey}";
    }

    private static int Score(CivicEvent ev) =>
        (ev.StartAt is { } s && s.TimeOfDay != TimeSpan.Zero ? 1 : 0) +
        (string.IsNullOrEmpty(ev.ImageUrl) ? 0 : 1);

    private static DateTimeOffset? ParsePerryStartAt(string isoDate, string timeText)
    {
        if (!DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", out var date))
        {
            return null;
        }

        var match = PerryTimeRegex.Match(timeText);
        if (!match.Success)
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        var hour = int.Parse(match.Groups["hour"].Value);
        var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value) : 0;
        var isPm = match.Groups["meridiem"].Value.Equals("p", StringComparison.OrdinalIgnoreCase);
        if (isPm && hour < 12) hour += 12;
        if (!isPm && hour == 12) hour = 0;

        return new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);
    }

    private static DateTimeOffset? ParseIsoOrNull(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateTime.TryParse(value, out var parsed)
            ? new DateTimeOffset(parsed, TimeSpan.Zero)
            : null;

    private static string CleanText(string value) =>
        WhitespaceRegex.Replace(value, " ").Trim();
}
