using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// DB-snapshot history (WikiPageRevision) - replaces the old git-commit-per-save model (see git
// history for the LibGit2Sharp version) now that page content is structured WikiBlock JSON
// rather than a single Markdown string that read well as a prose diff. Originally trimmed with
// a flat MaxRevisionsPerPage cap mirroring CmsPageRevision/PageRevisionService's own pattern;
// see TrimOldRevisionsAsync for the time-tiered policy that replaced it.
public sealed class WikiService(IAppDbContext dbContext, IWikiSyncedBlockService? syncedBlockService = null) : IWikiService
{
    // Was a flat 20-revision cap (hard-deleted anything past it); replaced in Phase 4.4 with a
    // time-tiered policy closer to Notion's own page history - see TrimOldRevisionsAsync.
    private static readonly TimeSpan RecentRevisionRetentionWindow = TimeSpan.FromDays(90);
    private const string SyncedBlockSourceIdProp = "sourceId";

    public async Task<IReadOnlyList<WikiPage>> ListPagesAsync(bool includeTrashed = false, CancellationToken cancellationToken = default)
    {
        var query = dbContext.WikiPages.AsNoTracking();
        if (!includeTrashed)
        {
            query = query.Where(page => page.TrashedAt == null);
        }
        var pages = await query.ToListAsync(cancellationToken);

        return pages
            .OrderBy(page => page.ParentWikiPageId.HasValue)
            .ThenBy(page => page.SortOrder)
            .ThenBy(page => page.Title)
            .ToList();
    }

    public async Task<IReadOnlyList<WikiPage>> ListTrashedPagesAsync(CancellationToken cancellationToken = default)
    {
        var pages = await dbContext.WikiPages
            .AsNoTracking()
            .Where(page => page.TrashedAt != null)
            .ToListAsync(cancellationToken);

        // SQLite can't translate ORDER BY on a DateTimeOffset column - order client-side
        // after materializing (same pattern used throughout this app).
        return pages.OrderByDescending(page => page.TrashedAt).ToList();
    }

