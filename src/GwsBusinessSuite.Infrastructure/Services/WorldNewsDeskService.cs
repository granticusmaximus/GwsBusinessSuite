using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// N2 (World News trust plan, 13-11-26): a verified story becomes a draft article in Content, with
// the evidence written in (StoryDraft). A draft only: a person edits and publishes it.
public sealed partial class WorldNewsDeskService(IAppDbContext db, WorldNewsService news)
{
    public async Task<string> HandoverAsync(int hours, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (hours is < 1 or > 72) throw new ArgumentOutOfRangeException(nameof(hours), "Choose a handover range from 1 to 72 hours.");
        return await Task.Run(() => news.HandoverMarkdown(hours), cancellationToken);
    }

    public async Task<IReadOnlyList<DeskNote>> NotesAsync(string eventId, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (!await Task.Run(() => news.Store.HasEvent(eventId), cancellationToken))
            throw new InvalidOperationException("This story is no longer available.");
        return await Task.Run(() => news.Store.DeskNotes(eventId, limit: 100), cancellationToken);
    }

    public async Task<DeskNote> AddNoteAsync(string eventId, string text, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (DeskNotes.Problem(text) is { } problem) throw new ArgumentException(problem, nameof(text));
        if (!await Task.Run(() => news.Store.HasEvent(eventId), cancellationToken))
            throw new InvalidOperationException("This story is no longer available. Open a current story before adding a note.");
        var mentions = DeskNotes.Mentions(text).Select(name => name.ToLowerInvariant()).ToList();
        var recipients = (await db.AppUsers.AsNoTracking()
            .Where(user => user.IsActive && user.Role == AppRoles.Admin && mentions.Contains(user.Username.ToLower()))
            .Select(user => user.Username).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await Task.Run(() => news.AddDeskNote(eventId, username, text, recipients.Contains), cancellationToken);
    }

    private async Task RequireDeskUserAsync(string username, CancellationToken cancellationToken)
    {
        if (!await db.AppUsers.AsNoTracking()
                .AnyAsync(user => user.Username == username && user.IsActive && user.Role == AppRoles.Admin, cancellationToken))
            throw new UnauthorizedAccessException("Only active administrators can access the news desk.");
    }

    public async Task<Guid> CreateDraftAsync(StoryDocument story, EvidenceBoard? board, string username, CancellationToken cancellationToken = default)
    {
        // N11: nothing is drafted from a story on hold until its release time.
        if (news.Store.Hold(story.Event.Id) is { } hold && Holds.IsHeld(hold, DateTimeOffset.UtcNow))
            throw new InvalidOperationException(Holds.Describe(hold, at => at.ToLocalGwsDateTime()));
        var checks = news.Store.DeskChecks(story.Event.Id);
        var title = story.Event.Headline ?? story.Event.Title;
        var site = await db.SiteSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var article = new Article
        {
            Title = title.Length > 200 ? title[..200] : title,
            Slug = await UniqueSlugAsync(title, cancellationToken),
            BodyMarkdown = StoryDraft.Markdown(story, checks, board, at => at.ToLocalGwsDateTime()),
            MetaDescription = story.Trust is { } trust ? $"{trust.Confidence.Label}: {trust.Confidence.Reason}" : string.Empty,
            // "verified" only once the desk has ticked every checklist item with its evidence.
            Tags = DeskChecklist.IsComplete(checks) ? "world news, verified" : "world news",
            Status = ArticleStatuses.Draft,
            Source = ArticleSource.Manual,
            CategoryId = site?.DefaultArticleCategoryId,
            CreatedBy = username,
        };
        if (!string.IsNullOrWhiteSpace(site?.DefaultAuthorByline)) article.Author = site.DefaultAuthorByline;
        db.Articles.Add(article);
        await db.SaveChangesAsync(cancellationToken);
        return article.Id;
    }

    private async Task<string> UniqueSlugAsync(string title, CancellationToken cancellationToken)
    {
        var slug = NotSlug().Replace(title.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 80) slug = slug[..80].Trim('-');
        if (slug.Length == 0) slug = "world-news";
        var candidate = slug;
        for (var n = 2; await db.Articles.AnyAsync(a => a.Slug == candidate, cancellationToken); n++) candidate = $"{slug}-{n}";
        return candidate;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
