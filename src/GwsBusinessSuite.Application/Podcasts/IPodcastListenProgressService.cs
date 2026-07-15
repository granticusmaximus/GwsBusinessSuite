namespace GwsBusinessSuite.Application.Podcasts;

// Apple-Podcasts-style per-account resume position, scoped to the current AppUser
// (Admin/Author/Contributor) rather than a public visitor, since this app has no
// visitor-facing account system - see PodcastListenProgress's doc comment in
// CoreEntities.cs for why it's keyed by Username rather than an AppUser FK.
public interface IPodcastListenProgressService
{
    // Keyed by EpisodeId - callers ask for a whole episode list's progress in one round
    // trip rather than one row at a time.
    Task<IReadOnlyDictionary<Guid, PodcastListenProgressView>> GetProgressForEpisodesAsync(
        IReadOnlyList<Guid> episodeIds,
        CancellationToken cancellationToken = default);

    // Upserts the current position for the current user. Auto-marks IsCompleted once
    // position reaches the "nearly done" threshold when duration is known (see
    // PodcastListenProgressService.CompletionThreshold) - the browser calling this
    // periodically during playback is what drives that, not a separate explicit call.
    Task SaveProgressAsync(
        Guid episodeId,
        int positionSeconds,
        int? durationSeconds,
        CancellationToken cancellationToken = default);

    // Explicit completion signal from the browser's "ended" event - covers episodes
    // whose duration wasn't known upfront (so the position-based threshold in
    // SaveProgressAsync couldn't fire).
    Task MarkCompletedAsync(Guid episodeId, CancellationToken cancellationToken = default);
}

public sealed record PodcastListenProgressView(
    Guid EpisodeId,
    int PositionSeconds,
    int? DurationSeconds,
    bool IsCompleted,
    DateTimeOffset LastPlayedAt);
