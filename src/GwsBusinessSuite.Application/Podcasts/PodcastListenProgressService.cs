using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Podcasts;

public sealed class PodcastListenProgressService(
    IAppDbContext dbContext,
    ICurrentUserAccessor? currentUserAccessor = null) : IPodcastListenProgressService
{
    // "Nearly done" rather than exactly 100% - matches how most podcast apps treat the
    // last few seconds (often trailing silence/outro) as effectively finished.
    private const double CompletionThreshold = 0.95;

    private readonly ICurrentUserAccessor _currentUserAccessor = currentUserAccessor ?? FixedCurrentUserAccessor.Unknown;

    public async Task<IReadOnlyDictionary<Guid, PodcastListenProgressView>> GetProgressForEpisodesAsync(
        IReadOnlyList<Guid> episodeIds, CancellationToken cancellationToken = default)
    {
        if (episodeIds.Count == 0)
        {
            return new Dictionary<Guid, PodcastListenProgressView>();
        }

        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var rows = await dbContext.PodcastListenProgresses
            .Where(p => p.Username == username && episodeIds.Contains(p.EpisodeId))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            p => p.EpisodeId,
            p => new PodcastListenProgressView(p.EpisodeId, p.PositionSeconds, p.DurationSeconds, p.IsCompleted, p.LastPlayedAt));
    }

    public async Task SaveProgressAsync(
        Guid episodeId, int positionSeconds, int? durationSeconds, CancellationToken cancellationToken = default)
    {
        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var row = await dbContext.PodcastListenProgresses
            .FirstOrDefaultAsync(p => p.Username == username && p.EpisodeId == episodeId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new PodcastListenProgress
            {
                EpisodeId = episodeId,
                Username = username,
                CreatedBy = username
            };
            dbContext.PodcastListenProgresses.Add(row);
        }

        row.PositionSeconds = Math.Max(0, positionSeconds);
        row.DurationSeconds = durationSeconds;
        row.LastPlayedAt = now;
        row.UpdatedAt = now;
        row.UpdatedBy = username;

        if (durationSeconds is > 0 && positionSeconds >= durationSeconds * CompletionThreshold)
        {
            row.IsCompleted = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkCompletedAsync(Guid episodeId, CancellationToken cancellationToken = default)
    {
        var username = await _currentUserAccessor.GetCurrentUsernameAsync(cancellationToken);
        var row = await dbContext.PodcastListenProgresses
            .FirstOrDefaultAsync(p => p.Username == username && p.EpisodeId == episodeId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new PodcastListenProgress
            {
                EpisodeId = episodeId,
                Username = username,
                CreatedBy = username
            };
            dbContext.PodcastListenProgresses.Add(row);
        }

        row.IsCompleted = true;
        row.LastPlayedAt = now;
        row.UpdatedAt = now;
        row.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
