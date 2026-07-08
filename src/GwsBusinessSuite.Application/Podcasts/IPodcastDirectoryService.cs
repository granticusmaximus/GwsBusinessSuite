namespace GwsBusinessSuite.Application.Podcasts;

public interface IPodcastDirectoryService
{
    Task<IReadOnlyList<PodcastCatalogItem>> SearchCatalogAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PodcastCatalogItem>> DiscoverAsync(
        string? category,
        int limit = 30,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeaturedPodcastSection>> GetFeaturedSectionsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SavedPodcastSummary>> ListLibraryAsync(
        CancellationToken cancellationToken = default);

    Task<PodcastSaveResult> SavePodcastAsync(
        PodcastCatalogItem podcast,
        CancellationToken cancellationToken = default);

    Task DeletePodcastAsync(
        Guid podcastId,
        CancellationToken cancellationToken = default);

    Task<PodcastDetailView?> GetPodcastDetailAsync(
        Guid podcastId,
        bool refreshEpisodes = false,
        CancellationToken cancellationToken = default);
}

public sealed record PodcastCatalogItem(
    string Title,
    string Author,
    string Description,
    string Category,
    string FeedUrl,
    string ImageUrl,
    string AppleUrl,
    string? ItunesId);

public sealed record FeaturedPodcastSection(
    string Category,
    string Icon,
    IReadOnlyList<PodcastCatalogItem> Podcasts);

public sealed record SavedPodcastSummary(
    Guid Id,
    string Title,
    string Author,
    string Description,
    string Category,
    string FeedUrl,
    string ImageUrl,
    string AppleUrl,
    string? ItunesId,
    int EpisodeCount,
    DateTimeOffset? LastEpisodeRefreshAt);

public sealed record PodcastEpisodeView(
    Guid Id,
    string Title,
    string Description,
    string AudioUrl,
    int? DurationSeconds,
    DateTimeOffset? PublishedAt);

public sealed record PodcastDetailView(
    SavedPodcastSummary Podcast,
    IReadOnlyList<PodcastEpisodeView> Episodes);

public sealed record PodcastSaveResult(
    SavedPodcastSummary Podcast,
    bool AlreadySaved);

public static class PodcastDirectoryIdentity
{
    public static IReadOnlyList<string> BuildKeys(
        string? itunesId,
        string? feedUrl,
        string? appleUrl,
        string? title,
        string? author)
    {
        var keys = new List<string>();

        if (!string.IsNullOrWhiteSpace(itunesId))
        {
            keys.Add($"itunes:{itunesId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(feedUrl))
        {
            keys.Add($"feed:{NormalizeUrl(feedUrl)}");
        }

        if (!string.IsNullOrWhiteSpace(appleUrl))
        {
            keys.Add($"apple:{NormalizeUrl(appleUrl)}");
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            keys.Add($"name:{NormalizeText(title)}|{NormalizeText(author)}");
        }

        return keys;
    }

    private static string NormalizeUrl(string value)
    {
        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed.ToLowerInvariant();
        }

        var normalized = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        return normalized.TrimEnd('/').ToLowerInvariant();
    }

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
