using System.Globalization;

namespace GwsBusinessSuite.Application.Abstractions;

// The one way dates and times are shown to people anywhere in GWS (admin, public site, emails,
// exports): day-month-two-digit-year with dashes, and a 24-hour clock - e.g. "09-10-26 15:05".
// Grant's rule (2026-10-09). Use these instead of writing display format strings.
//
// Only for display. Values a machine reads stay in their own formats: <input type="date">
// (yyyy-MM-dd), datetime-local, ISO 8601 in JSON/APIs/URLs/storage, file names.
public static class GwsDateFormat
{
    public const string DatePattern = "dd-MM-yy";
    public const string TimePattern = "HH:mm";
    public const string DateTimePattern = DatePattern + " " + TimePattern;

    public static string ToGwsDate(this DateTimeOffset value) => value.ToString(DatePattern, CultureInfo.InvariantCulture);
    public static string ToGwsDate(this DateTime value) => value.ToString(DatePattern, CultureInfo.InvariantCulture);
    public static string ToGwsDate(this DateOnly value) => value.ToString(DatePattern, CultureInfo.InvariantCulture);

    public static string ToGwsTime(this DateTimeOffset value) => value.ToString(TimePattern, CultureInfo.InvariantCulture);
    public static string ToGwsTime(this DateTime value) => value.ToString(TimePattern, CultureInfo.InvariantCulture);
    public static string ToGwsTime(this TimeOnly value) => value.ToString(TimePattern, CultureInfo.InvariantCulture);

    public static string ToGwsDateTime(this DateTimeOffset value) => value.ToString(DateTimePattern, CultureInfo.InvariantCulture);
    public static string ToGwsDateTime(this DateTime value) => value.ToString(DateTimePattern, CultureInfo.InvariantCulture);

    // Shown in the person's (the server's) local time zone, which is how GWS shows times.
    public static string ToLocalGwsDate(this DateTimeOffset value) => value.ToLocalTime().ToGwsDate();
    public static string ToLocalGwsTime(this DateTimeOffset value) => value.ToLocalTime().ToGwsTime();
    public static string ToLocalGwsDateTime(this DateTimeOffset value) => value.ToLocalTime().ToGwsDateTime();
}
