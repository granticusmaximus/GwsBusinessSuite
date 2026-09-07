namespace GwsBusinessSuite.Application.Settings;

public sealed record SiteSettingsView(
    int PostsPerPage,
    Guid? DefaultArticleCategoryId,
    string? DefaultAuthorByline,
    string? OllamaModelOverride,
    int? OllamaTimeoutMinutesOverride,
    string? HeroImageModelOverride,
    int MaxMediaUploadSizeMb,
    // Null means "never configured" and takes AdminFeatures.DefaultHiddenKeys; an empty string
    // is a deliberate "show everything" and is preserved as such.
    string? HiddenNavKeys = null);

public interface ISiteSettingsService
{
    Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default);
}
