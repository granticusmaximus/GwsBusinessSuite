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
    string? HiddenNavKeys = null,
    // Added as a new trailing optional parameter (not inserted next to HeroImageModelOverride
    // above) deliberately - this record is constructed positionally at every real call site
    // (production and tests alike), and a mid-record insert would have broken all of them for
    // no benefit. No safe default exists (the main chat model is text-only), same shape as
    // HeroImageModelOverride.
    string? VisionModelOverride = null);

public interface ISiteSettingsService
{
    Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default);
}
