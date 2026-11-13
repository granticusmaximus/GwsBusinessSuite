using System.Globalization;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Daily notes, as in Obsidian/Logseq/Tana: one page per day under a "Daily notes" folder page.
// Each day's page is found by its SystemKey ("daily:2026-10-09"), not its title, so renaming
// the page never makes "Today" create a second one.
public static class DailyNotes
{
    public const string FolderSystemKey = "daily-notes";
    private const string DayKeyPrefix = "daily:";
    private const string DateFormat = "yyyy-MM-dd";

    public static string SystemKeyFor(DateOnly date) => DayKeyPrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture);

    // Each person's day page carries their name after the date (private Sentinel, 12-11-26):
    // "daily:2026-10-09:ann". The date is what's read here.
    public static DateOnly? DateFromSystemKey(string? systemKey) =>
        systemKey is not null && systemKey.StartsWith(DayKeyPrefix, StringComparison.Ordinal)
        && DateOnly.TryParseExact(DatePart(systemKey[DayKeyPrefix.Length..]), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string DatePart(string rest) => rest.IndexOf(':') is var colon and >= 0 ? rest[..colon] : rest;

    // "2026-10-09 Thursday": sorts by date and still reads naturally.
    public static string TitleFor(DateOnly date) =>
        date.ToString(DateFormat, CultureInfo.InvariantCulture) + " " + date.ToString("dddd", CultureInfo.InvariantCulture);
}

public interface IDailyNoteService
{
    // Opens the day's note, creating it (from the daily-note template, if one is chosen) the
    // first time.
    Task<WikiPage> OpenOrCreateAsync(DateOnly date, string performedBy, CancellationToken cancellationToken = default);

    // Adds a titled, timestamped section to the end of the day's note (creating it if needed).
    Task<WikiPage> AppendAsync(DateOnly date, string title, string markdownBody, string performedBy, CancellationToken cancellationToken = default);
}
