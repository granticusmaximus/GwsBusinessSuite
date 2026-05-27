using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Wiki;

public sealed class WikiService(IAppDbContext dbContext) : IWikiService
{
    public async Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default)
    {
        var pages = await dbContext.WikiPages
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return pages
            .OrderByDescending(page => page.UpdatedAt ?? page.CreatedAt)
            .ThenBy(page => page.Title)
            .ToList();
    }

    public async Task<WikiPage?> GetPageAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        return await dbContext.WikiPages
            .AsNoTracking()
            .FirstOrDefaultAsync(page => page.Id == wikiPageId, cancellationToken);
    }

    public async Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);

        var now = DateTimeOffset.UtcNow;
        var page = editor.WikiPageId is { } wikiPageId
            ? await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            : null;

        var isNew = page is null;
        page ??= new WikiPage
        {
            Title = string.Empty,
            Slug = string.Empty,
            CreatedAt = now,
            CreatedBy = "wiki-ui"
        };

        var requestedSlug = string.IsNullOrWhiteSpace(editor.Slug)
            ? CreateSlug(editor.Title)
            : CreateSlug(editor.Slug);
        var uniqueSlug = await GetUniqueSlugAsync(requestedSlug, page.Id, cancellationToken);

        page.Title = editor.Title.Trim();
        page.Slug = uniqueSlug;
        page.Markdown = editor.Markdown.Trim();
        page.UpdatedAt = now;
        page.UpdatedBy = "wiki-ui";

        if (isNew)
        {
            await dbContext.WikiPages.AddAsync(page, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return page;
    }

    public async Task DeletePageAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        dbContext.WikiPages.Remove(page);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static string CreateSlug(string value)
    {
        var slug = value.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(slug.Length);
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
