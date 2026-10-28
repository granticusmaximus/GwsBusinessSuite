using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Scheduling;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class BookingCalendarTests
{
    // A fixed Monday (UTC) so weekly availability is deterministic.
    private static readonly DateTimeOffset MondayMidnight = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Monday8Am = MondayMidnight.AddHours(8);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Parse_ShouldTurnTimedEventsIntoBusyTimes_SkippingFreeAndCancelledOnes()
    {
        var ics = Calendar(
            Event("busy", "DTSTART:20260818T140000Z", "DTEND:20260818T150000Z"),
            Event("free", "DTSTART:20260818T160000Z", "DTEND:20260818T170000Z", "TRANSP:TRANSPARENT"),
            Event("cancelled", "DTSTART:20260818T180000Z", "DTEND:20260818T190000Z", "STATUS:CANCELLED"));

        var result = BookingCalendarIcs.Parse(ics, Monday8Am, Monday8Am.AddDays(30), TimeZoneInfo.Utc, allDayEventsBlock: false);

        result.Busy.Should().ContainSingle().Which.Should().Be(new BookingCalendarIcs.BusyEntry(
            S: MondayMidnight.AddDays(1).AddHours(14), E: MondayMidnight.AddDays(1).AddHours(15)));
        result.Skipped.Should().Be(0);
    }

    [Fact]
    public void Parse_ShouldExpandRepeats_HonouringExceptionsAndMovedInstances()
    {
        // Weekly at 09:00 New York (13:00 UTC in August) from Tuesday 18-08: the 25th is
        // excluded, the 1st moved to Wednesday 15:00 UTC, the 8th as normal.
        var ics = Calendar(
            Event("standup", "DTSTART;TZID=America/New_York:20260818T090000", "DTEND;TZID=America/New_York:20260818T093000",
                "RRULE:FREQ=WEEKLY;COUNT=4", "EXDATE;TZID=America/New_York:20260825T090000"),
            Event("standup", "RECURRENCE-ID;TZID=America/New_York:20260901T090000", "DTSTART:20260902T150000Z", "DTEND:20260902T153000Z"));

        var result = BookingCalendarIcs.Parse(ics, Monday8Am, Monday8Am.AddDays(30), TimeZoneInfo.Utc, allDayEventsBlock: false);

        result.Busy.Select(entry => entry.S).Should().Equal(
            MondayMidnight.AddDays(1).AddHours(13),
            MondayMidnight.AddDays(16).AddHours(15),
            MondayMidnight.AddDays(22).AddHours(13));
    }

    [Fact]
    public void Parse_ShouldBlockAllDayEvents_OnlyWhenAsked_EvenThoseMarkedFree()
    {
        // Google marks every all-day event free (its holiday calendars too).
        var ics = Calendar(
            Event("trip", "DTSTART;VALUE=DATE:20260819", "DTEND;VALUE=DATE:20260820"),
            Event("holiday", "DTSTART;VALUE=DATE:20260820", "DTEND;VALUE=DATE:20260821", "TRANSP:TRANSPARENT"));

        BookingCalendarIcs.Parse(ics, Monday8Am, Monday8Am.AddDays(30), TimeZoneInfo.Utc, allDayEventsBlock: false).Busy.Should().BeEmpty();
        BookingCalendarIcs.Parse(ics, Monday8Am, Monday8Am.AddDays(30), TimeZoneInfo.Utc, allDayEventsBlock: true).Busy.Should().ContainSingle()
            .Which.Should().Be(new BookingCalendarIcs.BusyEntry(D: new DateOnly(2026, 8, 19), U: new DateOnly(2026, 8, 21)));
    }

    [Fact]
    public void Parse_ShouldReadTimesWithNoZone_AsTheOwnersWallClock()
    {
        var ics = Calendar(Event("floating", "DTSTART:20260818T090000", "DTEND:20260818T100000"));

        var result = BookingCalendarIcs.Parse(ics, Monday8Am, Monday8Am.AddDays(30), NewYork, allDayEventsBlock: false);

        result.Busy.Should().ContainSingle().Which.S.Should().Be(MondayMidnight.AddDays(1).AddHours(13));
    }

    [Fact]
    public void Parse_ShouldSkipAnEventWhoseRuleNeverMatches_AndKeepTheRest()
    {
        var ics = Calendar(
            Event("never", "DTSTART:20260818T100000Z", "DTEND:20260818T110000Z", "RRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30"),
            Event("real", "DTSTART:20260819T100000Z", "DTEND:20260819T110000Z"));

        var result = BookingCalendarIcs.Parse(ics, Monday8Am, Monday8Am.AddDays(30), TimeZoneInfo.Utc, allDayEventsBlock: false);

        result.Skipped.Should().Be(1);
        result.Busy.Should().ContainSingle().Which.S.Should().Be(MondayMidnight.AddDays(2).AddHours(10));
    }

    [Fact]
    public void Parse_ShouldRefuseAPageThatIsntACalendar()
    {
        var act = () => BookingCalendarIcs.Parse("<html><body>Sign in</body></html>", Monday8Am, Monday8Am.AddDays(30), TimeZoneInfo.Utc, false);

        act.Should().Throw<FormatException>().WithMessage("*iCal*");
    }

    [Fact]
    public async Task AddFeedAsync_ShouldReadTheCalendar_AndKeepTheAddressEncrypted()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Respond(HttpStatusCode.OK, Calendar(Event("busy", "DTSTART:20260818T140000Z", "DTEND:20260818T150000Z")));

        var view = await fixture.Calendar.AddFeedAsync(" Work ", "webcal://calendar.example.test/private/abc/basic.ics", " grant ", false, "admin");

        fixture.Requests.Should().ContainSingle().Which.Should().Be("https://calendar.example.test/private/abc/basic.ics");
        view.Should().Match<BookingCalendarFeedView>(feed =>
            feed.Name == "Work" && feed.UrlHint == "calendar.example.test" && feed.OwnerUsername == "grant"
            && feed.BusyCount == 1 && feed.LastError == null && feed.LastSyncedAt == Monday8Am);
        var stored = await fixture.Db.BookingCalendarFeeds.SingleAsync();
        stored.UrlProtected.Should().NotContain("calendar.example.test", "the address is a credential");
    }

    [Fact]
    public async Task AddFeedAsync_ShouldRefuseAnAddressThatIsntACalendar_WithoutSavingIt()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Invoking(f => f.Calendar.AddFeedAsync("Work", "http://calendar.example.test/a.ics", null, false, "admin"))
            .Should().ThrowAsync<ArgumentException>("the address is a credential and must not travel in the clear");

        fixture.Respond(HttpStatusCode.NotFound, "gone");
        await fixture.Invoking(f => f.Calendar.AddFeedAsync("Work", "https://calendar.example.test/a.ics", null, false, "admin"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Couldn't read that calendar: the calendar answered 404*");

        fixture.Respond(HttpStatusCode.OK, "<html>Sign in</html>");
        await fixture.Invoking(f => f.Calendar.AddFeedAsync("Work", "https://calendar.example.test/a.ics", null, false, "admin"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*iCal (.ics) address*");

        (await fixture.Db.BookingCalendarFeeds.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SyncFeedAsync_ShouldKeepTheLastBusyTimes_WhenTheCalendarCantBeRead()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Respond(HttpStatusCode.OK, Calendar(Event("busy", "DTSTART:20260818T140000Z", "DTEND:20260818T150000Z")));
        var feed = await fixture.Calendar.AddFeedAsync("Work", "https://calendar.example.test/a.ics", null, false, "admin");

        fixture.Respond(HttpStatusCode.InternalServerError, "down");
        var synced = await fixture.Calendar.SyncFeedAsync(feed.Id);

        synced!.LastError.Should().StartWith("the calendar answered 500");
        synced.LastSyncedAt.Should().Be(Monday8Am);
        (await fixture.Calendar.GetBusyAsync(null, TimeZoneInfo.Utc, MondayMidnight, MondayMidnight.AddDays(7)))
            .Should().ContainSingle("the last good busy times still apply");
        (await fixture.Calendar.SyncAllAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Slots_ShouldSkipCalendarBusyTimes_AndBookingOneIsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.CreateTypeAsync("Consult");
        fixture.AddFeed(null, new BookingCalendarIcs.BusyEntry(S: MondayMidnight.AddHours(10), E: MondayMidnight.AddHours(11)));

        var slots = await fixture.Bookings.GetAvailableSlotsAsync(type.Id, Monday8Am, 1);

        slots.Select(slot => slot.StartsAt.Hour).Should().Equal(9, 11);
        (await fixture.Bookings.CreateBookingAsync(type.Id, MondayMidnight.AddHours(10), "Ada", "ada@example.test", "")).Should().BeNull();
    }

    [Fact]
    public async Task Slots_ShouldApplyAnOwnersCalendar_OnlyToTheirBookingTypes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var grants = await fixture.CreateTypeAsync("Grant's call", owner: "grant");
        var sams = await fixture.CreateTypeAsync("Sam's call", owner: "Sam");
        fixture.AddFeed("sam", new BookingCalendarIcs.BusyEntry(S: MondayMidnight.AddHours(9), E: MondayMidnight.AddHours(10)));

        (await fixture.Bookings.GetAvailableSlotsAsync(grants.Id, Monday8Am, 1)).Select(slot => slot.StartsAt.Hour).Should().Equal(9, 10, 11);
        (await fixture.Bookings.GetAvailableSlotsAsync(sams.Id, Monday8Am, 1)).Select(slot => slot.StartsAt.Hour).Should().Equal(10, 11);
    }

    [Fact]
    public async Task Slots_ShouldTreatTheSameOwnersOtherBookingsAsBusy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var intro = await fixture.CreateTypeAsync("Intro");
        var consult = await fixture.CreateTypeAsync("Consult");
        var sams = await fixture.CreateTypeAsync("Sam's call", owner: "sam");

        (await fixture.Bookings.CreateBookingAsync(intro.Id, MondayMidnight.AddHours(9), "Ada", "ada@example.test", "")).Should().NotBeNull();

        (await fixture.Bookings.GetAvailableSlotsAsync(consult.Id, Monday8Am, 1)).Select(slot => slot.StartsAt.Hour)
            .Should().Equal([10, 11], "one person can't take two meetings at once");
        (await fixture.Bookings.GetAvailableSlotsAsync(sams.Id, Monday8Am, 1)).Select(slot => slot.StartsAt.Hour).Should().Equal(9, 10, 11);
    }

    [Fact]
    public async Task GetBusyAsync_ShouldReadAllDayEvents_AsWholeDaysInTheGivenZone()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.AddFeed(null, new BookingCalendarIcs.BusyEntry(D: new DateOnly(2026, 8, 18), U: new DateOnly(2026, 8, 19)));

        var busy = await fixture.Calendar.GetBusyAsync("anyone", NewYork, MondayMidnight, MondayMidnight.AddDays(7));

        busy.Should().ContainSingle().Which.Should().Be(new BookingBusyTime(
            MondayMidnight.AddDays(1).AddHours(4), MondayMidnight.AddDays(2).AddHours(4)));
    }

    [Fact]
    public async Task Export_ShouldListConfirmedBookings_OnlyThroughTheCurrentLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.CreateTypeAsync("Consult");
        await fixture.Bookings.CreateBookingAsync(type.Id, MondayMidnight.AddHours(9), "Ada Lovelace", "ada@example.test", "Bring the figures");
        var cancelled = await fixture.Bookings.CreateBookingAsync(type.Id, MondayMidnight.AddHours(10), "Bob", "bob@example.test", "");
        await fixture.Bookings.CancelBookingAsync(cancelled!.Id, "admin");

        (await fixture.Calendar.GetExportAsync()).Url.Should().BeNull();
        var first = Token((await fixture.Calendar.CreateExportLinkAsync("admin")).Url);
        var ics = await fixture.Calendar.RenderExportAsync(first);

        ics.Should().Contain("BEGIN:VCALENDAR").And.Contain("SUMMARY:Consult: Ada Lovelace").And.Contain("DTSTART:20260817T090000Z")
            .And.NotContain("Bob");
        var second = Token((await fixture.Calendar.CreateExportLinkAsync("admin")).Url);
        (await fixture.Calendar.RenderExportAsync(first)).Should().BeNull("a new link replaces the old one");
        (await fixture.Calendar.GetExportAsync()).Url.Should().Be($"/bookings/calendar/{second}.ics");

        await fixture.Calendar.DisableExportAsync("admin");
        (await fixture.Calendar.RenderExportAsync(second)).Should().BeNull();
        (await fixture.Calendar.GetExportAsync()).Url.Should().BeNull();
    }

    private static string Token(string? url) => url!["/bookings/calendar/".Length..^".ics".Length];

    private static string Calendar(params string[] events) =>
        string.Join("\r\n", ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//EN", .. events, "END:VCALENDAR", ""]);

    private static string Event(string uid, params string[] lines) =>
        string.Join("\r\n", ["BEGIN:VEVENT", $"UID:{uid}", "DTSTAMP:20260801T000000Z", .. lines, "END:VEVENT"]);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly StubHandler _handler = new();

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            var clock = new FixedTimeProvider(Monday8Am);
            Calendar = new BookingCalendarService(db, new StubHttpClientFactory(_handler), new ReversibleProtector(), clock,
                NullLogger<BookingCalendarService>.Instance);
            Bookings = new BookingService(db, new NoEmail(), clock, calendar: Calendar);
        }

        public ApplicationDbContext Db { get; }
        public BookingCalendarService Calendar { get; }
        public BookingService Bookings { get; }
        public List<string> Requests => _handler.Requests;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public void Respond(HttpStatusCode status, string body) => _handler.Response = (status, body);

        // Monday 09:00-12:00, hour-long slots.
        public Task<BookingTypeView> CreateTypeAsync(string title, string? owner = null) =>
            Bookings.SaveBookingTypeAsync(new BookingTypeEditorModel
            {
                Title = title,
                DurationMinutes = 60,
                OwnerUsername = owner,
                Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0))]
            }, "admin");

        public void AddFeed(string? owner, params BookingCalendarIcs.BusyEntry[] busy)
        {
            Db.BookingCalendarFeeds.Add(new BookingCalendarFeed
            {
                Name = "Work",
                UrlProtected = "x",
                OwnerUsername = owner,
                BusyJson = BookingCalendarIcs.ToJson(busy)
            });
            Db.SaveChanges();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public (HttpStatusCode Status, string Body) Response { get; set; } = (HttpStatusCode.OK, string.Empty);
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(Response.Status) { Content = new StringContent(Response.Body) });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ReversibleProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }

    private sealed class NoEmail : IBookingEmailSender
    {
        public Task SendConfirmationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, string manageUrl,
            CancellationToken cancellationToken = default, string? timeZoneId = null) => Task.CompletedTask;

        public Task SendCancellationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc,
            CancellationToken cancellationToken = default, string? timeZoneId = null) => Task.CompletedTask;
    }

    // Pinned to UTC, like BookingServiceTests: a type with no zone uses the server's.
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
