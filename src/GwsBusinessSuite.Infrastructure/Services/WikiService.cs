using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// DB-snapshot history (WikiPageRevision), mirroring CmsPageRevision/PageRevisionService's
// MaxRevisionsPerPage trim-on-save pattern - replaces the old git-commit-per-save model
// (see git history for the LibGit2Sharp version) now that page content is structured
// WikiBlock JSON rather than a single Markdown string that read well as a prose diff.
public sealed class WikiService(IAppDbContext dbContext) : IWikiService
{
    private const int MaxRevisionsPerPage = 20;

    public async Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default)
    {
        var pages = await dbContext.WikiPages
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return pages
            .OrderBy(page => page.ParentWikiPageId.HasValue)
            .ThenBy(page => page.SortOrder)
            .ThenBy(page => page.Title)
            .ToList();
    }

    public async Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WikiPages
            .AsNoTracking()
            .FirstOrDefaultAsync(page => page.Id == wikiPageId, cancellationToken);
    }

    public async Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, string performedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        WikiPage page;
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
            if (page.ContentVersion != editor.ExpectedContentVersion)
            {
                throw CreateConcurrencyException(page, editor.ExpectedContentVersion);
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
        await CreateRevisionAsync(page, performedBy, cancellationToken);

        return page;
    }

    public async Task DeletePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        // WikiPageRevisions cascade-delete via the FK configured in ApplicationDbContext.
        dbContext.WikiPages.Remove(page);
        await dbContext.SaveChangesAsync(cancellationToken);
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

    private async Task TrimOldRevisionsAsync(Guid wikiPageId, CancellationToken cancellationToken)
    {
        var revisions = await dbContext.WikiPageRevisions
            .Where(revision => revision.WikiPageId == wikiPageId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToListAsync(cancellationToken);

        if (revisions.Count <= MaxRevisionsPerPage)
        {
            return;
        }

        var toDelete = revisions.Skip(MaxRevisionsPerPage).ToList();
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
        var slugs = await dbContext.WikiPages
            .Where(page => page.Id != currentPageId)
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
