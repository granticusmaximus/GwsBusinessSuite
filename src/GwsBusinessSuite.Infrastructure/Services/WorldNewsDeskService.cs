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
    public const int SourceBookPageSize = 25;

    public async Task<IReadOnlyList<Correction>> CorrectionsAsync(string eventId, string username, int page = 0, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        return await Task.Run(() =>
        {
            if (!news.Store.HasEvent(eventId)) throw new InvalidOperationException("That story is no longer available.");
            return news.Store.Corrections(eventId, Corrections.PageSize + 1, checked(page * Corrections.PageSize));
        }, cancellationToken);
    }

    public async Task<Correction> PublishCorrectionAsync(string eventId, string title, string text, string? articleSlug, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (Corrections.Problem(title, text) is { } problem) throw new ArgumentException(problem, nameof(text));
        Guid? articleId = null;
        if (!string.IsNullOrWhiteSpace(articleSlug))
        {
            var slug = articleSlug.Trim();
            var article = await db.Articles.AsNoTracking().Where(a => a.Slug == slug && a.TrashedAt == null)
                .Select(a => new { a.Id, a.Status, a.PublishedAt }).SingleOrDefaultAsync(cancellationToken);
            if (article is null || !PublicationWindows.IsVisible(article.Status, ArticleStatuses.Published, article.PublishedAt, DateTimeOffset.UtcNow))
                throw new ArgumentException("Choose the slug of a currently published article, or leave it empty.", nameof(articleSlug));
            articleId = article.Id;
        }
        return await Task.Run(() => news.Store.AddCorrection(eventId, title, text, username, DateTimeOffset.UtcNow, articleId), cancellationToken);
    }

    public async Task<StoryHold> SetHoldAsync(string eventId, DateTimeOffset until, string reason, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (Holds.Problem(until, reason, now) is { } problem) throw new ArgumentException(problem, nameof(until));
        var hold = new StoryHold(eventId, until.ToUniversalTime(), reason.Trim(), username, now);
        await Task.Run(() => news.Store.SetHold(hold), cancellationToken);
        return hold;
    }

    public async Task ReleaseHoldAsync(string eventId, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        await Task.Run(() => news.Store.ReleaseHold(eventId), cancellationToken);
    }

    public async Task<(IReadOnlyList<LiveUpdate> Updates, StoryHold? Hold)> LiveUpdatesAsync(
        string eventId, string username, int page = 0, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        return await Task.Run(() =>
        {
            if (!news.Store.HasEvent(eventId)) throw new InvalidOperationException("That story is no longer available.");
            return (news.Store.LiveUpdates(eventId, LiveUpdates.PageSize + 1, checked(page * LiveUpdates.PageSize)), news.Store.Hold(eventId));
        }, cancellationToken);
    }

    public async Task<LiveUpdate> PublishLiveUpdateAsync(string eventId, string text, string status, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() => news.Store.AddLiveUpdate(eventId, username, text, status, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task CorrectLiveUpdateAsync(string eventId, string id, string text, string status, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        await Task.Run(() => news.Store.CorrectLiveUpdate(id, text, status, DateTimeOffset.UtcNow, eventId), cancellationToken);
    }

    public async Task<IReadOnlyList<SourceContact>> ContactsAsync(string username, int page = 0, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        return await Task.Run(() => news.Store.SourceContacts(username, checked(page * SourceBookPageSize), SourceBookPageSize + 1), cancellationToken);
    }

    public async Task<SourceContact> SaveContactAsync(SourceContact contact, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() => news.Store.SaveSourceContact(contact with { Owner = username }), cancellationToken);
    }

    public async Task RemoveContactAsync(string id, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        await Task.Run(() => news.Store.RemoveSourceContact(username, id), cancellationToken);
    }

    public async Task<(IReadOnlyList<SourceUse> Uses, (int Total, int Held, int Wrong) Counts)> SourceHistoryAsync(
        string id, string username, int page = 0, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegative(page);
        return await Task.Run(() =>
        {
            if (news.Store.FindSourceContact(username, id) is null)
                throw new InvalidOperationException("That contact is no longer available in your source book.");
            return (news.Store.SourceUses(username, sourceId: id, offset: checked(page * SourceBookPageSize), limit: SourceBookPageSize + 1),
                news.Store.SourceUseCounts(username, id));
        }, cancellationToken);
    }

    public async Task RecordSourceUseAsync(string id, string eventId, string outcome, string note, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (!SourceBook.Outcomes.Contains(outcome)) throw new ArgumentException("Choose unknown, held or wrong as the outcome.", nameof(outcome));
        await Task.Run(() =>
        {
            if (!news.Store.HasEvent(eventId)) throw new InvalidOperationException("That story is no longer available. Choose a current World News story.");
            if (!news.Store.RecordSourceUse(username, id, eventId, outcome, note, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("That contact is no longer available in your source book.");
        }, cancellationToken);
    }

    public async Task<DeskApprovals> ApprovalsAsync(string eventId, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() =>
        {
            var story = news.Story(eventId, username) ?? throw new InvalidOperationException("That story is no longer available.");
            var label = story.Trust?.Confidence.Level.ToString() ?? throw new InvalidOperationException("Refresh the story's trust label before approving it.");
            return new DeskApprovals(news.Store.SignOffs(eventId), DeskChecklist.IsComplete(news.Store.DeskChecks(eventId)), label);
        }, cancellationToken);
    }

    public async Task<SignOff> SignAsync(string eventId, string kind, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() =>
        {
            var story = news.Story(eventId, username) ?? throw new InvalidOperationException("That story is no longer available.");
            var label = story.Trust?.Confidence.Level.ToString() ?? throw new InvalidOperationException("Refresh the story's trust label before approving it.");
            return news.Store.AddSignOff(eventId, kind, username, label, DateTimeOffset.UtcNow);
        }, cancellationToken);
    }

    public async Task<string> HandoverAsync(int hours, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (hours is < 1 or > 72) throw new ArgumentOutOfRangeException(nameof(hours), "Choose a handover range from 1 to 72 hours.");
        return await Task.Run(() => news.HandoverMarkdown(hours), cancellationToken);
    }

    public async Task<IReadOnlyList<MorgueHit>> SearchMorgueAsync(string text, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (text.Length > MorgueSearch.MaxQueryLength)
            throw new ArgumentException($"Keep the search to {MorgueSearch.MaxQueryLength} characters or fewer.", nameof(text));
        return await Task.Run(() => news.Store.SearchDesk(text.Trim(), username, MorgueSearch.MaxResults), cancellationToken);
    }

    public async Task<DeskMetrics> MetricsAsync(string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() => news.Store.DeskMetrics(DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<IReadOnlyList<DossierItem>> DossierAsync(string eventId, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() =>
        {
            if (!news.Store.HasEvent(eventId)) throw new InvalidOperationException("That story is no longer available.");
            return news.Store.Dossier(eventId);
        }, cancellationToken);
    }

    public async Task<DossierItem> PinDossierItemAsync(
        string eventId, string kind, string title, string? url, string detail, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (Dossiers.Problem(kind, title, url, detail) is { } problem) throw new ArgumentException(problem, nameof(title));
        var normalizedUrl = string.IsNullOrWhiteSpace(url) ? null : new Uri(url.Trim(), UriKind.Absolute).AbsoluteUri;
        return await Task.Run(() =>
            news.Store.PinToDossier(eventId, kind, title.Trim(), normalizedUrl, detail.Trim(), username, DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("The dossier is full. Remove an item before adding another."),
            cancellationToken);
    }

    public async Task UnpinDossierItemAsync(string eventId, string id, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        if (!await Task.Run(() => news.Store.UnpinFromDossier(eventId, id), cancellationToken))
            throw new InvalidOperationException("That dossier item is no longer available on this story.");
    }

    public async Task<(string FileName, byte[] Pdf)> ExportDossierPdfAsync(string eventId, string username, CancellationToken cancellationToken = default)
    {
        await RequireDeskUserAsync(username, cancellationToken);
        return await Task.Run(() =>
        {
            var story = news.Story(eventId, username) ?? throw new InvalidOperationException("That story is no longer available.");
            var items = news.Store.Dossier(eventId);
            var notes = news.Store.DeskNotes(eventId, limit: 100);
            var checks = news.Store.DeskChecks(eventId);
            var signOffs = news.Store.SignOffs(eventId);
            return (DossierPdf.FileName(story), DossierPdf.Render(story, items, notes, checks, signOffs, username, DateTimeOffset.UtcNow));
        }, cancellationToken);
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
