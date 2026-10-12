using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SentinelNoteCleanupService(
    IWikiService wikiService,
    IOllamaService ollama,
    ISiteSettingsService siteSettings,
    ISentinelAccessService? accessService = null) : ISentinelNoteCleanupService
{
    public async Task<string> PrepareAsync(Guid pageId, string username, CancellationToken cancellationToken = default)
    {
        await EnsureEditAsync(pageId, username, cancellationToken);
        var page = await wikiService.GetPageAsync(pageId, cancellationToken) ?? throw new KeyNotFoundException("The page no longer exists.");
        var markdown = SentinelNoteCleanup.ToMarkdown(WikiBlockJson.ParseBlocks(page.BlocksJson));
        return markdown.Length == 0 ? throw new InvalidOperationException("This page is empty - there's nothing to clean up.") : markdown;
    }

    public async Task<string> RunOnServerAsync(string pageMarkdown, CancellationToken cancellationToken = default)
    {
        var settings = await siteSettings.GetSettingsAsync(cancellationToken);
        var model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? SentinelGptDefaults.Model : settings.OllamaModelOverride;
        return await ollama.GenerateAsync(model, SentinelNoteCleanup.SystemPrompt, SentinelNoteCleanup.UserPrompt(pageMarkdown), cancellationToken);
    }

    public async Task ApplyAsync(Guid pageId, IReadOnlyList<WikiBlock> blocks, string username, CancellationToken cancellationToken = default)
    {
        await EnsureEditAsync(pageId, username, cancellationToken);
        var page = await wikiService.GetPageAsync(pageId, cancellationToken) ?? throw new KeyNotFoundException("The page no longer exists.");
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

    private async Task EnsureEditAsync(Guid pageId, string username, CancellationToken ct)
    {
        if (accessService is not null && !await accessService.CanAccessAsync(pageId, isDatabase: false, username, SentinelAccessLevels.Edit, ct))
            throw new UnauthorizedAccessException("You need edit access to clean up this page.");
    }
}
