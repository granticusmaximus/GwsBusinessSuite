namespace GwsBusinessSuite.Application.Localization;

public interface IContentLocalizationService
{
    // Every non-trashed Article and CmsPage, for the admin picker.
    Task<IReadOnlyList<LocalizableContent>> ListLocalizableContentAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContentLocalizationSummary>> ListLocalizationsAsync(string contentType, Guid contentId, CancellationToken cancellationToken = default);

    Task<ContentLocalizationDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    // Upserts by (contentType, contentId, editor.LanguageCode) - a manual add/edit path.
    Task<ContentLocalizationDetail> SaveAsync(string contentType, Guid contentId, ContentLocalizationEditor editor, string performedBy, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, string performedBy, CancellationToken cancellationToken = default);

    Task<ContentLocalizationDetail> SetStatusAsync(Guid id, string status, string performedBy, CancellationToken cancellationToken = default);

    // AI-assisted first draft. Translates the title/body(/meta) of the source Article or
    // CmsPage and upserts the result as a Draft, ready for human review before publishing.
    Task<ContentLocalizationDetail> GenerateTranslationAsync(string contentType, Guid contentId, string languageCode, string model, string performedBy, CancellationToken cancellationToken = default);

    // Used by the public content routes: returns the Published localization for this
    // language, or null if none exists (falls back to the source content's own language).
    Task<PublishedLocalization?> GetPublishedAsync(string contentType, Guid contentId, string languageCode, CancellationToken cancellationToken = default);
}
