using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelWorkspaceService(
    IAppDbContext dbContext,
    TimeProvider timeProvider,
    ISentinelAccessService? accessService = null) : ISentinelWorkspaceService
{
    // Every call here loads full row content (BlocksJson/PropertyValuesJson blobs, not just
    // a title) into memory, and search re-runs this on every keystroke of live search. These
    // caps are a circuit breaker against that growing unbounded as a workspace scales, not a
    // pagination UX - once a workspace has more matches/pages than the cap, results/backlinks
    // become best-effort rather than exhaustive, which is an acceptable tradeoff against the
    // alternative of every search loading the entire workspace's content into memory.
    private const int MaxSearchCandidatesPerType = 500;
    private const int MaxScanPages = 2000;

    public async Task<IReadOnlyList<SentinelSearchResult>> SearchAsync(
        string query,
        string username,
        int maxResults = 25,
        CancellationToken cancellationToken = default)
    {
        var normalized = query.Trim();
        var terms = Tokenize(normalized);
        if (terms.Count == 0 || maxResults <= 0)
        {
            return [];
        }

        // Apply the broad token filter in SQLite before parsing/ranking rich JSON in
        // process. This keeps search responsive as a workspace grows while the final
        // Score pass below remains the source of truth for exact all-token matching.
        var pageQuery = dbContext.WikiPages.AsNoTracking();
        var databaseQuery = dbContext.WikiDatabases.AsNoTracking();
        foreach (var term in terms)
        {
            var loweredTerm = term.ToLower();
            pageQuery = pageQuery.Where(page =>
                page.Title.ToLower().Contains(loweredTerm)
                || page.BlocksJson.ToLower().Contains(loweredTerm));
            databaseQuery = databaseQuery.Where(database =>
                database.Title.ToLower().Contains(loweredTerm)
                || database.Properties.Any(property => property.Name.ToLower().Contains(loweredTerm))
                || database.Rows.Any(row =>
                    row.PropertyValuesJson.ToLower().Contains(loweredTerm)
                    || row.BlocksJson.ToLower().Contains(loweredTerm)));
        }

        var pages = await pageQuery.Take(MaxSearchCandidatesPerType).ToListAsync(cancellationToken);
        var databases = await databaseQuery
            .Include(database => database.Properties)
            .Include(database => database.Rows)
            .Take(MaxSearchCandidatesPerType)
            .ToListAsync(cancellationToken);
        var accessibleTargets = await GetAccessibleTargetsAsync(
            pages.Select(page => new SentinelAccessTarget(page.Id, IsDatabase: false))
                .Concat(databases.Select(database => new SentinelAccessTarget(database.Id, IsDatabase: true))),
            username,
            SentinelAccessLevels.View,
            cancellationToken);
        pages = pages
            .Where(page => accessibleTargets.Contains(new SentinelAccessTarget(page.Id, IsDatabase: false)))
            .ToList();
        databases = databases
            .Where(database => accessibleTargets.Contains(new SentinelAccessTarget(database.Id, IsDatabase: true)))
            .ToList();

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
                    terms,
                    page.CreatedBy,
                    page.CreatedAt));
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
                    terms,
                    database.CreatedBy,
                    database.CreatedAt));
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
        string username,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessTargetAsync(targetPageId, false, username, cancellationToken))
        {
            return [];
        }
        var target = await dbContext.WikiPages.AsNoTracking()
            .FirstOrDefaultAsync(page => page.Id == targetPageId, cancellationToken);
        if (target is null)
        {
            return [];
        }

        var expectedLink = $"wikilink:{targetPageId}";
        var legacyLink = $"[[{target.Title}]]";
        var backlinks = new List<SentinelBacklink>();
        var pages = await dbContext.WikiPages.AsNoTracking().Take(MaxScanPages).ToListAsync(cancellationToken);
        var accessibleTargets = await GetAccessibleTargetsAsync(
            pages.Select(page => new SentinelAccessTarget(page.Id, IsDatabase: false)),
            username,
            SentinelAccessLevels.View,
            cancellationToken);
        pages = pages.Where(page => accessibleTargets.Contains(
            new SentinelAccessTarget(page.Id, IsDatabase: false))).ToList();

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

    public async Task<IReadOnlyList<SentinelBacklink>> GetRowMentionsAsync(
        Guid wikiDatabaseId,
        Guid rowId,
        string username,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessTargetAsync(wikiDatabaseId, true, username, cancellationToken))
        {
            return [];
        }
        // Same scan scope as GetBacklinksAsync (WikiPages only, not other rows' bodies) -
        // a page-mentions-page or page-mentions-row link is found; a row-mentions-row link
        // written inside another row's page body is not, matching that existing limitation
        // rather than quietly having two different backlink scan depths in the same app.
        var expectedLink = $"rowmention:{wikiDatabaseId}:{rowId}";
        var pages = await dbContext.WikiPages.AsNoTracking().Take(MaxScanPages).ToListAsync(cancellationToken);
        var accessibleTargets = await GetAccessibleTargetsAsync(
            pages.Select(page => new SentinelAccessTarget(page.Id, IsDatabase: false)),
            username,
            SentinelAccessLevels.View,
            cancellationToken);
        pages = pages.Where(page => accessibleTargets.Contains(
            new SentinelAccessTarget(page.Id, IsDatabase: false))).ToList();
        var mentions = new List<SentinelBacklink>();

        foreach (var page in pages)
        {
            var matchingBlock = WikiBlockJson.ParseBlocks(page.BlocksJson).FirstOrDefault(block =>
                block.RichText.Any(span => string.Equals(span.Link, expectedLink, StringComparison.OrdinalIgnoreCase)));
            if (matchingBlock is null) continue;
            mentions.Add(new SentinelBacklink(page.Id, page.Title, WikiBlockHtmlRenderer.PlainTextPreview(matchingBlock, 140)));
        }

        return mentions.OrderBy(mention => mention.SourcePageTitle, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<SentinelSavedSearchView>> ListSavedSearchesAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var entries = await dbContext.SentinelSavedSearches.AsNoTracking()
            .Where(item => item.Username == normalizedUser)
            .ToListAsync(cancellationToken);
        return entries
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new SentinelSavedSearchView(item.Id, item.Query, item.CreatedAt))
            .ToList();
    }

    public async Task<SentinelSavedSearchView> SaveSearchAsync(
        string username,
        string query,
        CancellationToken cancellationToken = default)
    {
        var trimmedQuery = query?.Trim() ?? string.Empty;
        if (trimmedQuery.Length == 0)
        {
            throw new ArgumentException("A saved search needs a non-empty query.", nameof(query));
        }

        var normalizedUser = NormalizeUsername(username);
        var existing = await dbContext.SentinelSavedSearches.FirstOrDefaultAsync(item =>
            item.Username == normalizedUser && item.Query == trimmedQuery, cancellationToken);
        if (existing is not null)
        {
            return new SentinelSavedSearchView(existing.Id, existing.Query, existing.CreatedAt);
        }

        var now = timeProvider.GetUtcNow();
        var saved = new SentinelSavedSearch
        {
            Username = normalizedUser,
            Query = trimmedQuery,
            CreatedAt = now,
            CreatedBy = normalizedUser
        };
        await dbContext.SentinelSavedSearches.AddAsync(saved, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SentinelSavedSearchView(saved.Id, saved.Query, saved.CreatedAt);
    }

    public async Task DeleteSavedSearchAsync(
        string username,
        Guid savedSearchId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = NormalizeUsername(username);
        var saved = await dbContext.SentinelSavedSearches.FirstOrDefaultAsync(item =>
            item.Id == savedSearchId && item.Username == normalizedUser, cancellationToken);
        if (saved is null) return;
        dbContext.SentinelSavedSearches.Remove(saved);
        await dbContext.SaveChangesAsync(cancellationToken);
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
        var accessibleTargets = await GetAccessibleTargetsAsync(
            entries.Select(entry => new SentinelAccessTarget(entry.TargetId, entry.IsDatabase)),
            username,
            SentinelAccessLevels.View,
            cancellationToken);
        entries = entries.Where(entry => accessibleTargets.Contains(
            new SentinelAccessTarget(entry.TargetId, entry.IsDatabase))).ToList();
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
        if (!await CanAccessTargetAsync(targetId, isDatabase, username, cancellationToken))
        {
            throw new UnauthorizedAccessException("You don't have access to this Sentinel item.");
        }
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
        if (!await CanAccessTargetAsync(targetId, isDatabase, username, cancellationToken))
        {
            throw new UnauthorizedAccessException("You don't have access to this Sentinel item.");
        }
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
        string username,
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

        if (normalized.Length > 0)
        {
            var databases = await dbContext.WikiDatabases.AsNoTracking()
                .Include(database => database.Properties)
                .Include(database => database.Rows)
                .ToListAsync(cancellationToken);
            var accessibleTargets = await GetAccessibleTargetsAsync(
                databases.Select(database => new SentinelAccessTarget(database.Id, IsDatabase: true)),
                username,
                SentinelAccessLevels.View,
                cancellationToken);
            databases = databases.Where(database => accessibleTargets.Contains(
                new SentinelAccessTarget(database.Id, IsDatabase: true))).ToList();
            foreach (var database in databases)
            {
                var titleProperty = database.Properties.FirstOrDefault(property => property.Type == WikiDatabasePropertyTypes.Title);
                if (titleProperty is null) continue;

                foreach (var row in database.Rows)
                {
                    var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                    var title = WikiPropertyValues.GetText(values, titleProperty.Id);
                    if (string.IsNullOrWhiteSpace(title) || !title.Contains(normalized, StringComparison.OrdinalIgnoreCase)) continue;
                    // insertMention (wiki-block-editor.js) builds the anchor href as
                    // `${kind}mention:${value}`, so "row" + ":" + this Value literally
                    // produces "rowmention:{databaseId}:{rowId}" with no JS changes needed.
                    suggestions.Add(new SentinelMentionSuggestion("row", $"{database.Id}:{row.Id}", title, $"Row in {database.Title}"));
                }
            }
        }

        return suggestions.Take(maxResults).ToList();
    }

    public async Task<IReadOnlyList<SentinelMention>> GetMentionsAsync(
        string username,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var expectedLink = $"usermention:{NormalizeUsername(username)}";
        var pages = await dbContext.WikiPages.AsNoTracking()
            .Take(MaxScanPages)
            .ToListAsync(cancellationToken);
        var accessibleTargets = await GetAccessibleTargetsAsync(
            pages.Select(page => new SentinelAccessTarget(page.Id, IsDatabase: false)),
            username,
            SentinelAccessLevels.View,
            cancellationToken);
        pages = pages.Where(page => accessibleTargets.Contains(
            new SentinelAccessTarget(page.Id, IsDatabase: false))).ToList();
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
        // Regression guard for a real bug: FirstOrDefault() on an empty sequence of
        // (string Term, int Index) value tuples returns (null, 0) - not a negative sentinel -
        // so the old "if (index < 0)" guard below never caught a title-only match (content has
        // no term at all) and fell through to firstMatch.Term.Length, a NullReferenceException.
        // Passing an explicit default with Index -1 makes the "no match" case detectable.
        var firstMatch = terms
            .Select(term => (Term: term, Index: singleLine.IndexOf(term, StringComparison.OrdinalIgnoreCase)))
            .Where(match => match.Index >= 0)
            .OrderBy(match => match.Index)
            .FirstOrDefault((Term: string.Empty, Index: -1));
        var index = firstMatch.Index;
        if (index < 0) return titleFallback;

        const int context = 55;
        var start = Math.Max(0, index - context);
        var length = Math.Min(singleLine.Length - start, firstMatch.Term.Length + context * 2);
        var preview = singleLine.Substring(start, length);
        return $"{(start > 0 ? "…" : string.Empty)}{preview}{(start + length < singleLine.Length ? "…" : string.Empty)}";
    }

    private async Task<IReadOnlySet<SentinelAccessTarget>> GetAccessibleTargetsAsync(
        IEnumerable<SentinelAccessTarget> targets,
        string username,
        string requiredAccessLevel,
        CancellationToken cancellationToken)
    {
        var distinctTargets = targets.Distinct().ToList();
        return accessService is null
            ? distinctTargets.ToHashSet()
            : await accessService.GetAccessibleTargetsAsync(
                distinctTargets, username, requiredAccessLevel, cancellationToken);
    }

    private async Task<bool> CanAccessTargetAsync(
        Guid targetId,
        bool isDatabase,
        string username,
        CancellationToken cancellationToken) => accessService is null
            || await accessService.CanAccessAsync(
                targetId, isDatabase, username, SentinelAccessLevels.View, cancellationToken);

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
