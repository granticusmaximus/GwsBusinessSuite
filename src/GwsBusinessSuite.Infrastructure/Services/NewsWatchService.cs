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
    ILogger<NewsWatchService> logger) : INewsWatchService
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
        var retention = await db.NewsWatchSettings.AsNoTracking().Select(x => (int?)x.RetentionHours).FirstOrDefaultAsync(ct) ?? NewsRetention.DefaultHours;
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
            .Select(s => new SavedArticleView(s.Id, s.Url, s.Title, s.Source, s.TopicName, s.PublishedAt, s.Summary, s.CreatedAt, s.ClippedWikiPageId))
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
            CreatedBy = username,
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.SavedNewsArticles.Add(saved);
        return saved;
    }

    // ---- Sentinel clips -------------------------------------------------------------------

    public async Task<Guid> ClipToSentinelAsync(string username, NewsItemDto item, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var saved = await FindOrSaveAsync(db, username, item, ct);
        if (saved.ClippedWikiPageId is { } existingId
            && await db.WikiPages.AnyAsync(p => p.Id == existingId && p.TrashedAt == null, ct))
        {
            await db.SaveChangesAsync(ct);
            return existingId;
        }

        var parentId = await db.WikiPages.AsNoTracking()
            .Where(p => p.Title == ClipsParentTitle && p.ParentWikiPageId == null && p.TrashedAt == null)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (parentId is null)
        {
            var parent = await wikiService.SavePageAsync(new WikiPageEditorModel
            {
                Title = ClipsParentTitle,
                Icon = "📰",
                BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown("Articles clipped from Media Watch. Each page links to the original."))
            }, username, cancellationToken: ct);
            parentId = parent.Id;
        }

        var page = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = Truncate(item.Title, 200),
            Icon = "📰",
            ParentWikiPageId = parentId,
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(ClipMarkdown(item, username, timeProvider.GetUtcNow())))
        }, username, cancellationToken: ct);

        saved.ClippedWikiPageId = page.Id;
        await db.SaveChangesAsync(ct);
        return page.Id;
    }

    public static string ClipMarkdown(NewsItemDto item, string username, DateTimeOffset clippedAt)
    {
        var md = new StringBuilder();
        md.AppendLine($"**{Escape(item.Source)}**{(item.PublishedAt is { } published ? $" · {published:yyyy-MM-dd}" : "")} · [Read the original]({item.Url})");
        md.AppendLine();
        if (!string.IsNullOrWhiteSpace(item.OllamaSummary))
        {
            md.AppendLine($"> {Escape(item.OllamaSummary)}");
            md.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(item.Description)) { md.AppendLine(Escape(item.Description)); md.AppendLine(); }
        md.AppendLine($"Clipped from Media Watch ({Escape(item.TopicName)}) by {username} on {clippedAt:yyyy-MM-dd}.");
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
        var s = await db.NewsWatchSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new NewsWatchSettings();
        return new NewsWatchSettingsView(s.RetentionHours, s.DigestEnabled, s.DigestRecipient, s.DigestFrequency,
            s.DigestHourLocal, s.DigestDayOfWeek, s.LastDigestSentAt, mailTransport.Describe(smtpOptions.Value).CanSend);
    }

    public async Task SaveSettingsAsync(NewsWatchSettingsView settings, CancellationToken ct = default)
    {
        if (!NewsRetention.AllowedHours.Contains(settings.RetentionHours))
            throw new ArgumentException("Pick one of the offered retention periods.");
        var recipient = string.IsNullOrWhiteSpace(settings.DigestRecipient) ? null : settings.DigestRecipient.Trim();
        if (settings.DigestEnabled && !IsEmailAddress(recipient))
            throw new ArgumentException("Enter a valid email address for the digest.");

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.NewsWatchSettings.FirstOrDefaultAsync(ct);
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
        var settings = await db.NewsWatchSettings.FirstOrDefaultAsync(ct);
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
        var items = await db.NewsItems.AsNoTracking().Where(n => n.FetchedAtUnixSeconds >= since).ToListAsync(ct);

        var sections = new List<(string Name, IReadOnlyList<NewsStory> Stories)>();
        foreach (var topic in topics.OrderBy(t => t.Name))
        {
            var stories = Stories(items.Where(i => i.TopicId == topic.Id), topic.Name, now);
            if (stories.Count > 0) sections.Add((topic.Name, stories));
        }
        var top = Stories(items.Where(i => i.TopicId == null), "Top News", now);
        if (top.Count > 0) sections.Add(("Top News", top));

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
        message.Body = new BodyBuilder { TextBody = DigestText(sections), HtmlBody = DigestHtml(sections) }.ToMessageBody();
        await mailTransport.SendAsync(message, smtp, "media-watch-digest", ct);
        settings.LastDigestSentAt = now;
        await db.SaveChangesAsync(ct);
        return new(true, $"Digest sent to {to.Address}.");
    }

    private static IReadOnlyList<NewsStory> Stories(IEnumerable<NewsItem> items, string topicName, DateTimeOffset now) =>
        NewsStoryClusterer.Cluster(items.Select(n => new NewsItemDto(n.Id, n.TopicId, topicName, "", n.Title, n.Url, n.Source,
                n.PublishedAt, n.Description, n.OllamaSummary, n.FetchedAt, n.ImageUrl)).ToList(), now)
            .OrderByDescending(s => s.OutletCount)
            .ThenByDescending(s => s.Lead.PublishedAt ?? s.Lead.FetchedAt)
            .Take(DigestStoriesPerTopic)
            .ToList();

    public static string DigestText(IReadOnlyList<(string Name, IReadOnlyList<NewsStory> Stories)> sections)
    {
        var text = new StringBuilder();
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

    public static string DigestHtml(IReadOnlyList<(string Name, IReadOnlyList<NewsStory> Stories)> sections)
    {
        static string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);
        var html = new StringBuilder("<div style=\"font-family:system-ui,sans-serif;font-size:14px;color:#1f2937\">");
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
