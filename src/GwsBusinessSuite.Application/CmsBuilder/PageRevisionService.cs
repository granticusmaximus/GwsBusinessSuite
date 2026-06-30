using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.CmsBuilder;

public sealed class PageRevisionService(IAppDbContext dbContext) : IPageRevisionService
{
    // Trim to this many revisions per page after every auto-save so the table doesn't
    // grow unboundedly on frequently-edited pages.
    private const int MaxRevisionsPerPage = 20;

    public async Task<CmsPageRevision> CreateRevisionAsync(
        CmsPage currentPage,
        string label = "",
        CancellationToken cancellationToken = default)
    {
        var nextNumber = await GetNextRevisionNumberAsync(currentPage.Id, cancellationToken);

        var revision = new CmsPageRevision
        {
            PageId = currentPage.Id,
            RevisionNumber = nextNumber,
            Title = currentPage.Title,
            Slug = currentPage.Slug,
            BlocksJson = currentPage.BlocksJson,
            MetaTitle = currentPage.MetaTitle,
            MetaDescription = currentPage.MetaDescription,
            OgImageUrl = currentPage.OgImageUrl,
            CustomCss = currentPage.CustomCss,
            Label = label?.Trim() ?? string.Empty,
            CreatedBy = "cms-page-revision"
        };

        await dbContext.CmsPageRevisions.AddAsync(revision, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await TrimOldRevisionsAsync(currentPage.Id, cancellationToken);

        return revision;
    }

    public async Task<IReadOnlyList<CmsPageRevision>> ListAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.CmsPageRevisions
            .AsNoTracking()
            .Where(revision => revision.PageId == pageId)
            .ToListAsync(cancellationToken);

        return revisions
            .OrderByDescending(revision => revision.RevisionNumber)
            .ToList();
    }

    public async Task<CmsPageRevision?> GetAsync(Guid revisionId, CancellationToken cancellationToken = default)
    {
        return await dbContext.CmsPageRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(revision => revision.Id == revisionId, cancellationToken);
    }

    public async Task<CmsPage> RestoreAsync(Guid pageId, Guid revisionId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.CmsPages
            .FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken)
            ?? throw new InvalidOperationException("The page no longer exists.");

        var revision = await dbContext.CmsPageRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == revisionId && r.PageId == pageId, cancellationToken)
            ?? throw new InvalidOperationException("The revision no longer exists.");

        // Checkpoint the CURRENT state before overwriting so the restore is itself reversible.
        await CreateRevisionAsync(page, $"Auto-save before restoring revision #{revision.RevisionNumber}", cancellationToken);

        page.BlocksJson = revision.BlocksJson;
        page.MetaTitle = revision.MetaTitle;
        page.MetaDescription = revision.MetaDescription;
        page.OgImageUrl = revision.OgImageUrl;
        page.CustomCss = revision.CustomCss;
        page.UpdatedAt = DateTimeOffset.UtcNow;
        page.UpdatedBy = "cms-revision-restore";

        await dbContext.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task DeleteAsync(Guid revisionId, CancellationToken cancellationToken = default)
    {
        var revision = await dbContext.CmsPageRevisions
            .FirstOrDefaultAsync(r => r.Id == revisionId, cancellationToken);

        if (revision is null) return;

        dbContext.CmsPageRevisions.Remove(revision);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAllForPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var revisions = await dbContext.CmsPageRevisions
            .Where(revision => revision.PageId == pageId)
            .ToListAsync(cancellationToken);

        if (revisions.Count > 0)
        {
            dbContext.CmsPageRevisions.RemoveRange(revisions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<int> GetNextRevisionNumberAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var revisions = await dbContext.CmsPageRevisions
            .Where(r => r.PageId == pageId)
            .Select(r => r.RevisionNumber)
            .ToListAsync(cancellationToken);

        return revisions.Count == 0 ? 1 : revisions.Max() + 1;
    }

    private async Task TrimOldRevisionsAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var revisions = await dbContext.CmsPageRevisions
            .Where(r => r.PageId == pageId)
            .ToListAsync(cancellationToken);

        if (revisions.Count <= MaxRevisionsPerPage) return;

        var toDelete = revisions
            .OrderBy(r => r.RevisionNumber)
            .Take(revisions.Count - MaxRevisionsPerPage)
            .ToList();

        dbContext.CmsPageRevisions.RemoveRange(toDelete);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
