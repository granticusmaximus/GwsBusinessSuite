namespace GwsBusinessSuite.Application.CmsBuilder;

// Part 6.2 (portable site recipes) - same "portable graph + fresh-identity rebuild" pattern
// already used by Automation workflow import/export/templates and Sentinel database templates:
// a flat package shaped like the live entities but stripped of DB-only concerns, carrying its
// *original* ids (remapping happens entirely at import time via fresh Guid maps, not at export
// time). Wrapped in an envelope with FormatVersion/ExportedAt exactly like
// AutomationWorkflowExportEnvelope.
public sealed record CmsSiteRecipeEnvelope(int FormatVersion, DateTimeOffset ExportedAt, CmsSiteRecipePackage Site);

public sealed record CmsSiteRecipePackage(
    string Name,
    string Slug,
    string Theme,
    string CustomCss,
    string NavMenuJson,
    string FooterNavMenuJson,
    string AccentColorHex,
    string FontPairingKey,
    IReadOnlyList<CmsPageCategoryRecipeItem> Categories,
    IReadOnlyList<CmsPagePropertyRecipeItem> Properties,
    IReadOnlyList<GlobalBlockRecipeItem> GlobalBlocks,
    IReadOnlyList<CmsPageRecipeItem> Pages);

public sealed record CmsPageCategoryRecipeItem(Guid Id, string Name, string Slug);

public sealed record CmsPagePropertyRecipeItem(Guid Id, string Name, string Type, int SortOrder, string ConfigJson);

public sealed record GlobalBlockRecipeItem(Guid Id, string Name, string Kind, string? WidgetType, string Json);

public sealed record CmsPageRecipeItem(
    Guid Id,
    Guid? ParentPageId,
    Guid? CategoryId,
    string Title,
    string Slug,
    string BlocksJson,
    string MetaTitle,
    string MetaDescription,
    string OgImageUrl,
    string CanonicalUrl,
    string Tags,
    string CustomCss,
    string PropertyValuesJson);
