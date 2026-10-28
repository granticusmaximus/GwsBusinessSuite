using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Scheduling;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class BookingCalendarService(
    IAppDbContext db,
    IHttpClientFactory httpClients,
    ISecretProtector secrets,
    TimeProvider timeProvider,
    ILogger<BookingCalendarService> logger) : IBookingCalendarService
{
    // Every connection is checked to be a public address (see DependencyInjection.cs).
    public const string HttpClientName = "booking-calendar";
    public const string ExportPath = "/bookings/calendar/";
    private const int MaxFeedBytes = 5 * 1024 * 1024;
    // Booked meetings this far back stay in the exported calendar.
    private const int ExportDaysBack = 60;

    public async Task<IReadOnlyList<BookingCalendarFeedView>> ListFeedsAsync(CancellationToken cancellationToken = default) =>
        (await db.BookingCalendarFeeds.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(feed => feed.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToView)
            .ToList();

    public async Task<BookingCalendarFeedView> AddFeedAsync(string name, string address, string? ownerUsername, bool allDayEventsBlock,
        string performedBy, CancellationToken cancellationToken = default)
    {
        var trimmedName = name.Trim();
        if (trimmedName.Length == 0) throw new ArgumentException("Give the calendar a name.");
        var uri = NormalizeAddress(address);
        var feed = new BookingCalendarFeed
        {
            Name = trimmedName.Length > 120 ? trimmedName[..120] : trimmedName,
            UrlProtected = secrets.Protect(uri.AbsoluteUri),
            UrlHint = uri.Host,
            OwnerUsername = NormalizeOwner(ownerUsername),
            AllDayEventsBlock = allDayEventsBlock,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedBy = performedBy
        };
        if (await RefreshAsync(feed, uri, cancellationToken) is { } error)
            throw new InvalidOperationException($"Couldn't read that calendar: {error}");

        db.BookingCalendarFeeds.Add(feed);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(feed);
    }

    public async Task RemoveFeedAsync(Guid feedId, CancellationToken cancellationToken = default)
    {
        var feed = await db.BookingCalendarFeeds.FirstOrDefaultAsync(item => item.Id == feedId, cancellationToken);
        if (feed is null) return;
        db.BookingCalendarFeeds.Remove(feed);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BookingCalendarFeedView?> SyncFeedAsync(Guid feedId, CancellationToken cancellationToken = default)
    {
        var feed = await db.BookingCalendarFeeds.FirstOrDefaultAsync(item => item.Id == feedId, cancellationToken);
        if (feed is null) return null;

        Uri uri;
        try
        {
            uri = new Uri(secrets.Unprotect(feed.UrlProtected));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or UriFormatException)
        {
            feed.LastAttemptAt = timeProvider.GetUtcNow();
            feed.LastError = "the saved address can't be read any more - remove this calendar and add it again.";
            await db.SaveChangesAsync(cancellationToken);
            return ToView(feed);
        }

        if (await RefreshAsync(feed, uri, cancellationToken) is { } error)
            logger.LogWarning("Booking calendar {Name} couldn't be read: {Error}", feed.Name, error);
        feed.UpdatedAt = feed.LastAttemptAt;
        feed.UpdatedBy = "calendar-sync";
        await db.SaveChangesAsync(cancellationToken);
        return ToView(feed);
    }

    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var ids = await db.BookingCalendarFeeds.AsNoTracking().Select(feed => feed.Id).ToListAsync(cancellationToken);
        var synced = 0;
        foreach (var id in ids)
        {
            if (await SyncFeedAsync(id, cancellationToken) is { LastError: null }) synced++;
        }

        return synced;
    }

    public async Task<IReadOnlyList<BookingBusyTime>> GetBusyAsync(string? ownerUsername, TimeZoneInfo zone, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var feeds = (await db.BookingCalendarFeeds.AsNoTracking()
                .Select(feed => new { feed.OwnerUsername, feed.BusyJson })
                .ToListAsync(cancellationToken))
            .Where(feed => feed.OwnerUsername is null || string.Equals(feed.OwnerUsername, owner, StringComparison.OrdinalIgnoreCase));

        var busy = new List<BookingBusyTime>();
        foreach (var feed in feeds)
        {
            foreach (var entry in BookingCalendarIcs.FromJson(feed.BusyJson))
            {
                var range = entry switch
                {
                    { S: { } start, E: { } end } => new BookingBusyTime(start, end),
                    { D: { } first, U: { } after } => new BookingBusyTime(DayStart(first, zone), DayStart(after, zone)),
                    _ => null
                };
                if (range is not null && range.Start < toUtc && range.End > fromUtc) busy.Add(range);
            }
        }

        return busy;
    }

    public async Task<BookingCalendarExportView> GetExportAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.BookingCalendarSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings?.ExportTokenProtected is not { } protectedToken) return new BookingCalendarExportView(null);
        try
        {
            return new BookingCalendarExportView(ExportUrl(secrets.Unprotect(protectedToken)));
        }
        catch (CryptographicException)
        {
            return new BookingCalendarExportView(null);
        }
    }

    public async Task<BookingCalendarExportView> CreateExportLinkAsync(string performedBy, CancellationToken cancellationToken = default)
    {
        var settings = await db.BookingCalendarSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new BookingCalendarSettings { CreatedAt = timeProvider.GetUtcNow(), CreatedBy = performedBy };
            db.BookingCalendarSettings.Add(settings);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        settings.ExportTokenHash = HashToken(token);
        settings.ExportTokenProtected = secrets.Protect(token);
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return new BookingCalendarExportView(ExportUrl(token));
    }

    public async Task DisableExportAsync(string performedBy, CancellationToken cancellationToken = default)
    {
        var settings = await db.BookingCalendarSettings.FirstOrDefaultAsync(cancellationToken);
        if (settings is null) return;
        settings.ExportTokenHash = null;
        settings.ExportTokenProtected = null;
        settings.UpdatedAt = timeProvider.GetUtcNow();
        settings.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> RenderExportAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return null;
        var hash = HashToken(token);
        if (!await db.BookingCalendarSettings.AsNoTracking().AnyAsync(settings => settings.ExportTokenHash == hash, cancellationToken)) return null;

        // SQLite/EF Core can't translate DateTimeOffset comparisons or ordering - filter and sort here.
        var since = timeProvider.GetUtcNow().AddDays(-ExportDaysBack);
        var bookings = (await db.Bookings.AsNoTracking()
                .Where(booking => booking.Status == BookingStatuses.Confirmed)
                .ToListAsync(cancellationToken))
            .Where(booking => booking.EndsAt > since)
            .OrderBy(booking => booking.StartsAt)
            .ToList();
        var titles = await db.BookingTypes.AsNoTracking().ToDictionaryAsync(type => type.Id, type => type.Title, cancellationToken);
        return BookingCalendarIcs.RenderBookings(bookings.Select(booking => (booking, titles.GetValueOrDefault(booking.BookingTypeId) ?? "Booking")));
    }

    // webcal:// is how calendar apps hand out subscription links; it's plain https underneath.
    // Only https is accepted: the address is a credential and shouldn't travel in the clear.
    public static Uri NormalizeAddress(string address)
    {
        var trimmed = address.Trim();
        if (trimmed.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) trimmed = "https://" + trimmed["webcal://".Length..];
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
            throw new ArgumentException("Paste the calendar's iCal address - it starts with https:// or webcal://.");
        return uri;
    }

    internal static string? NormalizeOwner(string? owner) => string.IsNullOrWhiteSpace(owner) ? null : owner.Trim();

    // Reads the calendar into the feed's busy times. Returns the reason when it can't, leaving
    // the last good busy times in place.
    private async Task<string?> RefreshAsync(BookingCalendarFeed feed, Uri uri, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        feed.LastAttemptAt = now;
        string? error;
        try
        {
            var client = httpClients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                error = $"the calendar answered {(int)response.StatusCode} {response.ReasonPhrase} - check the address is still shared.";
            }
            else
            {
                var text = await ReadLimitedAsync(response, cancellationToken);
                var result = BookingCalendarIcs.Parse(text, now.AddDays(-1), now.AddDays(BookingService.MaxDaysAhead + 1),
                    timeProvider.LocalTimeZone, feed.AllDayEventsBlock);
                feed.BusyJson = BookingCalendarIcs.ToJson(result.Busy);
                feed.BusyCount = result.Busy.Count;
                feed.SkippedCount = result.Skipped;
                feed.LastSyncedAt = now;
                feed.LastError = null;
                return null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            error = "it took too long to answer.";
        }
        // The address check refuses private networks and names that don't resolve.
        catch (HttpRequestException ex) when (ex.InnerException is InvalidOperationException inner)
        {
            error = inner.Message.Contains("could not be resolved", StringComparison.Ordinal)
                ? $"couldn't find the server {uri.Host}."
                : "that address points to a private network.";
        }
        catch (HttpRequestException ex)
        {
            error = $"couldn't connect ({ex.Message}).";
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Booking calendar {Name} couldn't be parsed.", feed.Name);
            error = "it isn't a calendar in iCal format.";
        }

        feed.LastError = error;
        return error;
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const string tooLarge = "the calendar is larger than 5 MB.";
        if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new InvalidDataException(tooLarge);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxFeedBytes) throw new InvalidDataException(tooLarge);
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // Midnight in the zone; where midnight is skipped by a clock change, the first hour that exists.
    private static DateTimeOffset DayStart(DateOnly date, TimeZoneInfo zone) =>
        BookingService.LocalToUtc(date, TimeOnly.MinValue, zone)
        ?? BookingService.LocalToUtc(date, new TimeOnly(1, 0), zone)
        ?? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), zone.BaseUtcOffset).ToUniversalTime();

    private static string ExportUrl(string token) => $"{ExportPath}{token}.ics";

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static BookingCalendarFeedView ToView(BookingCalendarFeed feed) => new(
        feed.Id, feed.Name, feed.UrlHint, feed.OwnerUsername, feed.AllDayEventsBlock, feed.BusyCount, feed.SkippedCount,
        feed.LastSyncedAt, feed.LastAttemptAt, feed.LastError);
}

// Reads every booking calendar every 10 minutes, so a new event hides its slots within about
// that long. The first read waits a minute after startup.
public sealed class BookingCalendarBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<BookingCalendarBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IBookingCalendarService>().SyncAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Booking calendar sync failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
