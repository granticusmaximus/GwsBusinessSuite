using FluentAssertions;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Playwright;

namespace GwsBusinessSuite.Tests;

// Feeds the exact real markup samples captured from Perry, GA's FullCalendar widget and
// Robins Region Chamber's GrowthZone event cards into the same extraction methods used in
// production (via Page.SetContentAsync, never a live network request), so this stays
// deterministic and never depends on those sites being reachable/unchanged during a test run.
[Collection("Playwright")]
public sealed class LocalEventsScraperServiceTests(PlaywrightBrowserFixture fixture)
{
    // FullCalendar's month view renders day-cell dates in a .fc-bg table and event cells
    // in a sibling .fc-content-skeleton table within the same .fc-row - the two are NOT
    // nested, they're correlated purely by column position (confirmed against the live
    // site's real DOM, not guessed). The event is deliberately placed in the 2nd column
    // (index 1), not the 1st, so this test would fail if the column-correlation logic
    // regressed to always reading the first date cell.
    [Fact]
    public async Task ExtractPerryEventsAsync_ShouldParseTitleTimeAndDate_FromTheSiblingBgTableAtTheSameColumn()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""
            <div class="fc-row fc-week fc-widget-content">
              <div class="fc-bg">
                <table>
                  <tr>
                    <td data-date="2026-07-20"></td>
                    <td data-date="2026-07-21"></td>
                    <td data-date="2026-07-22"></td>
                  </tr>
                </table>
              </div>
              <div class="fc-content-skeleton">
                <table>
                  <tr>
                    <td></td>
                    <td class="fc-event-container">
                      <a class="fc-day-grid-event fc-h-event fc-event fc-start fc-end">
                        <div class="fc-content">
                          <span class="fc-time">7p</span>
                          <span class="fc-title">City Hall Closed</span>
                        </div>
                      </a>
                    </td>
                    <td></td>
                  </tr>
                </table>
              </div>
            </div>
            """);

        var events = await LocalEventsScraperService.ExtractPerryEventsAsync(page);

        var single = events.Should().ContainSingle().Subject;
        single.Title.Should().Be("City Hall Closed");
        single.Url.Should().Be("https://www.perry-ga.gov/calendar-all-events");
        single.Source.Should().Be("Perry, GA City Calendar");
        single.StartAt.Should().Be(new DateTimeOffset(2026, 7, 21, 19, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ExtractPerryEventsAsync_ShouldSkipElements_WithoutAMatchingBgDateCell()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""
            <div class="fc-row">
              <div class="fc-content-skeleton">
                <table>
                  <tr>
                    <td class="fc-event-container">
                      <a class="fc-event">
                        <div class="fc-content"><span class="fc-title">Orphan Event</span></div>
                      </a>
                    </td>
                  </tr>
                </table>
              </div>
            </div>
            """);

        var events = await LocalEventsScraperService.ExtractPerryEventsAsync(page);

        events.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtractChamberEventsAsync_ShouldParseTitleUrlStartEndAndImage()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""
            <div class="card gz-events-card gz-has-logo">
                <div class="card-header">
                    <a href="https://chamber.robinsregion.com/events/details/8-30-am-leadership-robins-region-class-9079">
                        <span><img class="img-fluid gz-events-img" src="https://chambermaster.blob.core.windows.net/images/customers/2506/events/9079/200x200/LRR.png" alt="8:30 am - Leadership Robins Region Class"></span>
                    </a>
                </div>
                <div class="card-body gz-events-card-title">
                    <h5 class="card-title gz-card-title">
                        <a href="https://chamber.robinsregion.com/events/details/8-30-am-leadership-robins-region-class-9079">8:30 am - Leadership Robins Region Class</a>
                    </h5>
                    <ul class="list-group list-group-flush">
                        <li class="list-group-item gz-card-date">
                            <a href="https://chamber.robinsregion.com/events/details/8-30-am-leadership-robins-region-class-9079" class="card-link">
                                <i class="gz-fal gz-fa-calendar-alt"></i>
                                <span content="2026-07-21T08:30">Tuesday Jul 21, 2026</span>
                                <meta content="2026-07-21T17:00">
                            </a>
                        </li>
                    </ul>
                </div>
            </div>
            """);

        var events = await LocalEventsScraperService.ExtractChamberEventsAsync(page);

        var single = events.Should().ContainSingle().Subject;
        single.Title.Should().Be("8:30 am - Leadership Robins Region Class");
        single.Url.Should().Be("https://chamber.robinsregion.com/events/details/8-30-am-leadership-robins-region-class-9079");
        single.Source.Should().Be("Robins Region Chamber of Commerce");
        single.StartAt.Should().Be(new DateTimeOffset(2026, 7, 21, 8, 30, 0, TimeSpan.Zero));
        single.EndAt.Should().Be(new DateTimeOffset(2026, 7, 21, 17, 0, 0, TimeSpan.Zero));
        single.ImageUrl.Should().Contain("LRR.png");
    }

    [Fact]
    public async Task ExtractChamberEventsAsync_ShouldReturnEmpty_WhenNoCardsPresent()
    {
        await using var page = await fixture.Browser.NewPageAsync();
        await page.SetContentAsync("""<div class="gz-events-empty">Sorry, there are no events that meet the specified search criteria.</div>""");

        var events = await LocalEventsScraperService.ExtractChamberEventsAsync(page);

        events.Should().BeEmpty();
    }

    [Fact]
    public void DeduplicateEvents_ShouldMergeSameTitleAndDateAcrossSources_PreferringRicherEntry()
    {
        var perry = new CivicEvent(
            "City Hall Closed", "https://www.perry-ga.gov/calendar-all-events",
            new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), null, string.Empty,
            "Perry, GA City Calendar", null);
        var chamber = new CivicEvent(
            "CITY HALL CLOSED!", "https://chamber.robinsregion.com/events/details/x",
            new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero), null, string.Empty,
            "Robins Region Chamber of Commerce", "img.png");

        var result = LocalEventsScraperService.DeduplicateEvents([perry, chamber]);

        var single = result.Should().ContainSingle().Subject;
        single.Source.Should().Be("Robins Region Chamber of Commerce");
    }

    [Fact]
    public void DeduplicateEvents_ShouldKeepDistinctEvents_OnDifferentDatesOrTitles()
    {
        var first = new CivicEvent(
            "Farmers Market", "https://chamber.robinsregion.com/events/details/a",
            new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero), null, string.Empty,
            "Robins Region Chamber of Commerce", null);
        var second = new CivicEvent(
            "Farmers Market", "https://chamber.robinsregion.com/events/details/b",
            new DateTimeOffset(2026, 7, 28, 9, 0, 0, TimeSpan.Zero), null, string.Empty,
            "Robins Region Chamber of Commerce", null);

        var result = LocalEventsScraperService.DeduplicateEvents([first, second]);

        result.Should().HaveCount(2);
    }
}

// Shared across the whole "Playwright" test collection so Chromium is launched once,
// not once per test - a fresh IPage per test still gives full isolation.
public sealed class PlaywrightBrowserFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Idempotent - lets a bare `dotnet test` self-install the browser binary on a
        // fresh machine (mirrors the Docker build's --install-playwright-browsers hook),
        // though it won't install missing OS-level shared libraries the way
        // `--with-deps` does, so a fresh Linux CI runner still needs that step too.
        Microsoft.Playwright.Program.Main(["install", "chromium"]);
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }
}

[CollectionDefinition("Playwright")]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightBrowserFixture>;
