using FluentAssertions;
using GwsBusinessSuite.Application.Scheduling;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class BookingServiceTests
{
    // A fixed Monday so weekly-availability slot generation is deterministic.
    private static readonly DateTimeOffset FixedMonday8Am = new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MondayMidnight = new(2026, 8, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveBookingTypeAsync_ShouldAutoGenerateAUniqueSlugFromTheTitle()
    {
        await using var fixture = await Fixture.CreateAsync();

        var first = await fixture.Service.SaveBookingTypeAsync(
            new BookingTypeEditorModel { Title = "Intro Call" }, "owner");
        var second = await fixture.Service.SaveBookingTypeAsync(
            new BookingTypeEditorModel { Title = "Intro Call" }, "owner");

        first.Slug.Should().Be("intro-call");
        second.Slug.Should().Be("intro-call-2");
    }

    [Fact]
    public async Task GetAvailableSlotsAsync_ShouldGenerateSlotsWithinTheWeeklyWindow_RespectingDurationAndBuffer()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            BufferMinutes = 15,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 30))]
        }, "owner");

        var slots = await fixture.Service.GetAvailableSlotsAsync(type.Id, FixedMonday8Am, 1);

        // 09:00-09:30, then +45min step (30 duration + 15 buffer) -> 09:45-10:15. A third slot
        // would start at 10:30, which doesn't leave room for a 30-minute slot before 10:30 end.
        slots.Should().HaveCount(2);
        slots[0].StartsAt.Should().Be(MondayMidnight.AddHours(9));
        slots[1].StartsAt.Should().Be(MondayMidnight.AddHours(9).AddMinutes(45));
    }

    [Fact]
    public async Task GetAvailableSlotsAsync_ShouldExcludeSlotsLessThanOneHourFromNow()
    {
        await using var fixture = await Fixture.CreateAsync();
        // "Now" is 08:00 Monday; a window starting at 08:30 has slots inside the 1-hour lead
        // time and must be excluded, but 09:00 onward is fine.
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(8, 30), new TimeOnly(9, 30))]
        }, "owner");

        var slots = await fixture.Service.GetAvailableSlotsAsync(type.Id, FixedMonday8Am, 1);

        slots.Should().ContainSingle();
        slots[0].StartsAt.Should().Be(MondayMidnight.AddHours(9));
    }

    [Fact]
    public async Task CreateBookingAsync_ShouldSucceed_AndSendAConfirmationEmail_AndCreateAContact()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0))]
        }, "owner");
        var slotStart = MondayMidnight.AddHours(9);

        var booking = await fixture.Service.CreateBookingAsync(type.Id, slotStart, "Jamie Rivera", "jamie@example.test", "Looking forward to it");

        booking.Should().NotBeNull();
        booking!.Status.Should().Be(BookingStatuses.Confirmed);
        fixture.EmailSender.Confirmations.Should().ContainSingle();
        (await fixture.Db.Contacts.SingleAsync()).Email.Should().Be("jamie@example.test");

        // The stored hash must never equal the raw token embedded in the manage URL.
        var manageUrl = fixture.EmailSender.Confirmations[0].ManageUrl;
        var token = manageUrl.Split('/').Last();
        var stored = await fixture.Db.Bookings.SingleAsync();
        stored.ManageTokenHash.Should().NotBe(token);
    }

    [Fact]
    public async Task CreateBookingAsync_ShouldSetContactIdOnTheView()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0))]
        }, "owner");
        var slotStart = MondayMidnight.AddHours(9);

        var booking = await fixture.Service.CreateBookingAsync(type.Id, slotStart, "Jamie Rivera", "jamie@example.test", "");

        var contact = await fixture.Db.Contacts.SingleAsync();
        booking!.ContactId.Should().Be(contact.Id);
    }

    [Fact]
    public async Task ListBookingsForContactAsync_ShouldReturnOnlyThatContactsBookings()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(11, 0))]
        }, "owner");
        var jamie = await fixture.Service.CreateBookingAsync(type.Id, MondayMidnight.AddHours(9), "Jamie Rivera", "jamie@example.test", "");
        var alex = await fixture.Service.CreateBookingAsync(type.Id, MondayMidnight.AddHours(10), "Alex Chen", "alex@example.test", "");

        var jamieBookings = await fixture.Service.ListBookingsForContactAsync(jamie!.ContactId!.Value);

        jamieBookings.Should().ContainSingle(b => b.Id == jamie.Id);
        jamieBookings.Should().NotContain(b => b.Id == alex!.Id);
    }

    [Fact]
    public async Task CreateBookingAsync_ShouldReturnNull_WhenTheSlotIsAlreadyTaken()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0))]
        }, "owner");
        var slotStart = MondayMidnight.AddHours(9);
        (await fixture.Service.CreateBookingAsync(type.Id, slotStart, "Jamie Rivera", "jamie@example.test", "")).Should().NotBeNull();

        var second = await fixture.Service.CreateBookingAsync(type.Id, slotStart, "Alex Chen", "alex@example.test", "");

        second.Should().BeNull();
    }

    [Fact]
    public async Task CancelBookingByManageTokenAsync_ShouldCancelExactlyOnce_AndSendACancellationEmail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var type = await fixture.Service.SaveBookingTypeAsync(new BookingTypeEditorModel
        {
            Title = "Intro Call",
            DurationMinutes = 30,
            Availability = [new BookingAvailabilityWindow(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(10, 0))]
        }, "owner");
        var slotStart = MondayMidnight.AddHours(9);
        await fixture.Service.CreateBookingAsync(type.Id, slotStart, "Jamie Rivera", "jamie@example.test", "");
        var manageUrl = fixture.EmailSender.Confirmations[0].ManageUrl;
        var token = manageUrl.Split('/').Last();

        (await fixture.Service.CancelBookingByManageTokenAsync(token)).Should().BeTrue();
        fixture.EmailSender.Cancellations.Should().ContainSingle();

        (await fixture.Service.CancelBookingByManageTokenAsync(token)).Should().BeFalse();
        fixture.EmailSender.Cancellations.Should().ContainSingle("cancelling an already-cancelled booking must be a no-op");

        // The slot should be free again once cancelled.
        var reopened = await fixture.Service.CreateBookingAsync(type.Id, slotStart, "Alex Chen", "alex@example.test", "");
        reopened.Should().NotBeNull();
    }

    [Fact]
    public async Task GetBookingByManageTokenAsync_ShouldReturnNull_ForAnUnknownToken()
    {
        await using var fixture = await Fixture.CreateAsync();

        (await fixture.Service.GetBookingByManageTokenAsync("not-a-real-token")).Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, FakeBookingEmailSender emailSender)
        {
            _connection = connection;
            Db = db;
            EmailSender = emailSender;
            Service = new BookingService(db, emailSender, new FixedTimeProvider(FixedMonday8Am));
        }

        public ApplicationDbContext Db { get; }
        public FakeBookingEmailSender EmailSender { get; }
        public BookingService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db, new FakeBookingEmailSender());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeBookingEmailSender : IBookingEmailSender
    {
        public List<(string Email, string ManageUrl)> Confirmations { get; } = [];
        public List<string> Cancellations { get; } = [];

        public Task SendConfirmationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, string manageUrl, CancellationToken cancellationToken = default)
        {
            Confirmations.Add((attendeeEmail, manageUrl));
            return Task.CompletedTask;
        }

        public Task SendCancellationAsync(string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, CancellationToken cancellationToken = default)
        {
            Cancellations.Add(attendeeEmail);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
