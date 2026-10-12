using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Tags (item 21), page analytics (item 22) and knowledge gaps (item 23). Every list is filtered to
// what the user can view; changing tags needs edit access.
public sealed class SentinelInsightsService(
    IAppDbContext dbContext,
    IWikiService wikiService,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null) : ISentinelInsightsService
{
    public const int MaxList = 50;

    // ---- Tags ----

    // Called by WikiService after a page's blocks are saved: its text tags become exactly the
    // #tags now in its text. Manual tags are untouched.
    public static async Task SyncTextTagsAsync(IAppDbContext db, Guid pageId, string blocksJson, CancellationToken ct)
    {
        var wanted = SentinelTags.FromBlocks(WikiBlockJson.ParseBlocks(blocksJson)).ToHashSet(StringComparer.Ordinal);
        var existing = await db.PageTags.Where(t => t.WikiPageId == pageId && !t.IsManual).ToListAsync(ct);
        var changed = false;
        foreach (var stale in existing.Where(t => !wanted.Contains(t.Tag)))
        {
            db.PageTags.Remove(stale);
            changed = true;
        }
        foreach (var tag in wanted.Where(t => existing.All(e => e.Tag != t)))
        {
            await db.PageTags.AddAsync(new PageTag { WikiPageId = pageId, Tag = tag, CreatedBy = "text" }, ct);
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SentinelTagCount>> ListTagsAsync(string username, CancellationToken cancellationToken = default)
    {
        var rows = await LiveTagRowsAsync(cancellationToken);
        var visible = await VisibleAsync(rows.Select(r => r.PageId), username, cancellationToken);
        var pairs = rows.Where(r => visible.Contains(r.PageId)).Select(r => (r.PageId, r.Tag)).Distinct().ToList();
        // A nested tag also counts towards each of its parents.
        var counts = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        foreach (var (pageId, tag) in pairs)
        {
            var parts = tag.Split('/');
            for (var depth = 1; depth <= parts.Length; depth++)
            {
                var key = string.Join('/', parts.Take(depth));
                if (!counts.TryGetValue(key, out var pages)) counts[key] = pages = [];
                pages.Add(pageId);
            }
        }
        return counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new SentinelTagCount(c.Key, c.Value.Count)).ToList();
    }

    public async Task<IReadOnlyList<SentinelTaggedPage>> ListTaggedPagesAsync(string tag, string username, CancellationToken cancellationToken = default)
    {
        if (SentinelTags.Normalize(tag) is not { } wanted) return [];
        var rows = (await LiveTagRowsAsync(cancellationToken)).Where(r => SentinelTags.IsUnder(r.Tag, wanted)).ToList();
        var visible = await VisibleAsync(rows.Select(r => r.PageId), username, cancellationToken);
        var ids = rows.Select(r => r.PageId).Where(visible.Contains).Distinct().ToList();
        var pages = await dbContext.WikiPages.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Title, p.Icon }).ToListAsync(cancellationToken);
        return pages.OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .Select(p => new SentinelTaggedPage(p.Id, p.Title, p.Icon, rows.Where(r => r.PageId == p.Id).Select(r => r.Tag).Distinct().Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    public async Task<SentinelPageTagsView> GetPageTagsAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.PageTags.AsNoTracking().Where(t => t.WikiPageId == pageId).ToListAsync(cancellationToken);
        return new SentinelPageTagsView(
            rows.Where(t => !t.IsManual).Select(t => t.Tag).Order(StringComparer.Ordinal).ToList(),
            rows.Where(t => t.IsManual).Select(t => t.Tag).Order(StringComparer.Ordinal).ToList());
    }

    public async Task<SentinelPageTagsView> AddManualTagAsync(Guid pageId, string tag, string username, CancellationToken cancellationToken = default)
    {
        var normalized = SentinelTags.Normalize(tag) ?? throw new ArgumentException("Tags are letters, numbers, - and _, nested with / (like client/acme).");
        await EnsureEditAsync(pageId, username, cancellationToken);
        if (!await dbContext.PageTags.AnyAsync(t => t.WikiPageId == pageId && t.IsManual && t.Tag == normalized, cancellationToken))
        {
            await dbContext.PageTags.AddAsync(new PageTag { WikiPageId = pageId, Tag = normalized, IsManual = true, CreatedBy = username }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return await GetPageTagsAsync(pageId, cancellationToken);
    }

    public async Task<SentinelPageTagsView> RemoveManualTagAsync(Guid pageId, string tag, string username, CancellationToken cancellationToken = default)
    {
        await EnsureEditAsync(pageId, username, cancellationToken);
        var normalized = SentinelTags.Normalize(tag) ?? string.Empty;
        await dbContext.PageTags.Where(t => t.WikiPageId == pageId && t.IsManual && t.Tag == normalized).ExecuteDeleteAsync(cancellationToken);
        return await GetPageTagsAsync(pageId, cancellationToken);
    }

    private async Task<List<(Guid PageId, string Tag)>> LiveTagRowsAsync(CancellationToken ct) =>
        (await (from tag in dbContext.PageTags.AsNoTracking()
                join page in dbContext.WikiPages.AsNoTracking() on tag.WikiPageId equals page.Id
                where page.TrashedAt == null
                select new { tag.WikiPageId, tag.Tag }).ToListAsync(ct))
        .Select(r => (r.WikiPageId, r.Tag)).ToList();

    // ---- Analytics ----

    public async Task RecordViewAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var since = now - (long)SentinelGaps.ViewDebounce.TotalSeconds;
        if (await dbContext.PageViews.AnyAsync(v => v.Username == username && v.WikiPageId == pageId && v.ViewedUnix > since, cancellationToken)) return;
        await dbContext.PageViews.AddAsync(new PageView { WikiPageId = pageId, Username = username, ViewedUnix = now, CreatedBy = username }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SentinelPageViewStats> GetViewStatsAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow().AddDays(-SentinelGaps.StatsDays).ToUnixTimeSeconds();
        var views = await dbContext.PageViews.AsNoTracking().Where(v => v.WikiPageId == pageId && v.ViewedUnix >= since).Select(v => v.Username).ToListAsync(cancellationToken);
        return new SentinelPageViewStats(views.Count, views.Distinct(StringComparer.OrdinalIgnoreCase).Count(), SentinelGaps.StatsDays);
    }

    public async Task<IReadOnlyList<SentinelReadPage>> ListMostReadAsync(string username, CancellationToken cancellationToken = default)
    {
        var since = timeProvider.GetUtcNow().AddDays(-SentinelGaps.StatsDays).ToUnixTimeSeconds();
        var views = await dbContext.PageViews.AsNoTracking().Where(v => v.ViewedUnix >= since).Select(v => new { v.WikiPageId, v.Username, v.ViewedUnix }).ToListAsync(cancellationToken);
        var visible = await VisibleAsync(views.Select(v => v.WikiPageId), username, cancellationToken);
        var stats = views.Where(v => visible.Contains(v.WikiPageId)).GroupBy(v => v.WikiPageId)
            .Select(g => (Id: g.Key, Views: g.Count(), Viewers: g.Select(v => v.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Last: g.Max(v => v.ViewedUnix)))
            .OrderByDescending(s => s.Views).ThenByDescending(s => s.Viewers).Take(MaxList * 2).ToList();
        var ids = stats.Select(s => s.Id).ToList();
        var pages = await dbContext.WikiPages.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrashedAt == null)
            .Select(p => new { p.Id, p.Title, p.Icon }).ToDictionaryAsync(p => p.Id, cancellationToken);
        return stats.Where(s => pages.ContainsKey(s.Id)).Take(MaxList)
            .Select(s => new SentinelReadPage(s.Id, pages[s.Id].Title, pages[s.Id].Icon, s.Views, s.Viewers, DateTimeOffset.FromUnixTimeSeconds(s.Last))).ToList();
    }

    public async Task<IReadOnlyList<SentinelReadPage>> ListNeverReadAsync(string username, CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-SentinelGaps.NeverReadAfterDays);
        var viewed = (await dbContext.PageViews.AsNoTracking().Select(v => v.WikiPageId).Distinct().ToListAsync(cancellationToken)).ToHashSet();
        // CreatedAt is compared in memory: SQLite can't compare DateTimeOffset.
        var candidates = (await dbContext.WikiPages.AsNoTracking()
                .Where(p => p.TrashedAt == null && p.SystemKey == null && p.NotionArchivedAt == null)
                .Select(p => new { p.Id, p.Title, p.Icon, p.CreatedAt }).ToListAsync(cancellationToken))
            .Where(p => p.CreatedAt < cutoff && !viewed.Contains(p.Id)).ToList();
        var visible = await VisibleAsync(candidates.Select(p => p.Id), username, cancellationToken);
        return candidates.Where(p => visible.Contains(p.Id)).OrderBy(p => p.CreatedAt).Take(MaxList)
            .Select(p => new SentinelReadPage(p.Id, p.Title, p.Icon, 0, 0, null)).ToList();
    }

    // ---- Knowledge gaps ----

    public async Task<IReadOnlyList<SentinelKnowledgeGap>> ListKnowledgeGapsAsync(string username, CancellationToken cancellationToken = default)
    {
        var isAdmin = await dbContext.AppUsers.AsNoTracking().AnyAsync(u => u.Username == username && u.Role == AppRoles.Admin, cancellationToken);
        var rows = await dbContext.SentinelQuestions.AsNoTracking()
            .Where(q => q.ResolvedUnix == null && (q.NotFound || q.Uncited) && (isAdmin || q.Username == username))
            .OrderByDescending(q => q.AskedUnix).Take(500)
            .Select(q => new { q.Id, q.Question, q.Terms, q.AskedUnix }).ToListAsync(cancellationToken);
        return SentinelGaps.Group(rows.Select(r => new SentinelGaps.GapQuestion(r.Id, r.Question,
            r.Terms.Split(' ', StringSplitOptions.RemoveEmptyEntries), DateTimeOffset.FromUnixTimeSeconds(r.AskedUnix))));
    }

    public async Task<Guid> WriteGapPageAsync(SentinelKnowledgeGap gap, string username, CancellationToken cancellationToken = default)
    {
        WikiBlock Block(string type, string text, Dictionary<string, string>? props = null) =>
            new(Guid.NewGuid(), type, 0, text.Length == 0 ? [] : [new WikiRichTextSpan(text)], props ?? new Dictionary<string, string>());
        var blocks = new List<WikiBlock>
        {
            Block(WikiBlockTypes.Callout, $"People asked about this {gap.Asks} time{(gap.Asks == 1 ? "" : "s")} and the workspace had no answer. Questions this page should answer:", new() { ["icon"] = "❓" })
        };
        blocks.AddRange(gap.Questions.Select(q => Block(WikiBlockTypes.BulletedListItem, q)));
        blocks.Add(Block(WikiBlockTypes.Paragraph, string.Empty));
        var page = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = gap.Topic,
            BlocksJson = WikiBlockJson.Serialize(blocks)
        }, username, createRevisionCheckpoint: true, cancellationToken);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var ids = gap.QuestionIds.ToList();
        await dbContext.SentinelQuestions.Where(q => ids.Contains(q.Id)).ExecuteUpdateAsync(s => s.SetProperty(q => q.ResolvedUnix, now), cancellationToken);
        return page.Id;
    }

    // ---- Access ----

    private async Task EnsureEditAsync(Guid pageId, string username, CancellationToken ct)
    {
        if (accessService is not null && !await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.Edit, ct))
            throw new UnauthorizedAccessException("You need edit access to change this page's tags.");
    }

    private async Task<HashSet<Guid>> VisibleAsync(IEnumerable<Guid> ids, string username, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (accessService is null || list.Count == 0) return list.ToHashSet();
        var allowed = await accessService.GetAccessibleTargetsAsync(list.Select(id => new SentinelAccessTarget(id, IsDatabase: false)).ToList(), username, SentinelAccessLevels.View, ct);
        return allowed.Select(t => t.TargetId).ToHashSet();
    }
}
