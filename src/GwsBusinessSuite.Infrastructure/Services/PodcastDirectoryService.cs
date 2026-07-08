using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CodeHollow.FeedReader;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Podcasts;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class PodcastDirectoryService(
    IAppDbContextFactory dbContextFactory,
    HttpClient http,
    ILogger<PodcastDirectoryService> logger) : IPodcastDirectoryService
{
    private static readonly (string Category, string Icon)[] FeaturedCategories =
    [
        ("Comedy", "😂"),
        ("True Crime", "🔍"),
        ("News", "📰"),
        ("Technology", "💻"),
        ("Business", "💼"),
        ("Health", "🏥")
    ];

    private static readonly string[] DiscoverQueries =
    [
        "podcast",
        "comedy",
        "news",
        "technology",
        "business",
        "health"
    ];

    private const int MaxSearchLimit = 50;
    private const int EpisodeRefreshHours = 12;

    public async Task<IReadOnlyList<PodcastCatalogItem>> SearchCatalogAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return await SearchAppleAsync(query.Trim(), limit, cancellationToken);
    }

    public async Task<IReadOnlyList<PodcastCatalogItem>> DiscoverAsync(
        string? category,
        int limit = 30,
        CancellationToken cancellationToken = default)
    {
        var normalizedCategory = NormalizeCategory(category);
        if (string.IsNullOrWhiteSpace(normalizedCategory) || normalizedCategory.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            var perQueryLimit = Math.Max(6, (int)Math.Ceiling(Math.Clamp(limit, 1, MaxSearchLimit) / (double)DiscoverQueries.Length));
            var queries = DiscoverQueries.Select(query => SearchAppleAsync(query, perQueryLimit, cancellationToken));
            var batches = await Task.WhenAll(queries);
            return Deduplicate(batches.SelectMany(x => x)).Take(Math.Clamp(limit, 1, MaxSearchLimit)).ToList();
        }

        return await SearchAppleAsync(normalizedCategory, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<FeaturedPodcastSection>> GetFeaturedSectionsAsync(
        CancellationToken cancellationToken = default)
    {
        var sections = new List<FeaturedPodcastSection>(FeaturedCategories.Length);

        foreach (var category in FeaturedCategories)
        {
            var podcasts = await SearchAppleAsync(category.Category, 6, cancellationToken);
            sections.Add(new FeaturedPodcastSection(category.Category, category.Icon, podcasts));
        }

        return sections;
    }

    public async Task<IReadOnlyList<SavedPodcastSummary>> ListLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var shows = await db.PodcastShows
            .AsNoTracking()
            .OrderByDescending(show => show.CreatedAt)
            .ToListAsync(cancellationToken);

        var counts = await db.PodcastEpisodes
            .AsNoTracking()
            .GroupBy(episode => episode.PodcastShowId)
            .Select(group => new { PodcastShowId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.PodcastShowId, x => x.Count, cancellationToken);

        return shows
            .Select(show => ToSummary(show, counts.GetValueOrDefault(show.Id, 0)))
            .ToList();
    }

    public async Task<PodcastSaveResult> SavePodcastAsync(
        PodcastCatalogItem podcast,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(podcast.FeedUrl))
        {
            throw new InvalidOperationException("This podcast cannot be saved because it does not expose an RSS feed URL.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await FindExistingShowAsync(db, podcast, cancellationToken);
        if (existing is not null)
        {
            var episodeCount = await db.PodcastEpisodes.CountAsync(x => x.PodcastShowId == existing.Id, cancellationToken);
            return new PodcastSaveResult(ToSummary(existing, episodeCount), AlreadySaved: true);
        }

        var show = new PodcastShow
        {
            Title = Truncate(podcast.Title.Trim(), 500),
            Author = Truncate(podcast.Author.Trim(), 300),
            Description = Truncate(CleanText(podcast.Description), 4000),
            Category = Truncate(NormalizeCategory(podcast.Category), 200),
            FeedUrl = Truncate(podcast.FeedUrl.Trim(), 1000),
            ImageUrl = Truncate(podcast.ImageUrl.Trim(), 1000),
            AppleUrl = Truncate(podcast.AppleUrl.Trim(), 1000),
            ItunesId = string.IsNullOrWhiteSpace(podcast.ItunesId) ? null : podcast.ItunesId.Trim()
        };

        db.PodcastShows.Add(show);
        await db.SaveChangesAsync(cancellationToken);

        return new PodcastSaveResult(ToSummary(show, 0), AlreadySaved: false);
    }

    public async Task DeletePodcastAsync(
        Guid podcastId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        await db.PodcastEpisodes.Where(x => x.PodcastShowId == podcastId).ExecuteDeleteAsync(cancellationToken);

        var show = await db.PodcastShows.FirstOrDefaultAsync(x => x.Id == podcastId, cancellationToken);
        if (show is null)
        {
            return;
        }

        db.PodcastShows.Remove(show);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PodcastDetailView?> GetPodcastDetailAsync(
        Guid podcastId,
        bool refreshEpisodes = false,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var show = await db.PodcastShows.FirstOrDefaultAsync(x => x.Id == podcastId, cancellationToken);
        if (show is null)
        {
            return null;
        }

        var shouldRefresh = !string.IsNullOrWhiteSpace(show.FeedUrl) &&
            (refreshEpisodes ||
             show.LastEpisodeRefreshAt is null ||
             show.LastEpisodeRefreshAt < DateTimeOffset.UtcNow.AddHours(-EpisodeRefreshHours) ||
             !await db.PodcastEpisodes.AnyAsync(x => x.PodcastShowId == show.Id, cancellationToken));

        if (shouldRefresh)
        {
            await RefreshEpisodesAsync(db, show, cancellationToken);
        }

        var episodes = await db.PodcastEpisodes
            .AsNoTracking()
            .Where(x => x.PodcastShowId == show.Id)
            .ToListAsync(cancellationToken);

        episodes = episodes
            .OrderByDescending(x => x.PublishedAt ?? x.CreatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ToList();

        return new PodcastDetailView(
            ToSummary(show, episodes.Count),
            episodes.Select(ToEpisodeView).ToList());
    }

    private async Task<IReadOnlyList<PodcastCatalogItem>> SearchAppleAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.GetFromJsonAsync<AppleSearchResponse>(
                $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=podcast&entity=podcast&limit={Math.Clamp(limit, 1, MaxSearchLimit)}",
                cancellationToken);

            if (response?.Results is null || response.Results.Count == 0)
            {
                return [];
            }

            return Deduplicate(response.Results.Select(MapCatalogItem))
                .Take(Math.Clamp(limit, 1, MaxSearchLimit))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Podcast search failed for query {Query}", query);
            return [];
        }
    }

    private async Task RefreshEpisodesAsync(
        IAppDbContext db,
        PodcastShow show,
        CancellationToken cancellationToken)
    {
        var episodes = await FetchEpisodesAsync(show.FeedUrl, cancellationToken);

        await db.PodcastEpisodes.Where(x => x.PodcastShowId == show.Id).ExecuteDeleteAsync(cancellationToken);

        foreach (var episode in episodes)
        {
            db.PodcastEpisodes.Add(new PodcastEpisode
            {
                PodcastShowId = show.Id,
                Title = Truncate(episode.Title, 500),
                Description = Truncate(episode.Description, 4000),
                AudioUrl = Truncate(episode.AudioUrl, 1000),
                DurationSeconds = episode.DurationSeconds,
                PublishedAt = episode.PublishedAt,
                ExternalId = Truncate(episode.ExternalId, 1000)
            });
        }

        show.LastEpisodeRefreshAt = DateTimeOffset.UtcNow;
        show.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<ParsedEpisode>> FetchEpisodesAsync(
        string feedUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await http.GetStringAsync(feedUrl, cancellationToken);
            var feed = FeedReader.ReadFromString(content);

            return feed.Items
                .Select(MapEpisode)
                .Where(x => !string.IsNullOrWhiteSpace(x.AudioUrl))
                .GroupBy(x => x.ExternalId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(x => x.PublishedAt ?? DateTimeOffset.MinValue)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to refresh podcast feed {FeedUrl}", feedUrl);
            return [];
        }
    }

    private static ParsedEpisode MapEpisode(FeedItem item)
    {
        string audioUrl = string.Empty;
        string externalId = string.Empty;
        int? durationSeconds = null;

        if (item.SpecificItem is CodeHollow.FeedReader.Feeds.Rss20FeedItem rss)
        {
            audioUrl = rss.Element?.Elements().FirstOrDefault(x => x.Name.LocalName == "enclosure")?.Attribute("url")?.Value?.Trim() ?? string.Empty;
            externalId = rss.Element?.Elements().FirstOrDefault(x => x.Name.LocalName == "guid")?.Value?.Trim() ?? string.Empty;

            var durationRaw = rss.Element?.Elements().FirstOrDefault(x => x.Name.LocalName == "duration")?.Value;
            durationSeconds = ParseDurationSeconds(durationRaw);
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            externalId = !string.IsNullOrWhiteSpace(audioUrl)
                ? audioUrl
                : item.Link?.Trim() ?? item.Title?.Trim() ?? Guid.NewGuid().ToString("N");
        }

        return new ParsedEpisode(
            Title: string.IsNullOrWhiteSpace(item.Title) ? "Untitled Episode" : item.Title.Trim(),
            Description: CleanText(item.Description),
            AudioUrl: audioUrl,
            DurationSeconds: durationSeconds,
            PublishedAt: item.PublishingDate,
            ExternalId: externalId);
    }

    private async Task<PodcastShow?> FindExistingShowAsync(
        IAppDbContext db,
        PodcastCatalogItem podcast,
        CancellationToken cancellationToken)
    {
        var keys = PodcastDirectoryIdentity.BuildKeys(
            podcast.ItunesId,
            podcast.FeedUrl,
            podcast.AppleUrl,
            podcast.Title,
            podcast.Author);

        if (keys.Count == 0)
        {
            return null;
        }

        var shows = await db.PodcastShows.ToListAsync(cancellationToken);
        return shows.FirstOrDefault(show =>
            PodcastDirectoryIdentity.BuildKeys(show.ItunesId, show.FeedUrl, show.AppleUrl, show.Title, show.Author)
                .Any(key => keys.Contains(key, StringComparer.OrdinalIgnoreCase)));
    }

    private static PodcastCatalogItem MapCatalogItem(AppleSearchResult result)
    {
        var title = result.CollectionName ?? result.TrackName ?? "Untitled Podcast";
        var description = CleanText(result.Description);

        return new PodcastCatalogItem(
            Title: title.Trim(),
            Author: (result.ArtistName ?? string.Empty).Trim(),
            Description: Truncate(description, 2000),
            Category: NormalizeCategory(result.PrimaryGenreName),
            FeedUrl: (result.FeedUrl ?? string.Empty).Trim(),
            ImageUrl: (result.ArtworkUrl600 ?? result.ArtworkUrl100 ?? string.Empty).Trim(),
            AppleUrl: (result.CollectionViewUrl ?? string.Empty).Trim(),
            ItunesId: result.CollectionId?.ToString());
    }

    private static List<PodcastCatalogItem> Deduplicate(IEnumerable<PodcastCatalogItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PodcastCatalogItem>();

        foreach (var item in items)
        {
            var keys = PodcastDirectoryIdentity.BuildKeys(
                item.ItunesId,
                item.FeedUrl,
                item.AppleUrl,
                item.Title,
                item.Author);

            if (keys.Count == 0 || keys.Any(key => seen.Contains(key)))
            {
                continue;
            }

            foreach (var key in keys)
            {
                seen.Add(key);
            }

            results.Add(item);
        }

        return results;
    }

    private static SavedPodcastSummary ToSummary(PodcastShow show, int episodeCount) =>
        new(
            show.Id,
            show.Title,
            show.Author,
            show.Description,
            NormalizeCategory(show.Category),
            show.FeedUrl,
            show.ImageUrl,
            show.AppleUrl,
            show.ItunesId,
            episodeCount,
            show.LastEpisodeRefreshAt);

    private static PodcastEpisodeView ToEpisodeView(PodcastEpisode episode) =>
        new(
            episode.Id,
            episode.Title,
            episode.Description,
            episode.AudioUrl,
            episode.DurationSeconds,
            episode.PublishedAt);

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var withoutTags = Regex.Replace(value, "<[^>]*>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, "\\s+", " ").Trim();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static int? ParseDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var rawSeconds))
        {
            return rawSeconds;
        }

        var parts = value.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var minutes) &&
            int.TryParse(parts[1], out var seconds))
        {
            return (minutes * 60) + seconds;
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], out var hours) &&
            int.TryParse(parts[1], out var hourMinutes) &&
            int.TryParse(parts[2], out var hourSeconds))
        {
            return (hours * 3600) + (hourMinutes * 60) + hourSeconds;
        }

        return null;
    }

    private sealed record ParsedEpisode(
        string Title,
        string Description,
        string AudioUrl,
        int? DurationSeconds,
        DateTimeOffset? PublishedAt,
        string ExternalId);

    private sealed record AppleSearchResponse(List<AppleSearchResult> Results);

    private sealed record AppleSearchResult(
        string? CollectionName,
        string? TrackName,
        string? ArtistName,
        string? Description,
        string? PrimaryGenreName,
        string? FeedUrl,
        string? ArtworkUrl600,
        string? ArtworkUrl100,
        string? CollectionViewUrl,
        long? CollectionId);
}
