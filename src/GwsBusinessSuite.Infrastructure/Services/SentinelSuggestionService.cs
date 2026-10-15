using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// Suggested edits (SentinelSuggestions holds the diff/apply rules). Accepted changes are saved
// through WikiService.SavePageAsync, so they get a revision and every normal save rule.
public sealed class SentinelSuggestionService(
    IAppDbContext dbContext,
    IWikiService wikiService,
    ISentinelAccessService accessService,
    TimeProvider timeProvider) : ISentinelSuggestionService
{
    public async Task<SentinelSuggestionView?> SubmitAsync(Guid wikiPageId, string baselineBlocksJson, string editedBlocksJson, string? note, string username, CancellationToken cancellationToken = default)
    {
        var page = await RequirePageAsync(wikiPageId, username, SentinelAccessLevels.Comment, cancellationToken);
        var changes = SentinelSuggestions.Diff(
            WikiBlockJson.ParseBlocks(baselineBlocksJson), WikiBlockJson.ParseBlocks(editedBlocksJson), WikiBlockJson.ParseBlocks(page.BlocksJson));
        if (changes.Count == 0) return null;
        if (changes.Count > SentinelSuggestions.MaxChanges)
            throw new InvalidOperationException($"A suggestion can hold up to {SentinelSuggestions.MaxChanges} changes. Split it into smaller suggestions.");
        var now = timeProvider.GetUtcNow();
        var suggestion = new SentinelPageSuggestion
        {
            WikiPageId = wikiPageId,
            Note = (note ?? string.Empty).Trim() is { Length: > SentinelSuggestions.MaxNoteLength } longNote
                ? longNote[..SentinelSuggestions.MaxNoteLength] : (note ?? string.Empty).Trim(),
            ChangesJson = JsonSerializer.Serialize(changes, SentinelSuggestions.JsonOptions),
            BaseContentVersion = page.ContentVersion,
            CreatedAt = now,
            CreatedAtUnix = now.ToUnixTimeSeconds(),
            CreatedBy = username
        };
        await dbContext.PageSuggestions.AddAsync(suggestion, cancellationToken);
        var reviewer = page.OwnerUsername ?? page.CreatedBy;
        if (!string.IsNullOrWhiteSpace(reviewer) && !string.Equals(reviewer, username, StringComparison.OrdinalIgnoreCase))
        {
            await dbContext.SentinelNotifications.AddAsync(new SentinelNotification
            {
                Username = reviewer,
                Kind = SentinelSuggestions.NotificationKind,
                WikiPageId = wikiPageId,
                Message = $"{username} suggested {(changes.Count == 1 ? "a change" : $"{changes.Count} changes")} to \"{page.Title}\".",
                CreatedAt = now,
                CreatedBy = username
            }, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(suggestion);
    }

    public async Task<IReadOnlyList<SentinelSuggestionView>> ListOpenAsync(Guid wikiPageId, string username, CancellationToken cancellationToken = default)
    {
        if (!await accessService.CanAccessAsync(wikiPageId, false, username, SentinelAccessLevels.View, cancellationToken)) return [];
        return (await dbContext.PageSuggestions.AsNoTracking()
                .Where(item => item.WikiPageId == wikiPageId && item.Status == SentinelSuggestionStatuses.Open)
                .OrderBy(item => item.CreatedAtUnix)
                .ToListAsync(cancellationToken))
            .Select(ToView)
            .ToList();
    }

    public async Task<SentinelSuggestionView> ReviewAsync(Guid suggestionId, IReadOnlyCollection<Guid> changeIds, bool accept, string username, CancellationToken cancellationToken = default)
    {
        var suggestion = await dbContext.PageSuggestions.FirstOrDefaultAsync(item => item.Id == suggestionId, cancellationToken)
            ?? throw new KeyNotFoundException("That suggestion no longer exists.");
        var page = await RequirePageAsync(suggestion.WikiPageId, username, SentinelAccessLevels.Edit, cancellationToken);
        if (suggestion.Status != SentinelSuggestionStatuses.Open) throw new InvalidOperationException("That suggestion has already been dealt with.");

        var changes = Changes(suggestion);
        var chosen = changes
            .Where(change => change.Status == SentinelSuggestionStatuses.Pending && changeIds.Contains(change.Id))
            .ToList();
        if (chosen.Count == 0) return ToView(suggestion);

        if (accept)
        {
            var blocks = SentinelSuggestions.Apply(WikiBlockJson.ParseBlocks(page.BlocksJson), chosen);
            await wikiService.SavePageAsync(new WikiPageEditorModel
            {
                WikiPageId = page.Id,
                ExpectedContentVersion = page.ContentVersion,
                Title = page.Title,
                Slug = page.Slug,
                BlocksJson = WikiBlockJson.Serialize(blocks),
                BaseBlocksJson = page.BlocksJson,
                Icon = page.Icon,
                CoverImageUrl = page.CoverImageUrl,
                ParentWikiPageId = page.ParentWikiPageId,
                IsFullWidth = page.IsFullWidth,
                FontStyle = page.FontStyle
            }, username, createRevisionCheckpoint: true, cancellationToken);
        }

        var decided = chosen.Select(change => change.Id).ToHashSet();
        var status = accept ? SentinelSuggestionStatuses.Accepted : SentinelSuggestionStatuses.Rejected;
        changes = changes.Select(change => decided.Contains(change.Id) ? change with { Status = status } : change).ToList();
        suggestion.ChangesJson = JsonSerializer.Serialize(changes, SentinelSuggestions.JsonOptions);
        if (changes.All(change => change.Status != SentinelSuggestionStatuses.Pending)) suggestion.Status = SentinelSuggestionStatuses.Closed;
        suggestion.UpdatedAt = timeProvider.GetUtcNow();
        suggestion.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToView(suggestion);
    }

    public async Task WithdrawAsync(Guid suggestionId, string username, CancellationToken cancellationToken = default)
    {
        var suggestion = await dbContext.PageSuggestions.FirstOrDefaultAsync(item => item.Id == suggestionId, cancellationToken)
            ?? throw new KeyNotFoundException("That suggestion no longer exists.");
        if (!string.Equals(suggestion.CreatedBy, username, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only the person who made a suggestion can withdraw it.");
        if (suggestion.Status != SentinelSuggestionStatuses.Open) return;
        suggestion.Status = SentinelSuggestionStatuses.Withdrawn;
        suggestion.UpdatedAt = timeProvider.GetUtcNow();
        suggestion.UpdatedBy = username;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<WikiPage> RequirePageAsync(Guid wikiPageId, string username, string level, CancellationToken cancellationToken)
    {
        var page = await dbContext.WikiPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == wikiPageId && item.TrashedAt == null, cancellationToken)
            ?? throw new KeyNotFoundException("That page no longer exists.");
        if (!await accessService.CanAccessAsync(wikiPageId, false, username, level, cancellationToken))
            throw new UnauthorizedAccessException(level == SentinelAccessLevels.Edit
                ? "You need edit access to accept or reject suggestions."
                : "You need comment access to suggest edits to this page.");
        return page;
    }

    private static List<SentinelSuggestionChange> Changes(SentinelPageSuggestion suggestion) =>
        JsonSerializer.Deserialize<List<SentinelSuggestionChange>>(suggestion.ChangesJson, SentinelSuggestions.JsonOptions) ?? [];

    private static SentinelSuggestionView ToView(SentinelPageSuggestion suggestion) =>
        new(suggestion.Id, suggestion.WikiPageId, suggestion.CreatedBy, suggestion.Note, suggestion.CreatedAt, suggestion.Status, Changes(suggestion));
}
