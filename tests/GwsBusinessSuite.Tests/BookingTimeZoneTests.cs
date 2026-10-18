using FluentAssertions;
using GwsBusinessSuite.Application.Scheduling;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Booking types read their availability in their own time zone, with per-type booking rules,
// and attendees get times in their own zone.
public sealed class BookingTimeZoneTests
{
    // Monday 17-08-26 08:00 UTC (04:00 in New York, which is on daylight time, UTC-4).
    private static readonly DateTimeOffset Monday8AmUtc = new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    private static BookingAvailabilityWindow Monday(int startHour, int endHour) =>
        new(DayOfWeek.Monday, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0));

    [Fact]
    public async Task Slots_ShouldBeReadInTheTypesZone()
    {
        await using var f = await Fixture.CreateAsync(Monday8AmUtc);
        var type = await f.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Call", DurationMinutes = 30, TimeZoneId = "America/New_York", Availability = [Monday(9, 10)]
        }, "owner");

        var slots = await f.Service.GetAvailableSlotsAsync(type.Id, Monday8AmUtc, 1);

        type.TimeZoneId.Should().Be("America/New_York");
        slots.Select(slot => slot.StartsAt).Should().Equal(
            new DateTimeOffset(2026, 8, 17, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 17, 13, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Slots_ShouldSkipTheHourThatDoesNotExistWhenClocksSpringForward()
    {
        // Sunday 08-03-26: New York clocks jump from 02:00 to 03:00.
        var saturday = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        await using var f = await Fixture.CreateAsync(saturday);
        var type = await f.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Early", DurationMinutes = 30, TimeZoneId = "America/New_York",
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Sunday, new TimeOnly(1, 0), new TimeOnly(4, 0))]
        }, "owner");

        var slots = await f.Service.GetAvailableSlotsAsync(type.Id, saturday, 3);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        slots.Select(slot => TimeZoneInfo.ConvertTime(slot.StartsAt, zone).ToString("HH:mm"))
            .Should().Equal("01:00", "01:30", "03:00", "03:30");
    }

    [Fact]
    public async Task Rules_ShouldLimitHowFarAheadHowSoonAndHowManyPerDay()
    {
        await using var f = await Fixture.CreateAsync(Monday8AmUtc);
        var type = await f.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Call", DurationMinutes = 60, TimeZoneId = "UTC",
            DaysAhead = 8, MinNoticeMinutes = 120, MaxPerDay = 2,
            Availability = [Monday(9, 13)]
        }, "owner");

        var slots = await f.Service.GetAvailableSlotsAsync(type.Id, Monday8AmUtc, 60);

        // 2 h notice from 08:00 removes 09:00; 8 days ahead reaches next Monday too.
        slots.Select(slot => slot.StartsAt.ToString("dd HH:mm")).Should().Equal(
            "17 10:00", "17 11:00", "17 12:00", "24 09:00", "24 10:00", "24 11:00", "24 12:00");

        (await f.Service.CreateBookingAsync(type.Id, slots[0].StartsAt, "A", "a@example.test", "")).Should().NotBeNull();
        (await f.Service.CreateBookingAsync(type.Id, slots[2].StartsAt, "B", "b@example.test", "")).Should().NotBeNull();

        // Two booked on the 17th: that day is full, and a third booking there is refused.
        var after = await f.Service.GetAvailableSlotsAsync(type.Id, Monday8AmUtc, 60);
        after.Should().OnlyContain(slot => slot.StartsAt.Day == 24);
        (await f.Service.CreateBookingAsync(type.Id, slots[1].StartsAt, "C", "c@example.test", "")).Should().BeNull();
    }

    [Fact]
    public async Task Booking_ShouldBeRefused_ForATimeThePageWouldNotOffer()
    {
        await using var f = await Fixture.CreateAsync(Monday8AmUtc);
        var type = await f.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Call", DurationMinutes = 30, TimeZoneId = "UTC", DaysAhead = 7, Availability = [Monday(9, 11)]
        }, "owner");
        var monday9 = new DateTimeOffset(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

        (await f.Service.CreateBookingAsync(type.Id, monday9.AddMinutes(15), "A", "a@example.test", "")).Should().BeNull("not on the slot grid");
        (await f.Service.CreateBookingAsync(type.Id, monday9.AddHours(5), "A", "a@example.test", "")).Should().BeNull("outside availability");
        (await f.Service.CreateBookingAsync(type.Id, monday9.AddDays(7), "A", "a@example.test", "")).Should().BeNull("past the 7-day window");
        (await f.Service.CreateBookingAsync(type.Id, monday9, "A", "a@example.test", "")).Should().NotBeNull();
    }

    [Fact]
    public async Task Attendee_ShouldGetTimesInTheirOwnZone()
    {
        await using var f = await Fixture.CreateAsync(Monday8AmUtc);
        var type = await f.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Call", DurationMinutes = 30, TimeZoneId = "America/New_York", Availability = [Monday(9, 11)]
        }, "owner");
        var slots = await f.Service.GetAvailableSlotsAsync(type.Id, Monday8AmUtc, 1);

        var chicago = await f.Service.CreateBookingAsync(type.Id, slots[0].StartsAt, "A", "a@example.test", "", attendeeTimeZone: "America/Chicago");
        var unknown = await f.Service.CreateBookingAsync(type.Id, slots[1].StartsAt, "B", "b@example.test", "", attendeeTimeZone: "Mars/Olympus_Mons");

        chicago!.AttendeeTimeZone.Should().Be("America/Chicago");
        unknown!.AttendeeTimeZone.Should().Be("America/New_York", "an unknown zone falls back to the type's");
        f.Email.Zones.Should().Equal("America/Chicago", "America/New_York");
        BookingEmailSender.When(slots[0].StartsAt, "America/Chicago").Should().Be("17-08-26 08:00 (America/Chicago)");
        BookingEmailSender.When(slots[0].StartsAt, null).Should().Be("17-08-26 13:00 (UTC)");
    }

    [Fact]
    public async Task Save_ShouldRejectAnUnknownZone_AndBlankMeansTheServerZone()
    {
        await using var f = await Fixture.CreateAsync(Monday8AmUtc);
        await f.Service.Invoking(service => service.SaveBookingTypeAsync(
            new BookingTypeEditorModel { Title = "Call", TimeZoneId = "Nowhere/Land" }, "owner"))
            .Should().ThrowAsync<ArgumentException>();

        var blank = await f.Service.SaveBookingTypeAsync(new BookingTypeEditorModel { Title = "Call" }, "owner");
        blank.TimeZoneId.Should().Be("Europe/London", "the server zone, from the time provider");
        blank.DaysAhead.Should().Be(14);
        blank.MinNoticeMinutes.Should().Be(60);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ApplicationDbContext _db;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, DateTimeOffset now)
        {
            _connection = connection;
            _db = db;
            Service = new BookingService(db, Email, new Clock(now));
        }

        public RecordingEmail Email { get; } = new();
        public BookingService Service { get; }

        public static async Task<Fixture> CreateAsync(DateTimeOffset now)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, now);
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingEmail : IBookingEmailSender
    {
        public List<string?> Zones { get; } = [];

        public Task SendConfirmationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, string manageUrl, CancellationToken cancellationToken = default, string? timeZoneId = null)
        {
            Zones.Add(timeZoneId);
            return Task.CompletedTask;
        }

        public Task SendCancellationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, CancellationToken cancellationToken = default, string? timeZoneId = null) =>
            Task.CompletedTask;
    }

    // A server zone that isn't UTC, so "blank means the server zone" is actually tested.
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    }
}
