using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelPresenceService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    TimeProvider timeProvider) : ISentinelPresenceService
{
    public async Task EnterAsync(Guid sessionId, string username, Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var lease = await db.SentinelPresenceLeases.FirstOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (lease is null)
        {
            lease = new SentinelPresenceLease
            {
                Id = sessionId, Username = Normalize(username), WikiPageId = wikiPageId,
                LastSeenAt = now, LastSeenAtUnixSeconds = now.ToUnixTimeSeconds(),
                CreatedAt = now, CreatedBy = Normalize(username)
            };
            db.SentinelPresenceLeases.Add(lease);
        }
        else
        {
            lease.WikiPageId = wikiPageId;
            lease.LastSeenAt = now;
            lease.LastSeenAtUnixSeconds = now.ToUnixTimeSeconds();
            lease.UpdatedAt = now;
            lease.UpdatedBy = Normalize(username);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task TouchAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var lease = await db.SentinelPresenceLeases.FirstOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (lease is null) return;
        var now = timeProvider.GetUtcNow();
        lease.LastSeenAt = now;
        lease.LastSeenAtUnixSeconds = now.ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task LeaveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var lease = await db.SentinelPresenceLeases.FirstOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (lease is null) return;
        db.SentinelPresenceLeases.Remove(lease);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SentinelPresenceView>> ListAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var cutoffUnix = (timeProvider.GetUtcNow() - SentinelPresenceTracker.SessionTimeout).ToUnixTimeSeconds();

        // LastSeenAtUnixSeconds (unlike LastSeenAt itself) is a real integer column, so both
        // queries below push their filter down as SQL instead of materializing every presence
        // lease across the entire workspace on every poll of every page - this used to load
        // the whole SentinelPresenceLeases table regardless of which page asked.
        var expired = await db.SentinelPresenceLeases
            .Where(item => item.LastSeenAtUnixSeconds < cutoffUnix)
            .ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            db.SentinelPresenceLeases.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);
        }

        var leases = await db.SentinelPresenceLeases
            .Where(item => item.WikiPageId == wikiPageId && item.LastSeenAtUnixSeconds >= cutoffUnix)
            .ToListAsync(cancellationToken);
        return leases.GroupBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SentinelPresenceView(group.Key, group.Count(), group.Max(item => item.LastSeenAt)))
            .OrderBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string username) => string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim();
}
