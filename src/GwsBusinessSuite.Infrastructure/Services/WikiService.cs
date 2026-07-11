using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// The DB (WikiPage.Markdown) stays the source of truth for current-state reads - those
// paths are unchanged from before this class started managing git history. A local git
// repo is a side channel that SavePageAsync/DeletePageAsync write one commit to on every
// change, and that history/diff/revert reads from live (no bounded DB revision table,
// unlike CmsPageRevision - git history here is unbounded, matching OtterWiki).
public sealed class WikiService(IAppDbContext dbContext, string repoPath) : IWikiService
{
    private const string CommitAuthorEmail = "wiki@grantwatson.dev";

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

    public async Task<WikiPage> SavePageAsync(WikiPageEditorModel editor, string performedBy, CancellationToken cancellationToken = default)
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
            CreatedBy = performedBy
        };

        var previousSlug = page.Slug;

        var requestedSlug = string.IsNullOrWhiteSpace(editor.Slug)
            ? CreateSlug(editor.Title)
            : CreateSlug(editor.Slug);
        var uniqueSlug = await GetUniqueSlugAsync(requestedSlug, page.Id, cancellationToken);

        page.Title = editor.Title.Trim();
        page.Slug = uniqueSlug;
        page.Markdown = editor.Markdown.Trim();
        page.ParentWikiPageId = editor.ParentWikiPageId;
        page.UpdatedAt = now;
        page.UpdatedBy = performedBy;

        if (isNew)
        {
            await dbContext.WikiPages.AddAsync(page, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        CommitPageToGit(
            page,
            previousSlugToRemove: !isNew && !string.Equals(previousSlug, page.Slug, StringComparison.Ordinal) ? previousSlug : null,
            message: isNew ? $"Create {page.Title}" : $"Update {page.Title}",
            performedBy);

        return page;
    }

    public async Task DeletePageAsync(Guid wikiPageId, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        dbContext.WikiPages.Remove(page);
        await dbContext.SaveChangesAsync(cancellationToken);

        DeletePageFromGit(page, performedBy);
    }

    public async Task<IReadOnlyList<WikiRevisionView>> GetHistoryAsync(Guid wikiPageId, CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(wikiPageId, cancellationToken);
        if (page is null)
        {
            return [];
        }

        using var repo = OpenOrInitRepository();
        if (repo.Head.Tip is null)
        {
            return [];
        }

        // The default QueryBy(path) overload uses LibGit2Sharp's rename-following
        // "FullHistory" algorithm, which has a longstanding upstream bug that throws
        // KeyNotFoundException on some perfectly ordinary linear histories (see
        // libgit2/libgit2sharp#1410/#1401/#1599) - forcing a topological sort avoids it.
        return repo.Commits
            .QueryBy(FileNameFor(page.Slug), new CommitFilter { SortBy = CommitSortStrategies.Topological })
            .Select(entry => new WikiRevisionView
            {
                Sha = entry.Commit.Sha,
                Message = entry.Commit.MessageShort,
                AuthorName = entry.Commit.Author.Name,
                When = entry.Commit.Author.When
            })
            .ToList();
    }

    public async Task<string?> GetDiffAsync(Guid wikiPageId, string fromSha, string toSha, CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(wikiPageId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        using var repo = OpenOrInitRepository();
        var fromCommit = repo.Lookup<Commit>(fromSha);
        var toCommit = repo.Lookup<Commit>(toSha);
        if (fromCommit is null || toCommit is null)
        {
            return null;
        }

        var fileName = FileNameFor(page.Slug);
        using var patch = repo.Diff.Compare<Patch>(fromCommit.Tree, toCommit.Tree);
        var entry = patch.FirstOrDefault(change => change.Path == fileName);
        return entry?.Patch;
    }

    public async Task<string?> GetRevisionContentAsync(Guid wikiPageId, string sha, CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(wikiPageId, cancellationToken);
        if (page is null)
        {
            return null;
        }

        using var repo = OpenOrInitRepository();
        var commit = repo.Lookup<Commit>(sha);
        var entry = commit?.Tree[FileNameFor(page.Slug)];
        return entry?.Target is Blob blob ? blob.GetContentText() : null;
    }

    public async Task<WikiPage> RevertToRevisionAsync(Guid wikiPageId, string sha, string performedBy, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(item => item.Id == wikiPageId, cancellationToken)
            ?? throw new InvalidOperationException("The wiki page no longer exists.");

        var oldContent = await GetRevisionContentAsync(wikiPageId, sha, cancellationToken)
            ?? throw new InvalidOperationException("That revision no longer exists.");

        return await SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id,
            Title = page.Title,
            Slug = page.Slug,
            Markdown = oldContent,
            ParentWikiPageId = page.ParentWikiPageId
        }, performedBy, cancellationToken);
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

    private static string FileNameFor(string slug) => $"{slug}.md";

    private Repository OpenOrInitRepository()
    {
        Directory.CreateDirectory(repoPath);
        if (!Repository.IsValid(repoPath))
        {
            Repository.Init(repoPath);
        }

        return new Repository(repoPath);
    }

    private void CommitPageToGit(WikiPage page, string? previousSlugToRemove, string message, string performedBy)
    {
        using var repo = OpenOrInitRepository();

        if (previousSlugToRemove is not null)
        {
            var oldPath = Path.Combine(repoPath, FileNameFor(previousSlugToRemove));
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
            }
        }

        File.WriteAllText(Path.Combine(repoPath, FileNameFor(page.Slug)), page.Markdown);
        Commands.Stage(repo, "*");
        TryCommit(repo, message, performedBy);
    }

    private void DeletePageFromGit(WikiPage page, string performedBy)
    {
        using var repo = OpenOrInitRepository();

        var path = Path.Combine(repoPath, FileNameFor(page.Slug));
        if (!File.Exists(path))
        {
            return;
        }

        File.Delete(path);
        Commands.Stage(repo, "*");
        TryCommit(repo, $"Delete {page.Title}", performedBy);
    }

    private static void TryCommit(Repository repo, string message, string performedBy)
    {
        if (!repo.RetrieveStatus().IsDirty)
        {
            // Nothing actually changed (e.g. saving with identical content) - Commit()
            // throws EmptyCommitException in that case, so skip it rather than catch it.
            return;
        }

        var signature = new Signature(
            string.IsNullOrWhiteSpace(performedBy) ? "wiki" : performedBy, CommitAuthorEmail, DateTimeOffset.UtcNow);
        repo.Commit(message, signature, signature);
    }
}