    // Excludes trashed pages - opening a stale link/tab for a page that's since been trashed
    // should behave as "not found", not silently show/operate on it (same posture as
    // CrmService.GetContactAsync).
    public async Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages
            .AsNoTracking()
            .FirstOrDefaultAsync(page => page.Id == wikiPageId && page.TrashedAt == null, cancellationToken);
        if (page is not null)
        {
            page.BlocksJson = await HydrateSyncedBlocksAsync(page.BlocksJson, cancellationToken);
        }
        return page;
    }

    // Every synced-block instance's own RichText is a stale local snapshot at best - the shared
    // WikiSyncedBlockSource row is the only thing that's ever authoritative. Rewriting it here,
    // on every read, is what makes "edit any instance, every instance updates" true without any
    // explicit fan-out write to other pages that might also embed the same source.
    private async Task<string> HydrateSyncedBlocksAsync(string blocksJson, CancellationToken cancellationToken)
    {
        if (syncedBlockService is null)
        {
            return blocksJson;
        }

        var blocks = WikiBlockJson.ParseBlocks(blocksJson);
        var sourceIds = blocks
            .Select(TryGetSyncedBlockSourceId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (sourceIds.Count == 0)
        {
            return blocksJson;
        }

        var content = await syncedBlockService.GetContentBatchAsync(sourceIds, cancellationToken);
        if (content.Count == 0)
        {
            return blocksJson;
        }

        var hydrated = blocks
            .Select(block => TryGetSyncedBlockSourceId(block) is { } sourceId && content.TryGetValue(sourceId, out var richText)
                ? block with { RichText = richText }
                : block)
            .ToList();
        return WikiBlockJson.Serialize(hydrated);
    }

    // The inverse of hydration: on save, any synced-block instance's just-edited RichText is
    // pushed into its shared source so every OTHER instance (including ones on other pages)
    // picks it up the next time it's read. The page's own persisted copy is left as-is - it's
    // always overwritten again on the next GetPageAsync, so there's nothing to keep tidy here.
    //
    // Only instances whose content actually changed from this same page's own previous save are
    // propagated. Without that guard, a block that's new to this page (most notably a duplicated
    // page's synced-block instances, cloned straight from a possibly-stale on-disk copy without
    // ever being hydrated first) would blindly overwrite the shared source with a stale or blank
    // local snapshot instead of leaving the real content alone - it will pick up the true content
    // itself the next time anyone reads it, via HydrateSyncedBlocksAsync.
    private async Task PropagateSyncedBlocksAsync(
        string previousBlocksJson, string blocksJson, string performedBy, CancellationToken cancellationToken)
    {
        if (syncedBlockService is null)
        {
            return;
        }

        var previousById = WikiBlockJson.ParseBlocks(previousBlocksJson).ToDictionary(block => block.Id);
        foreach (var block in WikiBlockJson.ParseBlocks(blocksJson))
        {
            if (TryGetSyncedBlockSourceId(block) is not { } sourceId)
            {
                continue;
            }

            if (previousById.TryGetValue(block.Id, out var previous))
            {
                if (previous.RichText.SequenceEqual(block.RichText))
                {
                    continue;
                }
            }
            else if (block.RichText.Count == 0)
            {
                continue;
            }

            await syncedBlockService.UpdateContentAsync(sourceId, block.RichText, performedBy, cancellationToken);
        }
    }

    private static Guid? TryGetSyncedBlockSourceId(WikiBlock block) =>
        block.Type == WikiBlockTypes.SyncedBlock
            && block.Props.TryGetValue(SyncedBlockSourceIdProp, out var raw)
            && Guid.TryParse(raw, out var sourceId)
            ? sourceId
            : null;

    public async Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, string performedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        WikiPage page;
        var previousBlocksJson = "[]";
        var isNew = !editor.WikiPageId.HasValue;
        if (editor.WikiPageId is { } wikiPageId)
        {
            if (editor.ExpectedContentVersion <= 0)
            {
                throw new ArgumentException("An expected content version is required when updating a Sentinel page.", nameof(editor));
            }

            page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
                ?? throw new InvalidOperationException("The Sentinel page no longer exists.");
            // A Blazor circuit keeps one scoped DbContext for longer than a normal HTTP request.
            // Reload before comparing so an entity tracked by an earlier save cannot conceal a
            // change committed by another circuit.
            await ReloadAsync(page, cancellationToken);
            if (page.TrashedAt is not null)
            {
                // A stale editor tab (opened before the page was trashed elsewhere) shouldn't
                // be able to silently keep editing a trashed page - restore it first.
                throw new InvalidOperationException("This Sentinel page has been moved to Trash. Restore it before saving changes.");
            }
            previousBlocksJson = page.BlocksJson;
            if (page.ContentVersion != editor.ExpectedContentVersion)
            {
                var metadataStillCurrent = string.Equals(editor.Title.Trim(), page.Title, StringComparison.Ordinal)
                    && string.Equals(CreateSlug(editor.Slug), page.Slug, StringComparison.Ordinal)
                    && string.Equals(editor.Icon?.Trim(), page.Icon, StringComparison.Ordinal)
                    && string.Equals(editor.CoverImageUrl?.Trim(), page.CoverImageUrl, StringComparison.Ordinal);
                var merge = editor.BaseBlocksJson is null || !metadataStillCurrent
                    ? new WikiBlockMergeResult(false, editor.BlocksJson, [])
                    : WikiBlockMerge.ThreeWayMerge(editor.BaseBlocksJson, editor.BlocksJson, page.BlocksJson);
                if (!merge.IsSuccess)
                {
                    throw CreateConcurrencyException(page, editor.ExpectedContentVersion);
                }
                editor.BlocksJson = merge.MergedBlocksJson;
                editor.ExpectedContentVersion = page.ContentVersion;
            }
        }
        else
        {
            page = new WikiPage
            {
                Title = string.Empty,
                Slug = string.Empty,
                ContentVersion = 1,
                CreatedAt = now,
                CreatedBy = performedBy,
                SortOrder = await NextSortOrderAsync(editor.ParentWikiPageId, cancellationToken)
            };
        }

        var requestedSlug = string.IsNullOrWhiteSpace(editor.Slug)
            ? CreateSlug(editor.Title)
            : CreateSlug(editor.Slug);
        var uniqueSlug = await GetUniqueSlugAsync(requestedSlug, page.Id, cancellationToken);

        page.Title = editor.Title.Trim();
        page.Slug = uniqueSlug;
        page.BlocksJson = string.IsNullOrWhiteSpace(editor.BlocksJson) ? "[]" : editor.BlocksJson;
        page.Icon = string.IsNullOrWhiteSpace(editor.Icon) ? null : editor.Icon.Trim();
        page.CoverImageUrl = string.IsNullOrWhiteSpace(editor.CoverImageUrl) ? null : editor.CoverImageUrl.Trim();
        page.UpdatedAt = now;
        page.UpdatedBy = performedBy;

        if (isNew)
        {
            // Only set on create - once a page exists, moving it to a different parent/position
            // goes through ReorderPageAsync instead, which renumbers siblings and guards
            // against cycles. Letting a content save silently re-parent it too would leave
            // stale/colliding SortOrder values under whichever parent it lands on.
            page.ParentWikiPageId = editor.ParentWikiPageId;
            await dbContext.WikiPages.AddAsync(page, cancellationToken);
        }
        else
        {
            page.ContentVersion++;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await ReloadAsync(page, cancellationToken);
            throw CreateConcurrencyException(page, editor.ExpectedContentVersion);
        }
        await PropagateSyncedBlocksAsync(previousBlocksJson, page.BlocksJson, performedBy, cancellationToken);
        if (!isNew && !string.Equals(previousBlocksJson, page.BlocksJson, StringComparison.Ordinal))
        {
            await ReanchorDiscussionsAsync(page.Id, previousBlocksJson, page.BlocksJson, cancellationToken);
        }
        await CreateRevisionAsync(page, performedBy, cancellationToken);

        return page;
    }

    private async Task ReanchorDiscussionsAsync(
        Guid wikiPageId,
        string previousBlocksJson,
        string currentBlocksJson,
        CancellationToken cancellationToken)
    {
        var previousById = WikiBlockJson.ParseBlocks(previousBlocksJson).ToDictionary(block => block.Id);
        var currentById = WikiBlockJson.ParseBlocks(currentBlocksJson).ToDictionary(block => block.Id);
        var discussions = await dbContext.SentinelDiscussions
            .Where(discussion => discussion.WikiPageId == wikiPageId
                && discussion.BlockId != null
                && discussion.AnchorText != null
                && discussion.AnchorStart != null
                && discussion.AnchorEnd != null)
            .ToListAsync(cancellationToken);

        foreach (var discussion in discussions)
        {
            if (discussion.BlockId is not { } blockId
                || !previousById.TryGetValue(blockId, out var previousBlock)
                || !currentById.TryGetValue(blockId, out var currentBlock))
            {
                continue;
            }

            var rebased = SentinelDiscussionAnchorRebaser.Rebase(
                new SentinelDiscussionAnchor(
                    discussion.AnchorText!,
                    discussion.AnchorStart!.Value,
                    discussion.AnchorEnd!.Value),
                previousBlock.PlainText,
                currentBlock.PlainText);
            if (rebased is null)
            {
                continue;
            }

            discussion.AnchorStart = rebased.Start;
            discussion.AnchorEnd = rebased.End;
            discussion.UpdatedAt = DateTimeOffset.UtcNow;
            discussion.UpdatedBy = "anchor-rebase";
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<WikiPage> DuplicatePageAsync(
        Guid wikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var pages = await dbContext.WikiPages
            .AsNoTracking()
            .Where(page => page.TrashedAt == null)
            .ToListAsync(cancellationToken);
        var source = pages.FirstOrDefault(page => page.Id == wikiPageId)
            ?? throw new InvalidOperationException("The Sentinel page no longer exists.");
        var childrenByParent = pages
            .Where(page => page.ParentWikiPageId.HasValue)
            .GroupBy(page => page.ParentWikiPageId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(page => page.SortOrder).ThenBy(page => page.Title).ToList());

        await using var transaction = await dbContext.BeginTransactionAsync(cancellationToken);
        try
        {
            var visited = new HashSet<Guid>();
            var duplicated = await DuplicateBranchAsync(
                source,
                source.ParentWikiPageId,
                $"{source.Title} (copy)",
                childrenByParent,
                visited,
                performedBy,
                depth: 0,
                cancellationToken);
            await ReorderPageAsync(
                duplicated.Id,
                source.ParentWikiPageId,
                source.SortOrder + 1,
                performedBy,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return duplicated;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            // A Blazor circuit can retain this scoped DbContext after the failed action. EF's
            // entity states are not rewound by a database rollback, so discard the rolled-back
            // clones and sibling-order values before any later circuit action reuses the scope.
            if (dbContext is DbContext efContext)
            {
                efContext.ChangeTracker.Clear();
            }
            throw;
        }
    }

    public async Task TrashPageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken);
        if (page is null || page.TrashedAt is not null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var descendantPageIds = await GetDescendantPageIdsAsync(wikiPageId, cancellationToken);
        var subtreePageIds = new HashSet<Guid>(descendantPageIds) { wikiPageId };

        // Trashing a page takes its whole subtree with it - both the descendant pages and any
        // database parented anywhere in that subtree - so a trashed branch disappears together
        // instead of leaving orphaned-looking children still visible in the live tree.
        var pagesToTrash = await dbContext.WikiPages
            .Where(item => subtreePageIds.Contains(item.Id) && item.TrashedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var item in pagesToTrash)
        {
            item.TrashedAt = now;
            item.UpdatedAt = now;
            item.UpdatedBy = performedBy;
        }

        var databasesToTrash = await dbContext.WikiDatabases
            .Where(item => item.ParentWikiPageId != null
                && subtreePageIds.Contains(item.ParentWikiPageId!.Value)
                && item.TrashedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var item in databasesToTrash)
        {
            item.TrashedAt = now;
            item.UpdatedAt = now;
            item.UpdatedBy = performedBy;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RestorePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken);
        if (page is null || page.TrashedAt is null)
        {
            return;
        }

        // Restoring is per-item, not cascading - a page's (also-trashed) descendants stay
        // trashed until restored individually. If this page's original parent is itself still
        // trashed (or gone), reparent to the workspace root instead of coming back invisible
        // under a parent nobody can see.
        if (page.ParentWikiPageId is { } parentId)
        {
            var parentIsAvailable = await dbContext.WikiPages.AsNoTracking()
                .AnyAsync(item => item.Id == parentId && item.TrashedAt == null, cancellationToken);
            if (!parentIsAvailable)
            {
                page.ParentWikiPageId = null;
            }
        }

        page.TrashedAt = null;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = performedBy;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeletePagePermanentlyAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        // WikiPageRevisions cascade-delete via the FK configured in ApplicationDbContext.
        // SentinelResourcePermissions/SentinelPublicShares reference this page polymorphically
        // via TargetId+IsDatabase (they can point at either a WikiPage or a WikiDatabase), so a
        // real FK isn't possible - clean them up manually or they're dangling rows forever.
        await RemoveSentinelAccessRowsAsync(wikiPageId, isDatabase: false, cancellationToken);

        dbContext.WikiPages.Remove(page);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<HashSet<Guid>> GetDescendantPageIdsAsync(Guid rootPageId, CancellationToken cancellationToken)
    {
        var childIdsByParent = (await dbContext.WikiPages.AsNoTracking()
                .Where(page => page.ParentWikiPageId != null)
                .Select(page => new { page.Id, page.ParentWikiPageId })
                .ToListAsync(cancellationToken))
            .GroupBy(item => item.ParentWikiPageId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToList());

        var descendants = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(rootPageId);
        var guard = 0;
        while (frontier.Count > 0 && guard++ < 10_000)
        {
            if (!childIdsByParent.TryGetValue(frontier.Dequeue(), out var children))
            {
                continue;
            }
            foreach (var childId in children)
            {
                if (descendants.Add(childId))
                {
                    frontier.Enqueue(childId);
                }
            }
        }

        return descendants;
    }

    private async Task RemoveSentinelAccessRowsAsync(Guid targetId, bool isDatabase, CancellationToken cancellationToken)
    {
        var permissions = await dbContext.SentinelResourcePermissions
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .ToListAsync(cancellationToken);
        if (permissions.Count > 0)
        {
            dbContext.SentinelResourcePermissions.RemoveRange(permissions);
        }

        var shares = await dbContext.SentinelPublicShares
            .Where(item => item.TargetId == targetId && item.IsDatabase == isDatabase)
            .ToListAsync(cancellationToken);
        if (shares.Count > 0)
        {
            dbContext.SentinelPublicShares.RemoveRange(shares);
        }
    }

    public async Task ReorderPageAsync(
        Guid wikiPageId,
        Guid? newParentWikiPageId,
        int newSortOrder,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The wiki page no longer exists.");

        if (newParentWikiPageId == wikiPageId)
        {
            throw new InvalidOperationException("A page cannot be its own parent.");
        }
        if (newParentWikiPageId is { } candidateParentId && await IsDescendantAsync(wikiPageId, candidateParentId, cancellationToken))
        {
            throw new InvalidOperationException("Cannot move a page under one of its own descendants.");
        }

        var siblings = await dbContext.WikiPages
            .Where(item => item.ParentWikiPageId == newParentWikiPageId && item.Id != wikiPageId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);

        siblings.Insert(Math.Clamp(newSortOrder, 0, siblings.Count), page);

        var now = DateTimeOffset.UtcNow;
        page.ParentWikiPageId = newParentWikiPageId;
        for (var index = 0; index < siblings.Count; index++)
        {
            siblings[index].SortOrder = index;
            siblings[index].UpdatedAt = now;
            siblings[index].UpdatedBy = performedBy;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WikiRevisionView>> GetHistoryAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.WikiPageRevisions
            .AsNoTracking()
            .Where(revision => revision.WikiPageId == wikiPageId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);

        return revisions
            .Select(revision => new WikiRevisionView
            {
                Id = revision.Id,
                RevisionNumber = revision.RevisionNumber,
                Label = revision.Label,
                AuthorName = revision.CreatedBy,
                When = revision.CreatedAt
            })
            .ToList();
    }

    public async Task<string?> GetStructuralDiffAsync(
        Guid wikiPageId,
        Guid fromRevisionId,
        Guid toRevisionId,
        CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.WikiPageRevisions
            .AsNoTracking()
            .Where(revision => revision.WikiPageId == wikiPageId && (revision.Id == fromRevisionId || revision.Id == toRevisionId))
            .ToListAsync(cancellationToken);

        var from = revisions.FirstOrDefault(revision => revision.Id == fromRevisionId);
        var to = revisions.FirstOrDefault(revision => revision.Id == toRevisionId);
        if (from is null || to is null)
        {
            return null;
        }

        return BuildStructuralDiff(WikiBlockJson.ParseBlocks(from.BlocksJson), WikiBlockJson.ParseBlocks(to.BlocksJson));
    }

    public async Task<WikiPage> RevertToRevisionAsync(Guid wikiPageId, Guid revisionId, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The wiki page no longer exists.");
        var revision = await dbContext.WikiPageRevisions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == revisionId && item.WikiPageId == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("That revision no longer exists.");

        return await SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id,
            Title = revision.Title,
            Slug = revision.Slug,
            BlocksJson = revision.BlocksJson,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            ParentWikiPageId = page.ParentWikiPageId,
            ExpectedContentVersion = page.ContentVersion
        }, performedBy, cancellationToken);
    }

    private async Task ReloadAsync(WikiPage page, CancellationToken cancellationToken)
    {
        if (dbContext is not DbContext efContext)
        {
            throw new InvalidOperationException("Sentinel concurrency requires an EF Core DbContext.");
        }
        await efContext.Entry(page).ReloadAsync(cancellationToken);
    }

    private async Task<WikiPage> DuplicateBranchAsync(
        WikiPage source,
        Guid? newParentWikiPageId,
        string title,
        IReadOnlyDictionary<Guid, List<WikiPage>> childrenByParent,
        HashSet<Guid> visited,
        string performedBy,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > 128 || !visited.Add(source.Id))
        {
            throw new InvalidOperationException("The Sentinel page tree contains a cycle and cannot be duplicated.");
        }

        var clonedBlocks = WikiBlockJson.ParseBlocks(source.BlocksJson)
            .Select(block => block with { Id = Guid.NewGuid() })
            .ToList();
        var duplicate = await SavePageAsync(new WikiPageEditorModel
        {
            Title = title,
            BlocksJson = WikiBlockJson.Serialize(clonedBlocks),
            Icon = source.Icon,
            CoverImageUrl = source.CoverImageUrl,
            ParentWikiPageId = newParentWikiPageId
        }, performedBy, cancellationToken);

        if (childrenByParent.TryGetValue(source.Id, out var children))
        {
            foreach (var child in children)
            {
                await DuplicateBranchAsync(
                    child,
                    duplicate.Id,
                    child.Title,
                    childrenByParent,
                    visited,
                    performedBy,
                    depth + 1,
                    cancellationToken);
            }
        }

        return duplicate;
    }

    private static WikiPageConcurrencyException CreateConcurrencyException(WikiPage page, long expectedVersion) =>
        new(new WikiPageConflictSnapshot(
            page.Id,
            expectedVersion,
            page.ContentVersion,
            page.Title,
            page.BlocksJson,
            page.UpdatedAt,
            page.UpdatedBy));

    private async Task CreateRevisionAsync(WikiPage page, string performedBy, CancellationToken cancellationToken)
    {
        var nextNumber = await dbContext.WikiPageRevisions
            .Where(revision => revision.WikiPageId == page.Id)
            .Select(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken) is { Count: > 0 } numbers
                ? numbers.Max() + 1
                : 1;

        await dbContext.WikiPageRevisions.AddAsync(new WikiPageRevision
        {
            WikiPageId = page.Id,
            RevisionNumber = nextNumber,
            Title = page.Title,
            Slug = page.Slug,
            BlocksJson = page.BlocksJson,
            CreatedBy = performedBy
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await TrimOldRevisionsAsync(page.Id, cancellationToken);
    }

    // Every revision from the last RecentRevisionRetentionWindow is kept in full, however many
    // that is - real day-to-day "what changed an hour ago" history is effectively unbounded.
    // Only revisions OLDER than that window are thinned, down to at most one (the latest) per
    // calendar day, which bounds long-term storage growth (e.g. against a runaway automation
    // save loop) without silently deleting a page's entire history the way the old flat
    // 20-revision cap did.
    private async Task TrimOldRevisionsAsync(Guid wikiPageId, CancellationToken cancellationToken)
    {
        var revisions = await dbContext.WikiPageRevisions
            .Where(revision => revision.WikiPageId == wikiPageId)
            .ToListAsync(cancellationToken);

        // SQLite/EF Core can't translate DateTimeOffset comparisons server-side - filter and
        // group client-side after materializing, the same convention used throughout this app.
        var cutoff = DateTimeOffset.UtcNow - RecentRevisionRetentionWindow;
        var older = revisions.Where(revision => revision.CreatedAt < cutoff).ToList();
        if (older.Count == 0)
        {
            return;
        }

        var keepIds = older
            .GroupBy(revision => revision.CreatedAt.UtcDateTime.Date)
            .Select(group => group.OrderByDescending(revision => revision.RevisionNumber).First().Id)
            .ToHashSet();
        var toDelete = older.Where(revision => !keepIds.Contains(revision.Id)).ToList();
        if (toDelete.Count == 0)
        {
            return;
        }

        dbContext.WikiPageRevisions.RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string BuildStructuralDiff(IReadOnlyList<WikiBlock> from, IReadOnlyList<WikiBlock> to)
    {
        var fromById = from.ToDictionary(block => block.Id);
        var toById = to.ToDictionary(block => block.Id);
        var lines = new List<string>();

        foreach (var block in from)
        {
            if (!toById.ContainsKey(block.Id))
            {
                lines.Add($"- [{block.Type}] {WikiBlockHtmlRenderer.PlainTextPreview(block)}");
            }
        }

        foreach (var block in to)
        {
            if (!fromById.TryGetValue(block.Id, out var previous))
            {
                lines.Add($"+ [{block.Type}] {WikiBlockHtmlRenderer.PlainTextPreview(block)}");
            }
            else if (!string.Equals(WikiBlockJson.Serialize([previous]), WikiBlockJson.Serialize([block]), StringComparison.Ordinal))
            {
                lines.Add($"~ [{block.Type}] {WikiBlockHtmlRenderer.PlainTextPreview(block)}");
            }
        }

        return string.Join('\n', lines);
    }

    private async Task<int> NextSortOrderAsync(Guid? parentWikiPageId, CancellationToken cancellationToken)
    {
        var siblingOrders = await dbContext.WikiPages
            .Where(page => page.ParentWikiPageId == parentWikiPageId)
            .Select(page => page.SortOrder)
            .ToListAsync(cancellationToken);
        return siblingOrders.Count == 0 ? 0 : siblingOrders.Max() + 1;
    }

    private async Task<bool> IsDescendantAsync(Guid ancestorId, Guid candidateId, CancellationToken cancellationToken)
    {
        var parentById = await dbContext.WikiPages.AsNoTracking()
            .Select(page => new { page.Id, page.ParentWikiPageId })
            .ToDictionaryAsync(page => page.Id, page => page.ParentWikiPageId, cancellationToken);

        var current = candidateId;
        var guard = 0;
        while (parentById.TryGetValue(current, out var parent) && guard++ < 128)
        {
            if (parent is null)
            {
                return false;
            }
            if (parent == ancestorId)
            {
                return true;
            }
            current = parent.Value;
        }

        return false;
    }

    internal static string CreateSlug(string value)
    {
        var slug = value.Trim().ToLowerInvariant();
        var builder = new StringBuilder(slug.Length);
        var previousWasDash = false;

        foreach (var character in slug)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasDash = false;
                continue;
            }

            if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        var normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "wiki-page" : normalized;
    }

    private async Task<string> GetUniqueSlugAsync(string requestedSlug, Guid currentPageId, CancellationToken cancellationToken)
    {
        var baseSlug = string.IsNullOrWhiteSpace(requestedSlug) ? "wiki-page" : requestedSlug;
        // A trashed page's slug is available for reuse - it's effectively gone from the live
        // workspace. If the original is later restored while a new page now holds its old slug,
        // the restored page keeps whatever slug it already has rather than re-colliding.
        var slugs = await dbContext.WikiPages
            .Where(page => page.Id != currentPageId && page.TrashedAt == null)
            .Select(page => page.Slug)
            .ToListAsync(cancellationToken);

        if (!slugs.Contains(baseSlug, StringComparer.OrdinalIgnoreCase))
        {
            return baseSlug;
        }

        var counter = 2;
        while (true)
        {
            var candidate = $"{baseSlug}-{counter}";
            if (!slugs.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }

            counter++;
        }
    }
}
