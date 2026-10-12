using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Page aliases (Sentinel item 15). Reading is open to anyone who can see the page (the alias line
// shows under the title); changing them needs edit access, like the title.
public sealed class SentinelPageAliasService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null) : ISentinelPageAliasService
{
    public async Task<IReadOnlyList<string>> GetAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var stored = await dbContext.WikiPages.AsNoTracking().Where(p => p.Id == pageId).Select(p => p.Aliases).FirstOrDefaultAsync(cancellationToken);
        return WikiPageAliases.Parse(stored);
    }

    public async Task<SentinelAliasUpdate> SetAsync(Guid pageId, IEnumerable<string> aliases, string username, CancellationToken cancellationToken = default)
    {
        var page = await dbContext.WikiPages.FirstOrDefaultAsync(p => p.Id == pageId && p.TrashedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("The page no longer exists.");
        if (accessService is not null
            && !await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.Edit, cancellationToken))
            throw new UnauthorizedAccessException("You need edit access to change this page's aliases.");

        var normalized = WikiPageAliases.Normalize(aliases, page.Title);
        page.Aliases = WikiPageAliases.Serialize(normalized);
        page.UpdatedAt = timeProvider.GetUtcNow();
        page.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);

        var shared = new List<string>();
        if (normalized.Count > 0)
        {
            var others = await dbContext.WikiPages.AsNoTracking()
                .Where(p => p.Id != pageId && p.TrashedAt == null)
                .Select(p => new { p.Title, p.Aliases })
                .ToListAsync(cancellationToken);
            foreach (var alias in normalized)
            {
                var clash = others.FirstOrDefault(o => string.Equals(o.Title?.Trim(), alias, StringComparison.OrdinalIgnoreCase)
                    || WikiPageAliases.Parse(o.Aliases).Contains(alias, StringComparer.OrdinalIgnoreCase));
                if (clash is not null) shared.Add($"{alias} (also \"{(string.IsNullOrWhiteSpace(clash.Title) ? "Untitled" : clash.Title)}\")");
            }
        }
        return new SentinelAliasUpdate(normalized, shared);
    }
}
