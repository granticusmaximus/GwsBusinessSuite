using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Localization;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class ContentLocalizationService(
    IAppDbContext db,
    IOllamaService? ollama,
    TimeProvider timeProvider,
    ILogger<ContentLocalizationService> logger) : IContentLocalizationService
{
    private const string TranslationSystemPromptTemplate =
        "You are a professional translator. Translate each string in the JSON array into {0}. " +
        "Preserve Markdown and HTML syntax and translate only human-readable text. Respond with " +
        "ONLY a JSON array of strings in the same length and order, with no commentary.";

    private static readonly Dictionary<string, string> LanguageDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German", ["it"] = "Italian",
        ["pt"] = "Portuguese", ["ja"] = "Japanese", ["ko"] = "Korean",
        ["zh"] = "Chinese (Simplified)", ["ar"] = "Arabic", ["hi"] = "Hindi",
        ["nl"] = "Dutch", ["ru"] = "Russian", ["pl"] = "Polish", ["tr"] = "Turkish",
        ["sv"] = "Swedish"
    };

    private static readonly Dictionary<string, string[]> TranslatableWidgetFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hero"] = ["headline", "subline", "cta1Label", "cta2Label"],
        ["heading"] = ["text"],
        ["paragraph"] = ["text"],
        ["richtext"] = ["content"],
        ["button"] = ["label"],
        ["card"] = ["title", "body"],
        ["testimonial"] = ["quote", "authorName", "authorRole"],
        ["image"] = ["alt", "caption"]
    };

    public async Task<IReadOnlyList<LocalizableContent>> ListLocalizableContentAsync(CancellationToken cancellationToken = default)
    {
        var articles = await db.Articles.AsNoTracking()
            .Where(article => article.TrashedAt == null)
            .Select(article => new LocalizableContent(article.Id, article.Title, ContentLocalizationContentTypes.Article))
            .ToListAsync(cancellationToken);
        var pages = await db.CmsPages.AsNoTracking()
            .Where(page => page.TrashedAt == null)
            .Select(page => new LocalizableContent(page.Id, page.Title, ContentLocalizationContentTypes.CmsPage))
            .ToListAsync(cancellationToken);

        return articles.Concat(pages).OrderBy(item => item.Title).ToList();
    }

    public async Task<IReadOnlyList<ContentLocalizationSummary>> ListLocalizationsAsync(
        string contentType,
        Guid contentId,
        CancellationToken cancellationToken = default)
    {
        ValidateContentType(contentType);
        var localizations = await db.ContentLocalizations.AsNoTracking()
            .Where(item => item.ContentType == contentType && item.ContentId == contentId)
            .ToListAsync(cancellationToken);

        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column.
        return localizations
            .OrderByDescending(item => item.UpdatedAt ?? item.CreatedAt)
            .ThenBy(item => item.LanguageCode, StringComparer.OrdinalIgnoreCase)
            .Select(ToSummary)
            .ToList();
    }

    public async Task<ContentLocalizationDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var localization = await db.ContentLocalizations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return localization is null ? null : ToDetail(localization);
    }

    public async Task<ContentLocalizationDetail> SaveAsync(
        string contentType,
        Guid contentId,
        ContentLocalizationEditor editor,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editor);
        await EnsureSourceExistsAsync(contentType, contentId, cancellationToken);
        ValidateStatus(editor.Status);

        var languageCode = NormalizeLanguageCode(editor.LanguageCode);
        var localization = await FindTrackedAsync(contentType, contentId, languageCode, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (localization is null)
        {
            localization = new ContentLocalization
            {
                ContentType = contentType,
                ContentId = contentId,
                LanguageCode = languageCode,
                CreatedAt = now,
                CreatedBy = performedBy
            };
            db.ContentLocalizations.Add(localization);
        }

        localization.Title = editor.Title.Trim();
        localization.Body = editor.Body;
        localization.MetaDescription = NormalizeOptional(editor.MetaDescription);
        localization.Status = editor.Status;
        // A human save supersedes the AI provenance even when it began as a generated draft.
        localization.IsAiGenerated = false;
        localization.AiModel = null;
        localization.UpdatedAt = now;
        localization.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToDetail(localization);
    }

    public async Task DeleteAsync(Guid id, string performedBy, CancellationToken cancellationToken = default)
    {
        _ = performedBy;
        var localization = await db.ContentLocalizations.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (localization is null) return;

        db.ContentLocalizations.Remove(localization);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ContentLocalizationDetail> SetStatusAsync(
        Guid id,
        string status,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        ValidateStatus(status);
        var localization = await db.ContentLocalizations.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Localization {id} was not found.");
        localization.Status = status;
        localization.UpdatedAt = timeProvider.GetUtcNow();
        localization.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToDetail(localization);
    }

    public async Task<ContentLocalizationDetail> GenerateTranslationAsync(
        string contentType,
        Guid contentId,
        string languageCode,
        string model,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        var ai = ollama ?? throw new InvalidOperationException("Ollama is not available to generate a translation.");
        var normalizedLanguageCode = NormalizeLanguageCode(languageCode);
        var normalizedModel = model.Trim();
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            throw new InvalidOperationException("Select an Ollama model before generating a translation.");
        }

        var inputStrings = new List<string>();
        string translatedBody;
        string? translatedMetaDescription;
        if (contentType == ContentLocalizationContentTypes.Article)
        {
            var article = await db.Articles.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == contentId && item.TrashedAt == null, cancellationToken)
                ?? throw new InvalidOperationException($"Article {contentId} was not found.");
            inputStrings.AddRange([article.Title, article.BodyMarkdown, article.MetaDescription ?? string.Empty]);
            var translated = await TranslateAsync(ai, normalizedLanguageCode, normalizedModel, inputStrings, cancellationToken);
            translatedBody = translated[1];
            translatedMetaDescription = NormalizeOptional(translated[2]);
            return await UpsertAiDraftAsync(
                contentType, contentId, normalizedLanguageCode, translated[0], translatedBody,
                translatedMetaDescription, normalizedModel, performedBy, cancellationToken);
        }

        if (contentType == ContentLocalizationContentTypes.CmsPage)
        {
            var page = await db.CmsPages.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == contentId && item.TrashedAt == null, cancellationToken)
                ?? throw new InvalidOperationException($"CMS page {contentId} was not found.");
            var layout = CmsBuilderJson.ParseLayoutOrEmpty(page.BlocksJson);
            var fields = new List<(LayoutWidget Widget, string FieldKey)>();
            foreach (var widget in layout.Sections.SelectMany(section => section.Columns).SelectMany(column => column.Widgets))
            {
                // Accordion and form values are nested JSON blobs, so v1 translates only flat Props.
                if (!TranslatableWidgetFields.TryGetValue(widget.WidgetType, out var fieldKeys)) continue;
                foreach (var fieldKey in fieldKeys)
                {
                    if (widget.Props.TryGetValue(fieldKey, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        fields.Add((widget, fieldKey));
                    }
                }
            }

            inputStrings.Add(page.Title);
            inputStrings.AddRange(fields.Select(field => field.Widget.Props[field.FieldKey]));
            var metaDescriptionIndex = inputStrings.Count;
            inputStrings.Add(page.MetaDescription ?? string.Empty);
            var translated = await TranslateAsync(ai, normalizedLanguageCode, normalizedModel, inputStrings, cancellationToken);
            for (var index = 0; index < fields.Count; index++)
            {
                fields[index].Widget.Props[fields[index].FieldKey] = translated[index + 1];
            }

            translatedBody = CmsBuilderJson.Serialize(layout);
            translatedMetaDescription = NormalizeOptional(translated[metaDescriptionIndex]);
            return await UpsertAiDraftAsync(
                contentType, contentId, normalizedLanguageCode, translated[0], translatedBody,
                translatedMetaDescription, normalizedModel, performedBy, cancellationToken);
        }

        throw new InvalidOperationException($"Unknown localizable content type '{contentType}'.");
    }

    public async Task<PublishedLocalization?> GetPublishedAsync(
        string contentType,
        Guid contentId,
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        ValidateContentType(contentType);
        var normalizedLanguageCode = NormalizeLanguageCode(languageCode);
        return await db.ContentLocalizations.AsNoTracking()
            .Where(item => item.ContentType == contentType
                && item.ContentId == contentId
                && item.LanguageCode == normalizedLanguageCode
                && item.Status == ContentLocalizationStatuses.Published)
            .Select(item => new PublishedLocalization(item.Title, item.Body, item.MetaDescription))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<List<string>> TranslateAsync(
        IOllamaService ai,
        string languageCode,
        string model,
        List<string> inputStrings,
        CancellationToken cancellationToken)
    {
        var displayName = LanguageDisplayNames.GetValueOrDefault(languageCode, languageCode);
        var systemPrompt = string.Format(TranslationSystemPromptTemplate, displayName);
        var raw = await ai.GenerateAsync(
            model, systemPrompt, JsonSerializer.Serialize(inputStrings), numCtx: 4096, cancellationToken);

        var translated = TryParseTranslation(raw.Trim());
        if (translated is null || translated.Count != inputStrings.Count || translated.Any(item => item is null))
        {
            logger.LogWarning(
                "Ollama returned an invalid translation payload for {LanguageCode}: expected {ExpectedCount} strings.",
                languageCode, inputStrings.Count);
            throw new InvalidOperationException(
                $"Ollama returned an invalid translation. Expected {inputStrings.Count} translated strings in a JSON array.");
        }

        return translated;
    }

    private static List<string>? TryParseTranslation(string response)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(response);
        }
        catch (JsonException)
        {
            var start = response.IndexOf('[');
            var end = response.LastIndexOf(']');
            if (start < 0 || end <= start) return null;
            try
            {
                return JsonSerializer.Deserialize<List<string>>(response[start..(end + 1)]);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private async Task<ContentLocalizationDetail> UpsertAiDraftAsync(
        string contentType,
        Guid contentId,
        string languageCode,
        string title,
        string body,
        string? metaDescription,
        string model,
        string performedBy,
        CancellationToken cancellationToken)
    {
        var localization = await FindTrackedAsync(contentType, contentId, languageCode, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (localization is null)
        {
            localization = new ContentLocalization
            {
                ContentType = contentType,
                ContentId = contentId,
                LanguageCode = languageCode,
                CreatedAt = now,
                CreatedBy = performedBy
            };
            db.ContentLocalizations.Add(localization);
        }

        localization.Title = title;
        localization.Body = body;
        localization.MetaDescription = metaDescription;
        localization.Status = ContentLocalizationStatuses.Draft;
        localization.IsAiGenerated = true;
        localization.AiModel = model;
        localization.UpdatedAt = now;
        localization.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
        return ToDetail(localization);
    }

    private Task<ContentLocalization?> FindTrackedAsync(
        string contentType,
        Guid contentId,
        string languageCode,
        CancellationToken cancellationToken) =>
        db.ContentLocalizations.FirstOrDefaultAsync(
            item => item.ContentType == contentType
                && item.ContentId == contentId
                && item.LanguageCode == languageCode,
            cancellationToken);

    private async Task EnsureSourceExistsAsync(string contentType, Guid contentId, CancellationToken cancellationToken)
    {
        var exists = contentType switch
        {
            ContentLocalizationContentTypes.Article => await db.Articles.AsNoTracking()
                .AnyAsync(item => item.Id == contentId && item.TrashedAt == null, cancellationToken),
            ContentLocalizationContentTypes.CmsPage => await db.CmsPages.AsNoTracking()
                .AnyAsync(item => item.Id == contentId && item.TrashedAt == null, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown localizable content type '{contentType}'.")
        };
        if (!exists)
        {
            throw new InvalidOperationException($"{contentType} {contentId} was not found.");
        }
    }

    private static string NormalizeLanguageCode(string languageCode)
    {
        var normalized = (languageCode ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Enter a language code before saving or generating a translation.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateContentType(string contentType)
    {
        if (contentType is not (ContentLocalizationContentTypes.Article or ContentLocalizationContentTypes.CmsPage))
        {
            throw new InvalidOperationException($"Unknown localizable content type '{contentType}'.");
        }
    }

    private static void ValidateStatus(string status)
    {
        if (status is not (ContentLocalizationStatuses.Draft or ContentLocalizationStatuses.Published))
        {
            throw new InvalidOperationException($"Unknown localization status '{status}'.");
        }
    }

    private static ContentLocalizationSummary ToSummary(ContentLocalization item) => new(
        item.Id, item.LanguageCode, item.Status, item.IsAiGenerated, item.AiModel,
        item.UpdatedAt ?? item.CreatedAt);

    private static ContentLocalizationDetail ToDetail(ContentLocalization item) => new(
        item.Id, item.ContentType, item.ContentId, item.LanguageCode, item.Title, item.Body,
        item.MetaDescription, item.Status, item.IsAiGenerated, item.AiModel,
        item.UpdatedAt ?? item.CreatedAt);
}
