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
                LastSeenAt = now, CreatedAt = now, CreatedBy = Normalize(username)
            };
            db.SentinelPresenceLeases.Add(lease);
        }
        else
        {
            lease.WikiPageId = wikiPageId;
            lease.LastSeenAt = now;
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
        lease.LastSeenAt = timeProvider.GetUtcNow();
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
        var cutoff = timeProvider.GetUtcNow() - SentinelPresenceTracker.SessionTimeout;
        // SQLite stores DateTimeOffset as TEXT and cannot translate ordering comparisons.
        // Presence leases are bounded by active browser sessions, so materialize this small
        // set once and apply the cutoff provider-independently in memory.
        var allLeases = await db.SentinelPresenceLeases.ToListAsync(cancellationToken);
        var expired = allLeases.Where(item => item.LastSeenAt < cutoff).ToList();
        if (expired.Count > 0)
        {
            db.SentinelPresenceLeases.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);
        }
        var leases = allLeases
            .Where(item => item.WikiPageId == wikiPageId && item.LastSeenAt >= cutoff)
            .ToList();
        return leases.GroupBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SentinelPresenceView(group.Key, group.Count(), group.Max(item => item.LastSeenAt)))
            .OrderBy(item => item.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string username) => string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim();
}
