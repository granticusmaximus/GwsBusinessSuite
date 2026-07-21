using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelWorkspaceService(IAppDbContext dbContext) : ISentinelWorkspaceService
{
    public async Task<IReadOnlyList<SentinelSearchResult>> SearchAsync(
        string query,
        int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Trim();
        if (normalized.Length == 0 || maxResults <= 0)
        {
            return [];
        }

        var pages = await dbContext.WikiPages.AsNoTracking().ToListAsync(cancellationToken);
        var databases = await dbContext.WikiDatabases.AsNoTracking()
            .Include(database => database.Properties)
            .Include(database => database.Rows)
            .ToListAsync(cancellationToken);

        var results = new List<SentinelSearchResult>();
        foreach (var page in pages)
        {
            var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
            var content = string.Join('\n', blocks.Select(SearchableBlockText));
            var score = Score(page.Title, content, normalized);
            if (score > 0)
            {
                results.Add(new SentinelSearchResult(
                    page.Id,
                    false,
                    page.Title,
                    BuildPreview(content, normalized, "Page title match"),
                    page.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ? "Page" : "Page content",
                    score));
            }
        }

        foreach (var database in databases)
        {
            var contentParts = new List<string>();
            contentParts.AddRange(database.Properties.OrderBy(property => property.SortOrder).Select(property => property.Name));
            foreach (var row in database.Rows.OrderBy(row => row.SortOrder))
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                contentParts.Add(string.Join(" · ", database.Properties
                    .OrderBy(property => property.SortOrder)
                    .Select(property => WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt))
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
                contentParts.AddRange(WikiBlockJson.ParseBlocks(row.BlocksJson).Select(SearchableBlockText));
            }

            var content = string.Join('\n', contentParts);
            var score = Score(database.Title, content, normalized);
            if (score > 0)
            {
                results.Add(new SentinelSearchResult(
                    database.Id,
                    true,
                    database.Title,
                    BuildPreview(content, normalized, "Database title match"),
                    database.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase) ? "Database" : "Database content",
                    score));
            }
        }

        return results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    public async Task<IReadOnlyList<SentinelBacklink>> GetBacklinksAsync(
        Guid targetPageId,
        CancellationToken cancellationToken = default)
    {
        var pages = await dbContext.WikiPages.AsNoTracking().ToListAsync(cancellationToken);
        var target = pages.FirstOrDefault(page => page.Id == targetPageId);
        if (target is null)
        {
            return [];
        }

        var expectedLink = $"wikilink:{targetPageId}";
        var legacyLink = $"[[{target.Title}]]";
        var backlinks = new List<SentinelBacklink>();

        foreach (var source in pages.Where(page => page.Id != targetPageId))
        {
            foreach (var block in WikiBlockJson.ParseBlocks(source.BlocksJson))
            {
                var hasStructuredLink = block.RichText.Any(span =>
                    string.Equals(span.Link, expectedLink, StringComparison.OrdinalIgnoreCase));
                var hasLegacyLink = block.Type == WikiBlockTypes.Markdown
                    && block.Props.GetValueOrDefault("content", string.Empty)
                        .Contains(legacyLink, StringComparison.OrdinalIgnoreCase);

                if (!hasStructuredLink && !hasLegacyLink)
                {
                    continue;
                }

                backlinks.Add(new SentinelBacklink(
                    source.Id,
                    source.Title,
                    WikiBlockHtmlRenderer.PlainTextPreview(block, 140)));
                break;
            }
        }

        return backlinks.OrderBy(link => link.SourcePageTitle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string SearchableBlockText(WikiBlock block)
    {
        var propsText = string.Join(' ', block.Props.Values);
        return string.IsNullOrWhiteSpace(propsText) ? block.PlainText : $"{block.PlainText} {propsText}";
    }

    private static int Score(string title, string content, string query)
    {
        if (string.Equals(title, query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 60;
        return content.Contains(query, StringComparison.OrdinalIgnoreCase) ? 30 : 0;
    }

    private static string BuildPreview(string content, string query, string titleFallback)
    {
        if (string.IsNullOrWhiteSpace(content)) return titleFallback;

        var singleLine = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var index = singleLine.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return titleFallback;

        const int context = 55;
        var start = Math.Max(0, index - context);
        var length = Math.Min(singleLine.Length - start, query.Length + context * 2);
        var preview = singleLine.Substring(start, length);
        return $"{(start > 0 ? "…" : string.Empty)}{preview}{(start + length < singleLine.Length ? "…" : string.Empty)}";
    }
}
