namespace GwsBusinessSuite.Application.Settings;

public sealed record SiteSettingsView(
    int PostsPerPage,
    Guid? DefaultArticleCategoryId,
    string? DefaultAuthorByline,
    string? OllamaModelOverride,
    int? OllamaTimeoutMinutesOverride,
    string? HeroImageModelOverride,
    int MaxMediaUploadSizeMb);

public interface ISiteSettingsService
{
    Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default);
}
