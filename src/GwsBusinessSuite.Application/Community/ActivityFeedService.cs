using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Community;

public sealed class ActivityFeedService(IAppDbContext dbContext) : IActivityFeedService
{
    public async Task RecordAsync(string performedBy, string verb, string targetLabel, string targetUrl, CancellationToken cancellationToken = default)
    {
        dbContext.ActivityEvents.Add(new ActivityEvent
        {
            CreatedBy = string.IsNullOrWhiteSpace(performedBy) ? "unknown" : performedBy,
            Verb = verb,
            TargetLabel = targetLabel,
            TargetUrl = targetUrl
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityEventView>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        // Materialize then sort/take client-side - EF Core/SQLite can't translate an
        // ORDER BY over a DateTimeOffset column (established gotcha elsewhere in this codebase,
        // e.g. RelatedArticlesService/CrmService's own "materialize then filter" comments).
        var all = await dbContext.ActivityEvents.AsNoTracking().ToListAsync(cancellationToken);
        var events = all
            .OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToList();
        if (events.Count == 0) return [];

        // Resolve actor display names in one batched pass rather than per-row - same
        // "materialize small tables, join in-memory" posture as CommunityDirectoryService.
        var actorUsernames = events.Select(e => e.CreatedBy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lowerUsernames = actorUsernames.Select(u => u.ToLowerInvariant()).ToList();
        var users = await dbContext.AppUsers.AsNoTracking()
            .Where(u => lowerUsernames.Contains(u.Username.ToLower()))
            .ToListAsync(cancellationToken);
        var profiles = await dbContext.MemberProfiles.AsNoTracking()
            .Where(p => users.Select(u => u.Id).Contains(p.AppUserId))
            .ToListAsync(cancellationToken);
        var profileByUserId = profiles.ToDictionary(p => p.AppUserId);
        var displayNameByUsername = users.ToDictionary(
            u => u.Username,
            u => profileByUserId.TryGetValue(u.Id, out var p) && !string.IsNullOrWhiteSpace(p.DisplayName) ? p.DisplayName : u.Username,
            StringComparer.OrdinalIgnoreCase);

        return events.Select(e => new ActivityEventView(
            e.Id,
            e.CreatedBy,
            displayNameByUsername.GetValueOrDefault(e.CreatedBy, e.CreatedBy),
            e.Verb,
            e.TargetLabel,
            e.TargetUrl,
            e.CreatedAt
        )).ToList();
    }
}
