using System.Text.Json;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Parse/serialize for GlobalBlock.OverridableFieldsJson (a flat JSON string array of Props
// keys), plus the curated catalog of candidate keys per widget type - the set a widget author
// is offered to flag overridable when defining a Global Block. Intentionally narrower than
// every Props key on a widget type (e.g. a button's "variant"/"size" stay part of the shared
// design, not offered here) - these are specifically the human-facing content/link fields,
// the same category GwsBusinessSuite.Infrastructure.Services.ContentLocalizationService's
// TranslatableWidgetFields targets for a different purpose, but including href/link fields
// here too (unlike that map) since a per-instance CTA destination is exactly the kind of thing
// worth varying per placement.
public static class GlobalBlockOverridableFields
{
    public static readonly IReadOnlyDictionary<string, string[]> CandidatesByWidgetType = new Dictionary<string, string[]>
    {
        ["hero"] = ["headline", "subline", "cta1Label", "cta1Href", "cta2Label", "cta2Href"],
        ["heading"] = ["text"],
        ["paragraph"] = ["text"],
        ["richtext"] = ["content"],
        ["button"] = ["label", "href"],
        ["card"] = ["title", "body", "link", "imageSrc"],
        ["testimonial"] = ["quote", "authorName", "authorRole"],
        ["image"] = ["src", "alt", "caption"],
    };

    public static IReadOnlyList<string> CandidatesFor(string widgetType) =>
        CandidatesByWidgetType.TryGetValue(widgetType, out var fields) ? fields : [];

    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<string> fields) =>
        JsonSerializer.Serialize(fields.Where(field => !string.IsNullOrWhiteSpace(field)).Distinct(StringComparer.Ordinal).ToList());
}
