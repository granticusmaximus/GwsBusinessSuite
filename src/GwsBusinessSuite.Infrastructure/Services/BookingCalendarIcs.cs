using System.Text.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Domain.Entities;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;
using Ical.Net.Serialization;

namespace GwsBusinessSuite.Infrastructure.Services;

// iCal in and out for booking calendar sync. Reading: timed events become UTC ranges; all-day
// events stay dates, because a day only means something in the booking type's zone (applied
// when slots are worked out). Cancelled events and timed events marked free don't count.
public static class BookingCalendarIcs
{
    // A rule that never matches (every 30 February) gives up after this many steps instead of
    // looping forever.
    private const int MaxUnmatchedIncrements = 1000;
    private const int MaxOccurrences = 20_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Stored shape: S and E for a timed range (UTC); D and U for all-day dates (U exclusive).
    public sealed record BusyEntry(DateTimeOffset? S = null, DateTimeOffset? E = null, DateOnly? D = null, DateOnly? U = null);

    public sealed record ParseResult(IReadOnlyList<BusyEntry> Busy, int Skipped);

    public static ParseResult Parse(string ics, DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeZoneInfo floatingZone, bool allDayEventsBlock)
    {
        if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("that address didn't return a calendar - check it's the iCal (.ics) address.");
        var calendar = Calendar.Load(ics) ?? throw new FormatException("that isn't a calendar in iCal format.");

        // Evaluated together so a moved instance replaces its original. When one event's rule
        // can't be worked out, find it, leave it out, and go again.
        var skipped = 0;
        List<Occurrence> occurrences;
        while (true)
        {
            try
            {
                occurrences = Occurrences(calendar.GetOccurrences<CalendarEvent>(Utc(fromUtc.AddDays(-1)), Options()), toUtc);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var bad = calendar.Events.FirstOrDefault(item => !Evaluates(item, fromUtc, toUtc));
                if (bad is null) throw;
                calendar.Events.Remove(bad);
                skipped++;
            }
        }

        var timed = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var days = new List<(DateOnly First, DateOnly After)>();
        var firstDay = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(toUtc.UtcDateTime).AddDays(1);
        foreach (var occurrence in occurrences)
        {
            if (occurrence.Source is not CalendarEvent item || string.Equals(item.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var start = occurrence.Period.StartTime;
            var end = occurrence.Period.EffectiveEndTime ?? start;
            // Google marks every all-day event free, so asking for all-day events to block
            // overrides that; a timed event marked free never blocks.
            if (start.HasTime && string.Equals(item.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase)) continue;
            if (!start.HasTime)
            {
                if (!allDayEventsBlock) continue;
                var after = end.HasTime ? end.Date.AddDays(1) : end.Date;
                if (after <= start.Date) after = start.Date.AddDays(1);
                if (after > firstDay && start.Date < lastDay) days.Add((start.Date, after));
                continue;
            }

            var startUtc = ToUtc(start, floatingZone);
            var endUtc = ToUtc(end, floatingZone);
            if (endUtc > startUtc && endUtc > fromUtc && startUtc < toUtc) timed.Add((startUtc, endUtc));
        }

        var busy = new List<BusyEntry>();
        foreach (var (start, end) in Merge(timed, (a, b) => a.CompareTo(b))) busy.Add(new BusyEntry(S: start, E: end));
        foreach (var (first, after) in Merge(days, (a, b) => a.CompareTo(b))) busy.Add(new BusyEntry(D: first, U: after));
        return new ParseResult(busy, skipped);
    }

    public static string ToJson(IReadOnlyList<BusyEntry> busy) => JsonSerializer.Serialize(busy, JsonOptions);

    public static IReadOnlyList<BusyEntry> FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<BusyEntry>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // The confirmed bookings as a calendar to subscribe to. Each booking keeps its id as the UID,
    // so a moved or cancelled booking updates or leaves the subscriber's calendar.
    public static string RenderBookings(IEnumerable<(Booking Booking, string TypeTitle)> bookings)
    {
        var calendar = new Calendar { ProductId = "-//GWS Business Suite//Bookings//EN" };
        calendar.AddProperty("X-WR-CALNAME", "Bookings");
        foreach (var (booking, typeTitle) in bookings)
        {
            var notes = string.IsNullOrWhiteSpace(booking.Notes) ? string.Empty : $"\n\n{booking.Notes}";
            calendar.Events.Add(new CalendarEvent
            {
                Uid = $"{booking.Id:N}@gws-bookings",
                Summary = $"{typeTitle}: {booking.AttendeeName}",
                Description = $"{booking.AttendeeName} <{booking.AttendeeEmail}>{notes}",
                DtStart = new CalDateTime(booking.StartsAt.UtcDateTime),
                DtEnd = new CalDateTime(booking.EndsAt.UtcDateTime),
                DtStamp = new CalDateTime((booking.UpdatedAt ?? booking.CreatedAt).UtcDateTime),
                Status = "CONFIRMED"
            });
        }

        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    private static EvaluationOptions Options() => new() { MaxUnmatchedIncrementsLimit = MaxUnmatchedIncrements };

    private static List<Occurrence> Occurrences(IEnumerable<Occurrence> all, DateTimeOffset toUtc) =>
        all.TakeWhileBefore(Utc(toUtc)).Take(MaxOccurrences).ToList();

    private static bool Evaluates(CalendarEvent item, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        try
        {
            Occurrences(item.GetOccurrences(Utc(fromUtc.AddDays(-1)), Options()), toUtc);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static CalDateTime Utc(DateTimeOffset value) => new(value.UtcDateTime);

    // A time with no zone ("floating") means wall-clock time where the calendar's owner is.
    private static DateTimeOffset ToUtc(CalDateTime value, TimeZoneInfo floatingZone)
    {
        if (!value.IsFloating) return new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, floatingZone.GetUtcOffset(local)).ToUniversalTime();
    }

    // Overlapping or touching ranges become one, which keeps the stored list short.
    private static List<(T Start, T End)> Merge<T>(List<(T Start, T End)> ranges, Comparison<T> compare)
    {
        ranges.Sort((a, b) => compare(a.Start, b.Start));
        var merged = new List<(T Start, T End)>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && compare(range.Start, merged[^1].End) <= 0)
            {
                if (compare(range.End, merged[^1].End) > 0) merged[^1] = (merged[^1].Start, range.End);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }
}
