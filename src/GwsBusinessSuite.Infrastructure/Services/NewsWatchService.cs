using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeHollow.FeedReader;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.NewsIntelligence;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed partial class NewsWatchService(
    IAppDbContextFactory dbContextFactory,
    IWikiService wikiService,
    HttpClient httpClient,
    IMailTransport mailTransport,
    IOptions<GrowthReportEmailOptions> smtpOptions,
    TimeProvider timeProvider,
    ILogger<NewsWatchService> logger,
    IArticleReader? articleReader = null) : INewsWatchService
{
    public const string ClipsParentTitle = "Media Watch clips";
    private const int DigestStoriesPerTopic = 5;
    private static readonly string[] CommonFeedPaths = ["/feed", "/feed/", "/rss", "/rss.xml", "/feed.xml", "/atom.xml", "/index.xml"];

    // ---- Read state -----------------------------------------------------------------------

    public async Task<IReadOnlySet<string>> GetReadUrlHashesAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return (await db.NewsReadMarks.AsNoTracking().Where(m => m.Username == username).Select(m => m.UrlHash).ToListAsync(ct)).ToHashSet();
    }

    public async Task MarkReadAsync(string username, IEnumerable<string> urls, CancellationToken ct = default)
    {
        var hashes = urls.Select(NewsStoryClusterer.UrlHash).Distinct().ToList();
        if (hashes.Count == 0) return;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var existing = (await db.NewsReadMarks.Where(m => m.Username == username && hashes.Contains(m.UrlHash))
            .Select(m => m.UrlHash).ToListAsync(ct)).ToHashSet();
        var now = timeProvider.GetUtcNow();
        foreach (var hash in hashes.Where(h => !existing.Contains(h)))
        {
            db.NewsReadMarks.Add(new NewsReadMark { Username = username, UrlHash = hash, ReadAt = now, CreatedBy = username, CreatedAt = now });
        }
        await db.SaveChangesAsync(ct);

        // Read marks only matter while an article can still be in a feed; keep two weeks.
        var cutoff = now.AddDays(-14).ToUnixTimeSeconds();
        var stale = (await db.NewsReadMarks.Where(m => m.Username == username).Select(m => new { m.Id, m.ReadAt }).ToListAsync(ct))
            .Where(m => m.ReadAt.ToUnixTimeSeconds() < cutoff).Select(m => m.Id).ToList();
        if (stale.Count > 0) await db.NewsReadMarks.Where(m => stale.Contains(m.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetUnreadCountsAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var retention = await db.NewsWatchSettings.AsNoTracking().OrderBy(oneRow => oneRow.Id).Select(x => (int?)x.RetentionHours).FirstOrDefaultAsync(ct) ?? NewsRetention.DefaultHours;
        var cutoff = timeProvider.GetUtcNow().AddHours(-retention).ToUnixTimeSeconds();
        var items = await db.NewsItems.AsNoTracking().Where(n => n.FetchedAtUnixSeconds >= cutoff)
            .Select(n => new { n.TopicId, n.Url }).ToListAsync(ct);
        var read = (await db.NewsReadMarks.AsNoTracking().Where(m => m.Username == username).Select(m => m.UrlHash).ToListAsync(ct)).ToHashSet();
        return items
            .Where(i => !read.Contains(NewsStoryClusterer.UrlHash(i.Url)))
            .GroupBy(i => i.TopicId ?? Guid.Empty)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    // ---- Saved articles -------------------------------------------------------------------

    public async Task<IReadOnlyList<SavedArticleView>> ListSavedAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var rows = await db.SavedNewsArticles.AsNoTracking().Where(s => s.Username == username).ToListAsync(ct);
        return rows.OrderByDescending(s => s.CreatedAt)
            .Select(s => new SavedArticleView(s.Id, s.Url, s.Title, s.Source, s.TopicName, s.PublishedAt, s.Summary, s.CreatedAt, s.ClippedWikiPageId, s.ReadingMinutes, s.FinishedAt))
            .ToList();
    }

    public async Task<IReadOnlySet<string>> GetSavedUrlHashesAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return (await db.SavedNewsArticles.AsNoTracking().Where(s => s.Username == username).Select(s => s.UrlHash).ToListAsync(ct)).ToHashSet();
    }

    public async Task SaveArticleAsync(string username, NewsItemDto item, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await FindOrSaveAsync(db, username, item, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UnsaveArticleAsync(string username, string url, CancellationToken ct = default)
    {
        var hash = NewsStoryClusterer.UrlHash(url);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.SavedNewsArticles.Where(s => s.Username == username && s.UrlHash == hash).ExecuteDeleteAsync(ct);
    }

    private async Task<SavedNewsArticle> FindOrSaveAsync(IAppDbContext db, string username, NewsItemDto item, CancellationToken ct)
    {
        var hash = NewsStoryClusterer.UrlHash(item.Url);
        var saved = await db.SavedNewsArticles.FirstOrDefaultAsync(s => s.Username == username && s.UrlHash == hash, ct);
        if (saved is not null) return saved;
        saved = new SavedNewsArticle
        {
            Username = username,
            UrlHash = hash,
            Url = item.Url,
            Title = Truncate(item.Title, 500),
            Source = Truncate(item.Source, 200),
            TopicName = Truncate(item.TopicName, 200),
            PublishedAt = item.PublishedAt,
            Summary = Truncate(string.IsNullOrWhiteSpace(item.OllamaSummary) ? item.Description : item.OllamaSummary, 1000),
            ReadingMinutes = item.ReadingMinutes,
            CreatedBy = username,
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.SavedNewsArticles.Add(saved);
        return saved;
    }

    // ---- Sentinel clips -------------------------------------------------------------------

    // The clips folder is one per person (Sentinel is private per person, 12-11-26): found by its
    // personal key, or a "Media Watch clips" page this person made before keys were used.
    public const string ClipsFolderSystemKey = "media-watch-clips";

    public Task<Guid> ClipToSentinelAsync(string username, NewsItemDto item, CancellationToken ct = default) => ClipToSentinelAsync(username, item, null, ct);

    public async Task<Guid> ClipToSentinelAsync(string username, NewsItemDto item, ClipExtras? extras, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var saved = await FindOrSaveAsync(db, username, item, ct);
        var now = timeProvider.GetLocalNow();
        if (saved.ClippedWikiPageId is { } existingId
            && await db.WikiPages.FirstOrDefaultAsync(p => p.Id == existingId && p.TrashedAt == null, ct) is { } existing)
        {
            await db.SaveChangesAsync(ct);
            // Clipped before: new notes and highlights are added to its page.
            var addition = NotesMarkdown(extras, now);
            if (addition.Length > 0)
            {
                var blocks = WikiBlockJson.ParseBlocks(existing.BlocksJson).ToList();
                blocks.AddRange(WikiBlockJson.FromMarkdown(addition));
                await wikiService.SavePageAsync(new WikiPageEditorModel
                {
                    WikiPageId = existing.Id,
                    Title = existing.Title,
                    Slug = existing.Slug,
                    Icon = existing.Icon,
                    CoverImageUrl = existing.CoverImageUrl,
                    ParentWikiPageId = existing.ParentWikiPageId,
                    IsFullWidth = existing.IsFullWidth,
                    FontStyle = existing.FontStyle,
                    ExpectedContentVersion = existing.ContentVersion,
                    BaseBlocksJson = existing.BlocksJson,
                    BlocksJson = WikiBlockJson.Serialize(blocks)
                }, username, cancellationToken: ct);
            }
            return existingId;
        }

        var folderId = await EnsureClipsFolderAsync(db, username, ct);
        // The whole readable article (6. full-text clips); the summary alone when it can't be read.
        string? articleMarkdown = null;
        if (articleReader is not null)
        {
            try
            {
                var article = await articleReader.ReadAsync(item.Url, item.Title, ct);
                if (article.Readable && article.Markdown.Length > 0) articleMarkdown = article.Markdown;
                if (article.Readable && article.Minutes > 0 && saved.ReadingMinutes is null) saved.ReadingMinutes = article.Minutes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Clip went ahead without the article text for {Url}", item.Url);
            }
        }

        var page = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = Truncate(item.Title, 200),
            Icon = "📰",
            ParentWikiPageId = folderId,
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(ClipMarkdown(item, username, now, articleMarkdown, extras)))
        }, username, cancellationToken: ct);

        saved.ClippedWikiPageId = page.Id;
        await db.SaveChangesAsync(ct);
        return page.Id;
    }

    private async Task<Guid> EnsureClipsFolderAsync(IAppDbContext db, string username, CancellationToken ct)
    {
        var folder = await SentinelPersonalPages.FindAsync(db, ClipsFolderSystemKey, username, ct);
        if (folder is null)
        {
            // A folder this person made before personal keys: adopt it.
            var legacy = (await db.WikiPages.Where(p => p.Title == ClipsParentTitle && p.ParentWikiPageId == null && p.TrashedAt == null && p.SystemKey == null)
                    .ToListAsync(ct))
                .FirstOrDefault(p => string.Equals(p.CreatedBy, username, StringComparison.OrdinalIgnoreCase));
            if (legacy is not null)
            {
                legacy.SystemKey = SentinelPersonalPages.KeyFor(ClipsFolderSystemKey, username);
                await db.SaveChangesAsync(ct);
                return legacy.Id;
            }
            var created = await wikiService.SavePageAsync(new WikiPageEditorModel
            {
                Title = ClipsParentTitle,
                Icon = "📰",
                BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown("Articles clipped from Media Watch. Each page links to the original."))
            }, username, cancellationToken: ct);
            var page = await db.WikiPages.FirstAsync(p => p.Id == created.Id, ct);
            page.SystemKey = SentinelPersonalPages.KeyFor(ClipsFolderSystemKey, username);
            await db.SaveChangesAsync(ct);
            return page.Id;
        }
        return folder.Id;
    }

    public static string NotesMarkdown(ClipExtras? extras, DateTimeOffset nowLocal)
    {
        if (extras is null) return string.Empty;
        var highlights = extras.Highlights.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        if (string.IsNullOrWhiteSpace(extras.Notes) && highlights.Count == 0) return string.Empty;
        var md = new StringBuilder();
        md.AppendLine($"## My notes ({nowLocal.ToGwsDateTime()})");
        md.AppendLine();
        foreach (var highlight in highlights)
        {
            md.AppendLine("> " + highlight.Trim().Replace("\n", " "));
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(extras.Notes)) { md.AppendLine(extras.Notes.Trim()); md.AppendLine(); }
        return md.ToString();
    }

    // ---- Reading queue (20-11-26) -----------------------------------------------------------

    public async Task SetFinishedAsync(string username, string url, bool finished, CancellationToken ct = default)
    {
        var hash = NewsStoryClusterer.UrlHash(url);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var saved = await db.SavedNewsArticles.FirstOrDefaultAsync(s => s.Username == username && s.UrlHash == hash, ct);
        if (saved is null) return;
        saved.FinishedAt = finished ? timeProvider.GetUtcNow() : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetReadingMinutesAsync(string username, string url, int minutes, CancellationToken ct = default)
    {
        if (minutes <= 0) return;
        var hash = NewsStoryClusterer.UrlHash(url);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var saved = await db.SavedNewsArticles.FirstOrDefaultAsync(s => s.Username == username && s.UrlHash == hash, ct);
        if (saved is null || saved.ReadingMinutes == minutes) return;
        saved.ReadingMinutes = minutes;
        await db.SaveChangesAsync(ct);
    }

    // ---- Weekly reading page (20-11-26) ------------------------------------------------------

    public async Task<Guid> BuildReadingDigestAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow();
        var since = now.AddDays(-7).ToUnixTimeSeconds();
        var technical = await db.WatchedTopics.AsNoTracking().Where(t => t.TopicType == WatchedTopicTypes.Technical)
            .Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var names = technical.ToDictionary(t => t.Id, t => t.Name);
        var ids = names.Keys.ToList();
        var items = await db.NewsItems.AsNoTracking()
            .Where(n => n.TopicId != null && ids.Contains(n.TopicId.Value) && n.FetchedAtUnixSeconds >= since)
            .ToListAsync(ct);
        var settings = await db.NewsWatchSettings.AsNoTracking().OrderBy(row => row.Id).FirstOrDefaultAsync(ct) ?? new NewsWatchSettings();
        var mutedKeywords = ReadingFilters.Lines(settings.MutedKeywords);
        var mutedSources = ReadingFilters.Lines(settings.MutedSources);
        var read = (await db.NewsReadMarks.AsNoTracking().Where(m => m.Username == username).Select(m => m.UrlHash).ToListAsync(ct)).ToHashSet();
        var candidates = items
            .Where(n => !ReadingFilters.IsMuted(n.Title, n.Description, n.Source, n.Url, mutedKeywords, mutedSources))
            .Select(n => new ReadingDigest.Candidate(n.Title, n.Url, n.Source, names.GetValueOrDefault(n.TopicId!.Value, ""), n.PublishedAt,
                n.CommunityScore, n.ReadingMinutes, string.IsNullOrWhiteSpace(n.OllamaSummary) ? n.Description : n.OllamaSummary));
        var picks = ReadingDigest.Pick(candidates, read, NewsStoryClusterer.UrlHash, now);
        var markdown = ReadingDigest.Markdown(picks, timeProvider.GetLocalNow());
        // 6. Old highlights brought back (20-11-26): three from clips at least a week old.
        var resurfaced = ReadingLibrary.Resurface(await ClipTextsAsync(db, username, ct), now);
        if (resurfaced.Count > 0)
        {
            markdown += "\n## From your highlights\n\n" + string.Join("\n\n", resurfaced.Select(r => $"> {r.Quote}\n\n*{r.Title}*")) + "\n";
        }
        var blocks = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(markdown));

        var page = await SentinelPersonalPages.FindAsync(db, ReadingDigest.PageSystemKey, username, ct);
        if (page is null)
        {
            var created = await wikiService.SavePageAsync(new WikiPageEditorModel
            {
                Title = ReadingDigest.PageTitle,
                Icon = "📚",
                BlocksJson = blocks
            }, username, cancellationToken: ct);
            var stored = await db.WikiPages.FirstAsync(p => p.Id == created.Id, ct);
            stored.SystemKey = SentinelPersonalPages.KeyFor(ReadingDigest.PageSystemKey, username);
            await db.SaveChangesAsync(ct);
            return stored.Id;
        }
        await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id,
            Title = page.Title,
            Slug = page.Slug,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            ParentWikiPageId = page.ParentWikiPageId,
            IsFullWidth = page.IsFullWidth,
            FontStyle = page.FontStyle,
            ExpectedContentVersion = page.ContentVersion,
            BaseBlocksJson = page.BlocksJson,
            BlocksJson = blocks
        }, username, cancellationToken: ct);
        return page.Id;
    }

    // Someone's clip pages as text, quote blocks marked "> ", for highlights and library search.
    private static async Task<List<(string Title, string Markdown, DateTimeOffset ClippedAt, Guid PageId)>> ClipPagesAsync(IAppDbContext db, string username, CancellationToken ct)
    {
        var folder = await SentinelPersonalPages.FindAsync(db, ClipsFolderSystemKey, username, ct);
        if (folder is null) return [];
        var pages = await db.WikiPages.AsNoTracking().Where(p => p.ParentWikiPageId == folder.Id)
            .Select(p => new { p.Id, p.Title, p.BlocksJson, p.CreatedAt }).ToListAsync(ct);
        return pages.Select(p => (p.Title, string.Join('\n', WikiBlockJson.ParseBlocks(p.BlocksJson)
                .Select(b => b.Type == WikiBlockTypes.Quote ? "> " + b.PlainText : b.PlainText)), p.CreatedAt, p.Id))
            .ToList();
    }

    private static async Task<IEnumerable<(string Title, string Markdown, DateTimeOffset ClippedAt)>> ClipTextsAsync(IAppDbContext db, string username, CancellationToken ct) =>
        (await ClipPagesAsync(db, username, ct)).Select(c => (c.Title, c.Markdown, c.ClippedAt));

    public async Task<IReadOnlyList<LibraryHit>> SearchLibraryAsync(string username, string query, CancellationToken ct = default)
    {
        if (ReadingLibrary.Words(query).Count == 0) return [];
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var hits = new List<LibraryHit>();
        var saved = await db.SavedNewsArticles.AsNoTracking().Where(s => s.Username == username).ToListAsync(ct);
        var clips = await ClipPagesAsync(db, username, ct);
        var clipText = clips.ToDictionary(c => c.PageId, c => c.Markdown);
        foreach (var s in saved)
        {
            var text = s.ClippedWikiPageId is { } pageId ? clipText.GetValueOrDefault(pageId) : null;
            if (!ReadingLibrary.Matches(query, s.Title, s.Summary, s.Source, s.TopicName, text)) continue;
            hits.Add(new LibraryHit(s.Title, s.Url, s.Source, s.ClippedWikiPageId is null ? LibraryWhere.Queued : LibraryWhere.Clipped,
                ReadingLibrary.Snippet(text, query) ?? ReadingLibrary.Snippet(s.Summary, query), s.ClippedWikiPageId, s.CreatedAt));
        }
        // Clips whose queued copy was removed still count.
        var clippedIds = saved.Where(s => s.ClippedWikiPageId is not null).Select(s => s.ClippedWikiPageId!.Value).ToHashSet();
        foreach (var clip in clips.Where(c => !clippedIds.Contains(c.PageId) && ReadingLibrary.Matches(query, c.Title, c.Markdown)))
            hits.Add(new LibraryHit(clip.Title, $"/admin/sentinel?page={clip.PageId}", "Sentinel", LibraryWhere.Clipped,
                ReadingLibrary.Snippet(clip.Markdown, query), clip.PageId, clip.ClippedAt));
        // Articles you've read that are still in a feed.
        var savedHashes = saved.Select(s => s.UrlHash).ToHashSet();
        var readHashes = (await db.NewsReadMarks.AsNoTracking().Where(m => m.Username == username).Select(m => new { m.UrlHash, m.ReadAt }).ToListAsync(ct))
            .Where(m => !savedHashes.Contains(m.UrlHash)).ToDictionary(m => m.UrlHash, m => m.ReadAt);
        if (readHashes.Count > 0)
        {
            foreach (var item in (await db.NewsItems.AsNoTracking().ToListAsync(ct))
                         .Where(n => readHashes.ContainsKey(NewsStoryClusterer.UrlHash(n.Url)) && ReadingLibrary.Matches(query, n.Title, n.Description, n.OllamaSummary, n.Source))
                         .DistinctBy(n => NewsStoryClusterer.UrlHash(n.Url)))
                hits.Add(new LibraryHit(item.Title, item.Url, item.Source, LibraryWhere.Read, ReadingLibrary.Snippet(item.Description, query),
                    null, readHashes[NewsStoryClusterer.UrlHash(item.Url)]));
        }
        return hits.OrderByDescending(h => h.At ?? DateTimeOffset.MinValue).Take(ReadingLibrary.MaxHits).ToList();
    }

    public async Task<IReadOnlyList<string>> ListFollowedAuthorsAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return (await db.FollowedNewsAuthors.AsNoTracking().Where(a => a.Username == username).Select(a => a.Name).ToListAsync(ct))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public const int MaxFollowedAuthors = 100;

    public async Task FollowAuthorAsync(string username, string name, bool follow, CancellationToken ct = default)
    {
        var clean = NewsAuthors.Clean(name) ?? throw new ArgumentException("That byline isn't a writer's name that can be followed.");
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var existing = (await db.FollowedNewsAuthors.Where(a => a.Username == username).ToListAsync(ct))
            .Where(a => string.Equals(a.Name, clean, StringComparison.OrdinalIgnoreCase)).ToList();
        if (follow && existing.Count == 0)
        {
            if (await db.FollowedNewsAuthors.CountAsync(a => a.Username == username, ct) >= MaxFollowedAuthors)
                throw new ArgumentException($"You can follow up to {MaxFollowedAuthors} writers.");
            db.FollowedNewsAuthors.Add(new FollowedNewsAuthor { Username = username, Name = clean, CreatedBy = username, CreatedAt = timeProvider.GetUtcNow() });
        }
        if (!follow) db.FollowedNewsAuthors.RemoveRange(existing);
        await db.SaveChangesAsync(ct);
    }

    // Due on Mondays from 07:00 local, once a week.
    public static bool ReadingDigestDue(DateTimeOffset? lastRun, DateTimeOffset nowLocal)
    {
        var daysSinceMonday = ((int)nowLocal.DayOfWeek + 6) % 7;
        var mondaySeven = new DateTimeOffset(nowLocal.Date.AddDays(-daysSinceMonday).AddHours(7), nowLocal.Offset);
        // A first run waits for a Monday, so a deploy midweek doesn't build everyone a page.
        return nowLocal >= mondaySeven && (lastRun is null ? nowLocal.DayOfWeek == DayOfWeek.Monday : lastRun.Value < mondaySeven);
    }

    public async Task<int> RunReadingDigestsIfDueAsync(CancellationToken ct = default)
    {
        List<string> readers;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            var settings = await db.NewsWatchSettings.OrderBy(row => row.Id).FirstOrDefaultAsync(ct);
            if (!ReadingDigestDue(settings?.LastReadingDigestAt, timeProvider.GetLocalNow())) return 0;
            if (settings is null)
            {
                settings = new NewsWatchSettings { CreatedAt = timeProvider.GetUtcNow() };
                db.NewsWatchSettings.Add(settings);
            }
            settings.LastReadingDigestAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
            // Everyone who opened or saved an article in the last 30 days.
            var cutoff = timeProvider.GetUtcNow().AddDays(-30);
            var marks = (await db.NewsReadMarks.AsNoTracking().Select(m => new { m.Username, m.ReadAt }).ToListAsync(ct))
                .Where(m => m.ReadAt >= cutoff).Select(m => m.Username);
            var saves = (await db.SavedNewsArticles.AsNoTracking().Select(m => new { m.Username, m.CreatedAt }).ToListAsync(ct))
                .Where(m => m.CreatedAt >= cutoff).Select(m => m.Username);
            readers = marks.Concat(saves).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        var built = 0;
        foreach (var reader in readers)
        {
            try
            {
                await BuildReadingDigestAsync(reader, ct);
                built++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The weekly reading page couldn't be built for {User}", reader);
            }
        }
        return built;
    }

    public static string ClipMarkdown(NewsItemDto item, string username, DateTimeOffset clippedAt) =>
        ClipMarkdown(item, username, clippedAt, null, null);

    public static string ClipMarkdown(NewsItemDto item, string username, DateTimeOffset clippedAt, string? articleMarkdown, ClipExtras? extras)
    {
        var md = new StringBuilder();
        md.AppendLine($"**{Escape(item.Source)}**{(item.PublishedAt is { } published ? $" · {published:dd-MM-yy}" : "")} · [Read the original]({item.Url})");
        md.AppendLine();
        if (!string.IsNullOrWhiteSpace(item.OllamaSummary))
        {
            md.AppendLine($"> {Escape(item.OllamaSummary)}");
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(articleMarkdown))
        {
            md.Append(NotesMarkdown(extras, clippedAt));
            md.AppendLine("## Article");
            md.AppendLine();
            md.AppendLine(articleMarkdown.Trim());
            md.AppendLine();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(item.Description)) { md.AppendLine(Escape(item.Description)); md.AppendLine(); }
            md.Append(NotesMarkdown(extras, clippedAt));
        }
        md.AppendLine($"Clipped from Media Watch ({Escape(item.TopicName)}) by {username} on {clippedAt:dd-MM-yy}.");
        return md.ToString();
    }

    // ---- Trends ---------------------------------------------------------------------------

    public async Task<NewsTrendsView> GetTrendsAsync(int days = 14, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 7, 60);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var range = Enumerable.Range(0, days).Select(i => today.AddDays(i - days + 1)).ToList();
        var first = range[0].ToString("yyyy-MM-dd");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var topics = await db.WatchedTopics.AsNoTracking().ToListAsync(ct);
        var rows = (await db.NewsTrendDays.AsNoTracking().ToListAsync(ct))
            .Where(r => string.CompareOrdinal(r.Day, first) >= 0)
            .ToList();

        var keys = new List<(string Key, string Name, string Color)> { ("top", "All News", "#64748b") };
        keys.AddRange(topics.OrderBy(t => t.Name).Select(t => (t.Id.ToString(), t.Name, t.ColorHex)));

        var trends = keys.Select(k =>
        {
            var byDay = rows.Where(r => r.TopicKey == k.Key)
                .ToDictionary(r => DateOnly.ParseExact(r.Day, "yyyy-MM-dd"), r => r);
            var counts = range.Select(d => byDay.TryGetValue(d, out var r) ? r.ArticleCount : 0).ToList();
            var terms = byDay.Select(kv => (kv.Key, JsonSerializer.Deserialize<Dictionary<string, int>>(kv.Value.TermCountsJson) ?? [])).ToList();
            return new TopicTrend(k.Key, k.Name, k.Color, counts, NewsTrendTerms.Rising(terms, today));
        }).ToList();

        return new NewsTrendsView(range, trends);
    }

    // ---- Feed discovery -------------------------------------------------------------------

    public async Task<IReadOnlyList<DiscoveredFeed>> DiscoverFeedsAsync(string siteUrl, CancellationToken ct = default)
    {
        siteUrl = siteUrl.Trim();
        if (!siteUrl.Contains("://", StringComparison.Ordinal)) siteUrl = "https://" + siteUrl;
        if (!Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a website address like example.com.");
        if (!await IsPublicHostAsync(site.Host, ct))
            throw new ArgumentException("Only public websites can be searched for feeds.");

        var found = new List<DiscoveredFeed>();
        var candidates = new List<string>();
        var (body, isFeed) = await TryFetchAsync(site.ToString(), ct);
        if (body is null) throw new ArgumentException($"Couldn't load {site.Host}.");

        if (isFeed && TryParseFeed(site.ToString(), body) is { } direct) return [direct];
        candidates.AddRange(FindAlternateFeedLinks(body, site));
        candidates.AddRange(CommonFeedPaths.Select(path => new Uri(site, path).ToString()));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (found.Count >= 5) break;
            var (feedBody, _) = await TryFetchAsync(candidate, ct);
            if (feedBody is not null && TryParseFeed(candidate, feedBody) is { } feed
                && !found.Any(f => string.Equals(f.Title, feed.Title, StringComparison.OrdinalIgnoreCase) && f.ItemCount == feed.ItemCount))
            {
                found.Add(feed);
            }
        }
        return found;
    }

    // The server fetches whatever an admin types here, so it must not be pointed at its own
    // network (localhost, the Docker network, cloud metadata at 169.254.169.254...).
    public static async Task<bool> IsPublicHostAsync(string host, CancellationToken ct)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct); }
        catch (System.Net.Sockets.SocketException) { return false; }
        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6None)) return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return true;
        var b = ip.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }

    private async Task<(string? Body, bool LooksLikeFeed)> TryFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || !await IsPublicHostAsync(target.Host, ct)) return (null, false);
            using var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (null, false);
            var body = await response.Content.ReadAsStringAsync(ct);
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var head = body.TrimStart()[..Math.Min(200, body.TrimStart().Length)];
            return (body, type.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<rss", StringComparison.OrdinalIgnoreCase)
                || head.StartsWith("<feed", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Feed discovery fetch failed for {Url}", url);
            return (null, false);
        }
    }

    private static DiscoveredFeed? TryParseFeed(string url, string body)
    {
        try
        {
            var feed = FeedReader.ReadFromString(body);
            return feed.Items.Count == 0 && string.IsNullOrWhiteSpace(feed.Title)
                ? null
                : new DiscoveredFeed(url, string.IsNullOrWhiteSpace(feed.Title) ? new Uri(url).Host : feed.Title.Trim(), feed.Items.Count);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> FindAlternateFeedLinks(string html, Uri baseUri)
    {
        var links = new List<string>();
        foreach (Match tag in LinkTagPattern().Matches(html))
        {
            var attrs = tag.Value;
            if (!Regex.IsMatch(attrs, @"rel\s*=\s*[""']?alternate", RegexOptions.IgnoreCase)) continue;
            if (!Regex.IsMatch(attrs, @"type\s*=\s*[""']?application/(rss|atom)\+xml", RegexOptions.IgnoreCase)) continue;
            var href = Regex.Match(attrs, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (href.Success && Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href.Groups[1].Value), out var absolute)
                && absolute.Scheme is "http" or "https")
            {
                links.Add(absolute.ToString());
            }
        }
        return links;
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagPattern();

    // ---- Settings & digest ----------------------------------------------------------------

    public async Task<NewsWatchSettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var s = await db.NewsWatchSettings.AsNoTracking().OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(ct) ?? new NewsWatchSettings();
        return new NewsWatchSettingsView(s.RetentionHours, s.DigestEnabled, s.DigestRecipient, s.DigestFrequency,
            s.DigestHourLocal, s.DigestDayOfWeek, s.LastDigestSentAt, mailTransport.Describe(smtpOptions.Value).CanSend,
            s.MutedKeywords, s.MutedSources);
    }

    public async Task SaveSettingsAsync(NewsWatchSettingsView settings, CancellationToken ct = default)
    {
        if (!NewsRetention.AllowedHours.Contains(settings.RetentionHours))
            throw new ArgumentException("Pick one of the offered retention periods.");
        var recipient = string.IsNullOrWhiteSpace(settings.DigestRecipient) ? null : settings.DigestRecipient.Trim();
        if (settings.DigestEnabled && !IsEmailAddress(recipient))
            throw new ArgumentException("Enter a valid email address for the digest.");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.NewsWatchSettings.OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new NewsWatchSettings { CreatedAt = timeProvider.GetUtcNow() };
            db.NewsWatchSettings.Add(row);
        }
        row.RetentionHours = settings.RetentionHours;
        row.DigestEnabled = settings.DigestEnabled;
        row.DigestRecipient = recipient;
        row.DigestFrequency = settings.DigestFrequency == NewsDigestFrequencies.Weekly ? NewsDigestFrequencies.Weekly : NewsDigestFrequencies.Daily;
        row.DigestHourLocal = Math.Clamp(settings.DigestHourLocal, 0, 23);
        row.DigestDayOfWeek = Math.Clamp(settings.DigestDayOfWeek, 0, 6);
        row.MutedKeywords = Truncate(string.Join('\n', ReadingFilters.Lines(settings.MutedKeywords)), 4000);
        row.MutedSources = Truncate(string.Join('\n', ReadingFilters.Lines(settings.MutedSources)), 4000);
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    // MimeKit accepts a bare local part ("bob") as an address; a digest needs a real mailbox.
    public static bool IsEmailAddress(string? value) =>
        value is not null && MailboxAddress.TryParse(value, out var address) && address.Address.Contains('@')
        && address.Address.IndexOf('@') > 0 && address.Address.IndexOf('@') < address.Address.Length - 1;

    public static bool DigestDue(NewsWatchSettings s, DateTimeOffset nowLocal)
    {
        if (!s.DigestEnabled || nowLocal.Hour < s.DigestHourLocal) return false;
        if (s.DigestFrequency == NewsDigestFrequencies.Weekly && (int)nowLocal.DayOfWeek != s.DigestDayOfWeek) return false;
        if (s.LastDigestSentAt is not { } last) return true;
        return last.ToOffset(nowLocal.Offset).Date < nowLocal.Date;
    }

    public async Task<NewsDigestResult> SendDigestAsync(bool force, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var settings = await db.NewsWatchSettings.OrderBy(oneRow => oneRow.Id).FirstOrDefaultAsync(ct);
        if (settings is null || string.IsNullOrWhiteSpace(settings.DigestRecipient) || !MailboxAddress.TryParse(settings.DigestRecipient, out var to))
            return new(false, "Set a digest email address first.");
        var now = timeProvider.GetUtcNow();
        if (!force && !DigestDue(settings, now.ToLocalTime())) return new(false, "Not due yet.");
        var smtp = smtpOptions.Value;
        if (!mailTransport.Describe(smtp).CanSend || !MailboxAddress.TryParse(smtp.FromAddress, out var from))
            return new(false, "Email delivery isn't set up on this server (Settings > Email).");

        var window = settings.DigestFrequency == NewsDigestFrequencies.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
        var since = (settings.LastDigestSentAt is { } last && now - last < window ? last : now - window).ToUnixTimeSeconds();
        var topics = await db.WatchedTopics.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        // Muted keywords and sites stay out of the digest too (20-11-26).
        var mutedKeywords = ReadingFilters.Lines(settings.MutedKeywords);
        var mutedSources = ReadingFilters.Lines(settings.MutedSources);
        var items = (await db.NewsItems.AsNoTracking().Where(n => n.FetchedAtUnixSeconds >= since).ToListAsync(ct))
            .Where(n => !ReadingFilters.IsMuted(n.Title, n.Description, n.Source, n.Url, mutedKeywords, mutedSources))
            .ToList();

        var sections = new List<(string Name, IReadOnlyList<NewsStory> Stories)>();
        foreach (var topic in topics.OrderBy(t => t.Name))
        {
            var stories = Stories(items.Where(i => i.TopicId == topic.Id), topic.Name, now, topic.TopicType == WatchedTopicTypes.Technical);
            if (stories.Count > 0) sections.Add((topic.Name, stories));
        }
        var top = Stories(items.Where(i => i.TopicId == null), "Top News", now);
        if (top.Count > 0) sections.Add(("Top News", top));

        // Short reads waiting in the queue of whoever set the digest up.
        var owner = settings.UpdatedBy ?? settings.CreatedBy;
        var quickReads = string.IsNullOrWhiteSpace(owner) || owner == "system" ? [] : QuickReads(await ListSavedAsync(owner, ct));

        if (sections.Count == 0)
        {
            settings.LastDigestSentAt = now;
            await db.SaveChangesAsync(ct);
            return new(false, "No new articles since the last digest.");
        }

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = $"[Media Watch] {(settings.DigestFrequency == NewsDigestFrequencies.Weekly ? "Weekly" : "Daily")} digest - {sections.Sum(s => s.Stories.Count)} stories";
        message.Body = new BodyBuilder { TextBody = DigestText(sections, quickReads), HtmlBody = DigestHtml(sections, quickReads) }.ToMessageBody();
        await mailTransport.SendAsync(message, smtp, "media-watch-digest", ct);
        settings.LastDigestSentAt = now;
        await db.SaveChangesAsync(ct);
        return new(true, $"Digest sent to {to.Address}.");
    }

    // Technical topics lead with the best reads (discussion and depth); others with the stories
    // most outlets carried, best reads breaking ties (20-11-26).
    public static IReadOnlyList<NewsStory> Stories(IEnumerable<NewsItem> items, string topicName, DateTimeOffset now, bool technical = false)
    {
        var stories = NewsStoryClusterer.Cluster(items.Select(n => new NewsItemDto(n.Id, n.TopicId, topicName, "", n.Title, n.Url, n.Source,
            n.PublishedAt, n.Description, n.OllamaSummary, n.FetchedAt, n.ImageUrl, n.CommunityScore, n.ReadingMinutes)).ToList(), now);
        double Best(NewsStory s) => s.OtherReports.Prepend(s.Lead).Max(r => TechReadingRank.Score(r.CommunityScore, r.ReadingMinutes, r.PublishedAt ?? r.FetchedAt, now));
        var ordered = technical
            ? stories.OrderByDescending(Best).ThenByDescending(s => s.OutletCount)
            : stories.OrderByDescending(s => s.OutletCount).ThenByDescending(Best);
        return ordered.Take(DigestStoriesPerTopic).ToList();
    }

    public const int DigestQuickReads = 3;
    public const int QuickReadMinutes = 10;

    // Unfinished queued articles that take ten minutes or less, shortest first.
    public static IReadOnlyList<SavedArticleView> QuickReads(IEnumerable<SavedArticleView> saved) =>
        saved.Where(s => s.FinishedAt is null && s.ReadingMinutes is > 0 and <= QuickReadMinutes)
            .OrderBy(s => s.ReadingMinutes).ThenByDescending(s => s.SavedAt)
            .Take(DigestQuickReads).ToList();

    public static string DigestText(IReadOnlyList<(string Name, IReadOnlyList<NewsStory> Stories)> sections, IReadOnlyList<SavedArticleView>? quickReads = null)
    {
        var text = new StringBuilder();
        if (quickReads is { Count: > 0 })
        {
            text.AppendLine($"{quickReads.Count} QUEUED READS UNDER {QuickReadMinutes} MINUTES");
            foreach (var read in quickReads) text.AppendLine($"- {read.Title} ({read.ReadingMinutes} min, {read.Source})\n  {read.Url}");
            text.AppendLine();
        }
        foreach (var (name, stories) in sections)
        {
            text.AppendLine(name.ToUpperInvariant());
            foreach (var story in stories)
            {
                text.AppendLine($"- {story.Lead.Title} ({story.Lead.Source}{(story.OutletCount > 1 ? $" + {story.OutletCount - 1} more" : "")})");
                var take = string.IsNullOrWhiteSpace(story.Lead.OllamaSummary) ? null : story.Lead.OllamaSummary;
                if (take is not null) text.AppendLine($"  {take}");
                text.AppendLine($"  {story.Lead.Url}");
            }
            text.AppendLine();
        }
        text.AppendLine("Open Media Watch for the full feed.");
        return text.ToString();
    }

    public static string DigestHtml(IReadOnlyList<(string Name, IReadOnlyList<NewsStory> Stories)> sections, IReadOnlyList<SavedArticleView>? quickReads = null)
    {
        static string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);
        var html = new StringBuilder("<div style=\"font-family:system-ui,sans-serif;font-size:14px;color:#1f2937\">");
        if (quickReads is { Count: > 0 })
        {
            html.Append($"<h3 style=\"margin:18px 0 6px\">{quickReads.Count} queued reads under {QuickReadMinutes} minutes</h3>");
            foreach (var read in quickReads)
                html.Append($"<p style=\"margin:0 0 10px\"><a href=\"{E(read.Url)}\" style=\"font-weight:600\">{E(read.Title)}</a><br><span style=\"color:#64748b\">{read.ReadingMinutes} min · {E(read.Source)}</span></p>");
        }
        foreach (var (name, stories) in sections)
        {
            html.Append($"<h3 style=\"margin:18px 0 6px\">{E(name)}</h3>");
            foreach (var story in stories)
            {
                html.Append("<p style=\"margin:0 0 10px\">")
                    .Append($"<a href=\"{E(story.Lead.Url)}\" style=\"font-weight:600\">{E(story.Lead.Title)}</a><br>")
                    .Append($"<span style=\"color:#64748b\">{E(story.Lead.Source)}{(story.OutletCount > 1 ? $" + {story.OutletCount - 1} more outlet(s)" : "")}</span>");
                if (!string.IsNullOrWhiteSpace(story.Lead.OllamaSummary)) html.Append($"<br>{E(story.Lead.OllamaSummary)}");
                html.Append("</p>");
            }
        }
        html.Append("<p style=\"color:#64748b\">Open Media Watch for the full feed.</p></div>");
        return html.ToString();
    }

    private static string Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    // Feed text is untrusted; keep Markdown links/images from being smuggled into a Sentinel page.
    private static string Escape(string? value) => (value ?? string.Empty).Replace("[", "\\[").Replace("]", "\\]").Replace("\n", " ");
}
