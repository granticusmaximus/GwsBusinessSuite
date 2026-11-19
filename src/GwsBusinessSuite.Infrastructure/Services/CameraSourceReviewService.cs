using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record CameraSourceRow(string SourceName, CameraSourceDrafts.Draft Draft, string? ConfirmedBy, DateTimeOffset? ConfirmedAt, bool IsOff);

// The Camera sources page and the on/off switch the camera directory reads (19-11-26). The set of
// switched-off sources is kept in memory and re-read at most once a minute, off the caller's
// thread, so every map move doesn't touch the database.
public sealed class CameraSourceReviewService(
    IDbContextFactory<ApplicationDbContext> contexts,
    ILogger<CameraSourceReviewService> logger,
    TimeProvider? time = null) : ICameraSourceSwitch
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;
    private int _refreshing;

    public bool IsOff(string sourceName)
    {
        if (_time.GetUtcNow() - _readAt > RefreshEvery && Interlocked.Exchange(ref _refreshing, 1) == 0)
            _ = Task.Run(RefreshAsync);
        return _off.Contains(sourceName);
    }

    private async Task RefreshAsync()
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync();
            var off = await db.CameraSourceReviews.AsNoTracking().Where(r => r.IsOff).Select(r => r.SourceName).ToListAsync();
            _off = new HashSet<string>(off, StringComparer.OrdinalIgnoreCase);
            _readAt = _time.GetUtcNow();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(ex, "Camera sources: couldn't read which sources are switched off");
        }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    // Every source the app has, with its draft and review, unconfirmed first.
    public async Task<IReadOnlyList<CameraSourceRow>> ListAsync(IEnumerable<string> sourceNames, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var reviews = (await db.CameraSourceReviews.AsNoTracking().ToListAsync(ct))
            .ToDictionary(r => r.SourceName, StringComparer.OrdinalIgnoreCase);
        return sourceNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => reviews.TryGetValue(name, out var review)
                ? new CameraSourceRow(name, CameraSourceDrafts.For(name), review.ConfirmedBy, review.ConfirmedAt, review.IsOff)
                : new CameraSourceRow(name, CameraSourceDrafts.For(name), null, null, false))
            .OrderBy(row => row.ConfirmedAt is not null).ThenBy(row => row.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task ConfirmAsync(string sourceName, string username, CancellationToken ct = default) =>
        ChangeAsync(sourceName, username, review => { review.ConfirmedBy = username; review.ConfirmedAt = _time.GetUtcNow(); }, ct);

    public async Task SetOffAsync(string sourceName, bool off, string username, CancellationToken ct = default)
    {
        await ChangeAsync(sourceName, username, review => review.IsOff = off, ct);
        var next = new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase);
        if (off) next.Add(sourceName); else next.Remove(sourceName);
        _off = next;
        logger.LogInformation("Camera sources: {User} switched {Source} {State}", username, sourceName, off ? "off" : "on");
    }

    private async Task ChangeAsync(string sourceName, string username, Action<CameraSourceReview> change, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var review = await db.CameraSourceReviews.FirstOrDefaultAsync(r => r.SourceName == sourceName, ct);
        if (review is null)
        {
            review = new CameraSourceReview { SourceName = sourceName, CreatedBy = username };
            db.CameraSourceReviews.Add(review);
        }
        change(review);
        review.UpdatedAt = _time.GetUtcNow();
        review.UpdatedBy = username;
        await db.SaveChangesAsync(ct);
    }
}
