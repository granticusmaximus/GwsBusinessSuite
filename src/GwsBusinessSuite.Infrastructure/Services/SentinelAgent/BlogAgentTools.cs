using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AffiliateSuggestions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.ContentCalendar;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Blog posts and the content calendar for the agent (Phase D, 30-10-26). Posts are Articles,
// which the post editor and its API write directly (there's no article service), so these do the
// same with the editor's defaults. Posts are ContentAccess like the editor; it only edits Drafts,
// and publishing waits for a Confirm because it goes live and alert subscribers get an email.
// The content calendar is AdminOnly like its page.
public sealed class BlogAgentTools(
    IAppDbContext db,
    ISiteSettingsService settings,
    IContentCalendarService calendar,
    IMediaLibraryService media,
    TimeProvider clock,
    ILogger<BlogAgentTools> logger,
    IAffiliateSuggestionService? affiliateSuggestions = null) : ISentinelAgentToolset
{
    private static readonly string[] PostRoles = [AppRoles.Admin, AppRoles.Author, AppRoles.Contributor];
    private static readonly string[] CalendarRoles = [AppRoles.Admin];
    private const int PostsShown = 25;
    private const int BodyCharacters = 6_000;
    private const int CalendarItemsShown = 40;

    public SentinelAgentArea Area { get; } = new("blog", "blog posts and the content calendar", ["blog", "post", "article", "draft", "calendar", "schedul", "publish"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "find_blog_posts",
            "Find blog posts by words in the title or tags, optionally only Draft or Published ones. Gives ids, status and when each went (or goes) live.",
            SentinelAgentJson.Schema(
                ("search", "string", "Words in the title or tags", false),
                ("status", "string", "Draft or Published", false)),
            SentinelAgentRisk.Read, PostRoles,
            _ => "Looking at the blog posts",
            FindAsync),
        new SentinelAgentTool(
            "read_blog_post",
            "Read one blog post: title, status, description, tags and its markdown body.",
            SentinelAgentJson.Schema(("postId", "string", "The post id", true)),
            SentinelAgentRisk.Read, PostRoles,
            _ => "Reading a blog post",
            ReadAsync),
        new SentinelAgentTool(
            "create_blog_post_draft",
            "Write a new blog post as a Draft (nothing goes live). The body is markdown. Optionally a hero image from the media library (find_media gives ids).",
            SentinelAgentJson.Schema(
                ("title", "string", "The post's title", true),
                ("body", "string", "The post as markdown", true),
                ("description", "string", "A one-sentence summary for search results", false),
                ("tags", "string[]", "Tags", false),
                ("heroImageId", "string", "Optional: a media library image id", false)),
            SentinelAgentRisk.Change, PostRoles,
            args => $"Drafting the post '{args.OptionalText("title")}'",
            CreateAsync,
            UndoCreateAsync),
        new SentinelAgentTool(
            "edit_blog_post_draft",
            "Change a Draft blog post: a new title, description, tags or hero image, a new body (replaces it) or text to add at the end. Published posts are changed in the post editor.",
            SentinelAgentJson.Schema(
                ("postId", "string", "The post id", true),
                ("title", "string", "The new title", false),
                ("body", "string", "A whole new body as markdown (replaces it)", false),
                ("addToEnd", "string", "Markdown to add at the end", false),
                ("description", "string", "The new summary", false),
                ("tags", "string[]", "The new tags (replaces them)", false),
                ("heroImageId", "string", "A media library image id", false)),
            SentinelAgentRisk.Change, PostRoles,
            _ => "Editing a draft post",
            EditAsync,
            UndoEditAsync),
        new SentinelAgentTool(
            "publish_blog_post",
            "Publish a Draft blog post now: it goes live on the blog and article-alert subscribers get an email. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(("postId", "string", "The post id", true)),
            SentinelAgentRisk.Confirm, PostRoles,
            _ => "Proposing to publish a post",
            PublishAsync,
            UndoPublishAsync,
            async (args, _, ct) =>
            {
                var post = await DraftAsync(args.Id("postId"), ct);
                return $"Publish the blog post '{post.Title}' now (it goes live, and article-alert subscribers get an email)";
            }),
        new SentinelAgentTool(
            "content_calendar",
            "What's going out: scheduled and recent posts, pages, social posts, emails and live shows, with ids, kind and time. Shows the next 30 days unless told otherwise.",
            SentinelAgentJson.Schema(
                ("days", "number", "How many days ahead (30 when left out)", false),
                ("daysBack", "number", "How many days back to include (0 when left out)", false)),
            SentinelAgentRisk.Read, CalendarRoles,
            _ => "Looking at the content calendar",
            CalendarAsync),
        new SentinelAgentTool(
            "reschedule_content",
            "Move a scheduled post, page or social post (from content_calendar) to a new future time.",
            SentinelAgentJson.Schema(
                ("kind", "string", "The item's kind from content_calendar: post, page or social", true),
                ("id", "string", "The item's id", true),
                ("at", "string", "The new time, as 2026-11-04 09:00 (local time)", true)),
            SentinelAgentRisk.Change, CalendarRoles,
            args => $"Moving a {args.OptionalText("kind")} to {args.OptionalText("at")}",
            RescheduleAsync,
            UndoRescheduleAsync)
    ];

    public static string LinkTo(Guid postId) => $"/admin/article-editor/{postId}";

    private async Task<SentinelAgentToolResult> FindAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var status = args.OptionalText("status") is { } named
            ? new[] { ArticleStatuses.Draft, ArticleStatuses.Published }.FirstOrDefault(s => string.Equals(s, named, StringComparison.OrdinalIgnoreCase))
                ?? throw new SentinelAgentToolException("'status' is Draft or Published.")
            : null;
        var words = (args.OptionalText("search") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var posts = (await db.Articles.AsNoTracking()
                .Where(article => article.TrashedAt == null && (status == null || article.Status == status))
                .Select(article => new { article.Id, article.Title, article.Status, article.PublishedAt, article.Tags, article.UpdatedAt, article.CreatedAt })
                .ToListAsync(ct))
            .Where(post => words.All(word => post.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || post.Tags.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(post => post.UpdatedAt ?? post.CreatedAt)
            .ToList();
        return SentinelAgentToolResult.Of(new
        {
            found = posts.Count,
            posts = posts.Take(PostsShown).Select(post => new
            {
                id = post.Id,
                title = post.Title,
                status = post.Status,
                live = post.Status == ArticleStatuses.Published ? post.PublishedAt?.ToString("yyyy-MM-dd HH:mm") : null
            })
        });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var post = await PostAsync(args.Id("postId"), ct);
        return SentinelAgentToolResult.Of(new
        {
            id = post.Id,
            title = post.Title,
            status = post.Status,
            slug = post.Slug,
            description = post.MetaDescription,
            tags = post.Tags,
            author = post.Author,
            live = post.Status == ArticleStatuses.Published ? post.PublishedAt?.ToString("yyyy-MM-dd HH:mm") : null,
            body = post.BodyMarkdown.Length <= BodyCharacters ? post.BodyMarkdown : post.BodyMarkdown[..BodyCharacters] + "\n\n(... the rest is cut off here)"
        });
    }

    private async Task<SentinelAgentToolResult> CreateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var title = args.Text("title");
        var site = await settings.GetSettingsAsync(ct);
        var post = new Article
        {
            Title = title,
            Slug = await UniqueSlugAsync(title, ct),
            BodyMarkdown = args.Text("body"),
            MetaDescription = args.OptionalText("description") ?? string.Empty,
            Tags = string.Join(", ", args.Texts("tags")),
            Status = ArticleStatuses.Draft,
            Source = ArticleSource.Manual,
            CategoryId = site.DefaultArticleCategoryId,
            CreatedBy = context.User.Username
        };
        if (!string.IsNullOrWhiteSpace(site.DefaultAuthorByline)) post.Author = site.DefaultAuthorByline;
        if (args.OptionalId("heroImageId") is { } imageId) await SetHeroAsync(post, imageId, ct);
        db.Articles.Add(post);
        await db.SaveChangesAsync(ct);
        return SentinelAgentToolResult.Of(
            new { created = true, postId = post.Id, post.Title, status = post.Status },
            new SentinelAgentChange($"Drafted the blog post '{post.Title}'", "blog-post", post.Id.ToString(), post.Title, LinkTo(post.Id),
                SentinelAgentJson.Serialize(new PostUndo(post.Id))));
    }

    // Undo moves the draft to the trash rather than deleting it.
    private async Task UndoCreateAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var post = await TrackedAsync(SentinelAgentJson.Deserialize<PostUndo>(undoJson).PostId, ct);
        if (post.Status != ArticleStatuses.Draft) throw new SentinelAgentToolException($"'{post.Title}' has been published since, so it's left live.");
        post.TrashedAt = clock.GetUtcNow();
        Touch(post, context);
        await db.SaveChangesAsync(ct);
    }

    private async Task<SentinelAgentToolResult> EditAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var post = await TrackedAsync((await DraftAsync(args.Id("postId"), ct)).Id, ct);
        var before = PostFields.Of(post);
        if (args.OptionalText("title") is { } title) post.Title = title;
        if (args.OptionalText("body") is { } body) post.BodyMarkdown = body;
        if (args.OptionalText("addToEnd") is { } addition) post.BodyMarkdown = post.BodyMarkdown.TrimEnd() + "\n\n" + addition;
        if (args.OptionalText("description") is { } description) post.MetaDescription = description;
        if (args["tags"] is not null) post.Tags = string.Join(", ", args.Texts("tags"));
        if (args.OptionalId("heroImageId") is { } imageId) await SetHeroAsync(post, imageId, ct);
        var after = PostFields.Of(post);
        if (after == before) throw new SentinelAgentToolException($"Nothing to change: say what's new for '{post.Title}'.");

        Touch(post, context);
        await db.SaveChangesAsync(ct);
        return SentinelAgentToolResult.Of(
            new { edited = post.Title, bodyCharacters = post.BodyMarkdown.Length },
            new SentinelAgentChange($"Edited the draft post '{post.Title}'", "blog-post", post.Id.ToString(), post.Title, LinkTo(post.Id),
                SentinelAgentJson.Serialize(new PostEditUndo(post.Id, before, after))));
    }

    private async Task UndoEditAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<PostEditUndo>(undoJson);
        var post = await TrackedAsync(undo.PostId, ct);
        if (PostFields.Of(post) != undo.After)
            throw new SentinelAgentToolException($"'{post.Title}' has changed since, so undoing would lose that. Edit it in the post editor.");
        undo.Before.ApplyTo(post);
        Touch(post, context);
        await db.SaveChangesAsync(ct);
    }

    // Like the editor's Publish: goes live now, then affiliate suggestions are made (best effort).
    private async Task<SentinelAgentToolResult> PublishAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var post = await TrackedAsync((await DraftAsync(args.Id("postId"), ct)).Id, ct);
        var publishedBefore = post.PublishedAt;
        var now = clock.GetUtcNow();
        post.Status = ArticleStatuses.Published;
        if (post.PublishedAt is null || post.PublishedAt > now) post.PublishedAt = now;
        Touch(post, context);
        await db.SaveChangesAsync(ct);
        if (affiliateSuggestions is not null)
        {
            try
            {
                await affiliateSuggestions.GenerateForArticleAsync(post.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Affiliate suggestion generation failed for post {Id} published by SentinelGPT.", post.Id);
            }
        }

        return SentinelAgentToolResult.Of(new { published = post.Title, url = $"/blog/{post.Slug}" },
            new SentinelAgentChange($"Published the blog post '{post.Title}'", "blog-post", post.Id.ToString(), post.Title, $"/blog/{post.Slug}",
                SentinelAgentJson.Serialize(new PublishUndo(post.Id, publishedBefore))));
    }

    // Back to Draft, like the editor's Unpublish. Alert emails already sent stay sent.
    private async Task UndoPublishAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<PublishUndo>(undoJson);
        var post = await TrackedAsync(undo.PostId, ct);
        if (post.Status != ArticleStatuses.Published) throw new SentinelAgentToolException($"'{post.Title}' is already a draft.");
        post.Status = ArticleStatuses.Draft;
        post.PublishedAt = undo.PublishedAtBefore;
        Touch(post, context);
        await db.SaveChangesAsync(ct);
    }

    private async Task<SentinelAgentToolResult> CalendarAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ahead = (int)Math.Clamp(args.OptionalNumber("days") ?? 30, 1, 365);
        var back = (int)Math.Clamp(args.OptionalNumber("daysBack") ?? 0, 0, 365);
        var items = await calendar.GetItemsAsync(now.AddDays(-back), now.AddDays(ahead), ct);
        return SentinelAgentToolResult.Of(new
        {
            found = items.Count,
            items = items.OrderBy(item => item.At).Take(CalendarItemsShown).Select(item => new
            {
                kind = item.Kind,
                id = item.Id,
                title = item.Title,
                at = item.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                status = item.Status,
                canMove = item.CanReschedule ? true : (bool?)null
            })
        });
    }

    private async Task<SentinelAgentToolResult> RescheduleAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var kind = args.Text("kind").ToLowerInvariant();
        var id = args.Id("id");
        var at = LocalTime(args.Text("at"));
        var now = clock.GetUtcNow();
        var item = (await calendar.GetItemsAsync(now, now.AddYears(2), ct)).FirstOrDefault(entry => entry.Kind == kind && entry.Id == id)
            ?? throw new SentinelAgentToolException($"There's no upcoming {kind} with id {id}. Look it up with content_calendar.");
        if (!item.CanReschedule) throw new SentinelAgentToolException($"'{item.Title}' can't be moved from the calendar: emails follow their campaign and live shows aren't scheduled.");
        await calendar.RescheduleAsync(kind, id, at, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { moved = item.Title, to = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") },
            new SentinelAgentChange($"Moved '{item.Title}' to {at.ToLocalTime():dd-MM-yy HH:mm}", "calendar-item", id.ToString(), item.Title, item.Link,
                SentinelAgentJson.Serialize(new RescheduleUndo(kind, id, item.At, at))));
    }

    private async Task UndoRescheduleAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<RescheduleUndo>(undoJson);
        if (undo.Before <= clock.GetUtcNow()) throw new SentinelAgentToolException("Its old time has passed, so it can't go back to it. Pick a new time instead.");
        await calendar.RescheduleAsync(undo.Kind, undo.Id, undo.Before, context.User.Username, ct);
    }

    private async Task<Article> PostAsync(Guid postId, CancellationToken ct) =>
        await db.Articles.AsNoTracking().FirstOrDefaultAsync(article => article.Id == postId && article.TrashedAt == null, ct)
            ?? throw new SentinelAgentToolException($"There's no blog post with id {postId}. Find the posts first.");

    private async Task<Article> TrackedAsync(Guid postId, CancellationToken ct) =>
        await db.Articles.FirstOrDefaultAsync(article => article.Id == postId && article.TrashedAt == null, ct)
            ?? throw new SentinelAgentToolException($"There's no blog post with id {postId}. Find the posts first.");

    private async Task<Article> DraftAsync(Guid postId, CancellationToken ct)
    {
        var post = await PostAsync(postId, ct);
        return post.Status == ArticleStatuses.Draft
            ? post
            : throw new SentinelAgentToolException($"'{post.Title}' is already published. Change it in the post editor.");
    }

    private async Task SetHeroAsync(Article post, Guid imageId, CancellationToken ct)
    {
        var image = (await media.ListAsync(ct)).FirstOrDefault(asset => asset.Id == imageId)
            ?? throw new SentinelAgentToolException($"There's no media library image with id {imageId}. Find it with find_media.");
        post.HeroImageUrl = image.Url;
        post.HeroImageAltText = image.AltText;
    }

    private async Task<string> UniqueSlugAsync(string title, CancellationToken ct)
    {
        var slug = Regex.Replace(Regex.Replace(title.ToLowerInvariant().Trim(), @"[^a-z0-9\s-]", string.Empty), @"\s+", "-").Trim('-');
        if (slug.Length == 0) slug = "post";
        var taken = await db.Articles.AsNoTracking().Where(article => article.Slug.StartsWith(slug)).Select(article => article.Slug).ToListAsync(ct);
        if (!taken.Contains(slug)) return slug;
        var n = 2;
        while (taken.Contains($"{slug}-{n}")) n++;
        return $"{slug}-{n}";
    }

    private void Touch(Article post, SentinelAgentToolContext context)
    {
        post.UpdatedAt = clock.GetUtcNow();
        post.UpdatedBy = context.User.Username;
    }

    // "2026-11-04 09:00" in the server's time zone (the one the prompt's "today" uses), to UTC.
    private static DateTimeOffset LocalTime(string text)
    {
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "dd-MM-yy HH:mm", "yyyy-MM-dd"];
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local).ToUniversalTime()
            : throw new SentinelAgentToolException($"'{text}' isn't a time like 2026-11-04 09:00.");
    }

    private sealed record PostFields(string Title, string BodyMarkdown, string MetaDescription, string Tags, string? HeroImageUrl, string HeroImageAltText)
    {
        public static PostFields Of(Article post) => new(post.Title, post.BodyMarkdown, post.MetaDescription, post.Tags, post.HeroImageUrl, post.HeroImageAltText);

        public void ApplyTo(Article post)
        {
            post.Title = Title;
            post.BodyMarkdown = BodyMarkdown;
            post.MetaDescription = MetaDescription;
            post.Tags = Tags;
            post.HeroImageUrl = HeroImageUrl;
            post.HeroImageAltText = HeroImageAltText;
        }
    }

    private sealed record PostUndo(Guid PostId);

    private sealed record PostEditUndo(Guid PostId, PostFields Before, PostFields After);

    private sealed record PublishUndo(Guid PostId, DateTimeOffset? PublishedAtBefore);

    private sealed record RescheduleUndo(string Kind, Guid Id, DateTimeOffset Before, DateTimeOffset After);
}
