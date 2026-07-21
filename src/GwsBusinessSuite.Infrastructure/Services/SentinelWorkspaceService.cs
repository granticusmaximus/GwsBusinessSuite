using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelWorkspaceService(IAppDbContext dbContext, TimeProvider timeProvider) : ISentinelWorkspaceService
{
    public async Task<IReadOnlyList<SentinelSearchResult>> SearchAsync(
        string query,
        int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Trim();
        var terms = Tokenize(normalized);
        if (terms.Count == 0 || maxResults <= 0)
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
            var score = Score(page.Title, content, normalized, terms);
            if (score > 0)
            {
                results.Add(new SentinelSearchResult(
                    page.Id,
                    false,
                    page.Title,
                    BuildPreview(content, terms, "Page title match"),
                    terms.All(term => page.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) ? "Page" : "Page content",
                    score,
                    terms));
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
            var score = Score(database.Title, content, normalized, terms);
            if (score > 0)
            {
                results.Add(new SentinelSearchResult(
                    database.Id,
                    true,
                    database.Title,
                    BuildPreview(content, terms, "Database title match"),
                    terms.All(term => database.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) ? "Database" : "Database content",
                    score,
                    terms));
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

    public async Task<SentinelNavigationState> GetNavigationAsync(
        string username,
        int maxRecents = 8,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var entries = await dbContext.SentinelNavigationEntries
            .Where(entry => entry.Username == normalizedUser)
            .ToListAsync(cancellationToken);
        entries = entries.OrderByDescending(entry => entry.LastOpenedAt).ToList();
        var pages = await dbContext.WikiPages.AsNoTracking()
            .Where(page => entries.Select(entry => entry.TargetId).Contains(page.Id))
            .ToDictionaryAsync(page => page.Id, cancellationToken);
        var databases = await dbContext.WikiDatabases.AsNoTracking()
            .Where(database => entries.Select(entry => entry.TargetId).Contains(database.Id))
            .ToDictionaryAsync(database => database.Id, cancellationToken);

        var items = new List<SentinelNavigationItem>();
        var staleEntries = new List<SentinelNavigationEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsDatabase && databases.TryGetValue(entry.TargetId, out var database))
            {
                items.Add(new SentinelNavigationItem(entry.TargetId, true, database.Title, database.Icon,
                    entry.IsFavorite, entry.LastOpenedAt));
            }
            else if (!entry.IsDatabase && pages.TryGetValue(entry.TargetId, out var page))
            {
                items.Add(new SentinelNavigationItem(entry.TargetId, false, page.Title, page.Icon,
                    entry.IsFavorite, entry.LastOpenedAt));
            }
            else
            {
                staleEntries.Add(entry);
            }
        }

        if (staleEntries.Count > 0)
        {
            dbContext.SentinelNavigationEntries.RemoveRange(staleEntries);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new SentinelNavigationState(
            items.Where(item => item.IsFavorite).OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            items.OrderByDescending(item => item.LastOpenedAt).Take(Math.Max(0, maxRecents)).ToList());
    }

    public async Task RecordOpenedAsync(
        string username,
        Guid targetId,
        bool isDatabase,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var entry = await FindNavigationEntryAsync(normalizedUser, targetId, isDatabase, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (entry is null)
        {
            await dbContext.SentinelNavigationEntries.AddAsync(new SentinelNavigationEntry
            {
                Username = normalizedUser,
                TargetId = targetId,
                IsDatabase = isDatabase,
                LastOpenedAt = now,
                CreatedAt = now,
                CreatedBy = normalizedUser
            }, cancellationToken);
        }
        else
        {
            entry.LastOpenedAt = now;
            entry.UpdatedAt = now;
            entry.UpdatedBy = normalizedUser;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ToggleFavoriteAsync(
        string username,
        Guid targetId,
        bool isDatabase,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var entry = await FindNavigationEntryAsync(normalizedUser, targetId, isDatabase, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (entry is null)
        {
            entry = new SentinelNavigationEntry
            {
                Username = normalizedUser,
                TargetId = targetId,
                IsDatabase = isDatabase,
                IsFavorite = true,
                LastOpenedAt = now,
                CreatedAt = now,
                CreatedBy = normalizedUser
            };
            await dbContext.SentinelNavigationEntries.AddAsync(entry, cancellationToken);
        }
        else
        {
            entry.IsFavorite = !entry.IsFavorite;
            entry.UpdatedAt = now;
            entry.UpdatedBy = normalizedUser;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return entry.IsFavorite;
    }

    public async Task<IReadOnlyList<SentinelMentionSuggestion>> SearchMentionSuggestionsAsync(
        string query,
        int maxResults = 8,
        CancellationToken cancellationToken = default)
    {
        if (maxResults <= 0) return [];
        var normalized = query.Trim().TrimStart('@');
        var users = await dbContext.AppUsers.AsNoTracking()
            .Where(user => user.IsActive)
            .Select(user => user.Username)
            .ToListAsync(cancellationToken);
        var suggestions = users
            .Where(username => username.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(username => username.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .ThenBy(username => username, StringComparer.OrdinalIgnoreCase)
            .Select(username => new SentinelMentionSuggestion("user", username, $"@{username}", "Person"))
            .ToList();

        var today = timeProvider.GetLocalNow().Date;
        var dates = new[]
        {
            (Token: "today", Date: today),
            (Token: "tomorrow", Date: today.AddDays(1)),
            (Token: "yesterday", Date: today.AddDays(-1))
        };
        suggestions.AddRange(dates
            .Where(item => item.Token.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(item => new SentinelMentionSuggestion(
                "date", item.Date.ToString("yyyy-MM-dd"), $"@{item.Token}", item.Date.ToString("D"))));

        return suggestions.Take(maxResults).ToList();
    }

    public async Task<IReadOnlyList<SentinelMention>> GetMentionsAsync(
        string username,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var expectedLink = $"usermention:{NormalizeUsername(username)}";
        var pages = await dbContext.WikiPages.AsNoTracking()
            .ToListAsync(cancellationToken);
        pages = pages.OrderByDescending(page => page.UpdatedAt ?? page.CreatedAt).ToList();
        var mentions = new List<SentinelMention>();
        foreach (var page in pages)
        {
            var matchingBlock = WikiBlockJson.ParseBlocks(page.BlocksJson).FirstOrDefault(block =>
                block.RichText.Any(span => string.Equals(span.Link, expectedLink, StringComparison.OrdinalIgnoreCase)));
            if (matchingBlock is null) continue;
            mentions.Add(new SentinelMention(page.Id, page.Title,
                WikiBlockHtmlRenderer.PlainTextPreview(matchingBlock, 140), page.UpdatedAt ?? page.CreatedAt));
            if (mentions.Count >= maxResults) break;
        }

        return mentions;
    }

    private static string SearchableBlockText(WikiBlock block)
    {
        var propsText = string.Join(' ', block.Props.Values);
        return string.IsNullOrWhiteSpace(propsText) ? block.PlainText : $"{block.PlainText} {propsText}";
    }

    private static int Score(string title, string content, string query, IReadOnlyList<string> terms)
    {
        var searchable = $"{title}\n{content}";
        if (!terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase))) return 0;

        var score = 0;
        if (string.Equals(title, query, StringComparison.OrdinalIgnoreCase)) score += 240;
        else if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 120;
        if (content.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 60;

        foreach (var term in terms)
        {
            if (string.Equals(title, term, StringComparison.OrdinalIgnoreCase)) score += 60;
            else if (title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 25;
            score += Math.Min(Occurrences(content, term), 5) * 8;
        }
        return score;
    }

    private static string BuildPreview(string content, IReadOnlyList<string> terms, string titleFallback)
    {
        if (string.IsNullOrWhiteSpace(content)) return titleFallback;

        var singleLine = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var firstMatch = terms
            .Select(term => (Term: term, Index: singleLine.IndexOf(term, StringComparison.OrdinalIgnoreCase)))
            .Where(match => match.Index >= 0)
            .OrderBy(match => match.Index)
            .FirstOrDefault();
        var index = firstMatch.Index;
        if (index < 0) return titleFallback;

        const int context = 55;
        var start = Math.Max(0, index - context);
        var length = Math.Min(singleLine.Length - start, firstMatch.Term.Length + context * 2);
        var preview = singleLine.Substring(start, length);
        return $"{(start > 0 ? "…" : string.Empty)}{preview}{(start + length < singleLine.Length ? "…" : string.Empty)}";
    }

    private Task<SentinelNavigationEntry?> FindNavigationEntryAsync(
        string username, Guid targetId, bool isDatabase, CancellationToken cancellationToken) =>
        dbContext.SentinelNavigationEntries.FirstOrDefaultAsync(entry =>
            entry.Username == username && entry.TargetId == targetId && entry.IsDatabase == isDatabase,
            cancellationToken);

    private static string NormalizeUsername(string username) =>
        string.IsNullOrWhiteSpace(username) ? "unknown" : username.Trim().ToLowerInvariant();

    private static List<string> Tokenize(string value) => Regex.Matches(value, @"[\p{L}\p{N}_-]+")
        .Select(match => match.Value.ToLowerInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static int Occurrences(string value, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += term.Length;
        }
        return count;
    }
}
