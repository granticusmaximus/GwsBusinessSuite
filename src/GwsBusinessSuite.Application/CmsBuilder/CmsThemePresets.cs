namespace GwsBusinessSuite.Application.CmsBuilder;

// Workstream D, Phase 1 (15 unique visual themes) - a "theme" here is a named, curated bundle of
// one DesignTokenSet plus a default homepage PageLayout, mirroring CmsSectionTemplates.cs's own
// "static factory list" shape rather than a database table with an admin CRUD screen (per the
// master plan's decision 2: ship as code-defined records for v1, not user-editable data).
// HomepageLayout is a Func, not a plain PageLayout, for the exact reason CmsSectionTemplate.Build
// is one - LayoutSection/LayoutWidget self-generate a unique Id per construction, so every
// application of a theme needs its own fresh set of ids. Tokens has no such concern (DesignToken/
// DesignTokenSet carry no ids) so it's a plain value.
public sealed record CmsThemePreset(
    string Key,
    string Name,
    string Description,
    DesignTokenSet Tokens,
    Func<PageLayout> HomepageLayout);

public static class CmsThemePresets
{
    // Phase 1 ships 3 presets to prove the apply-a-theme mechanism end to end, deliberately
    // spanning distinct visual directions (per decision 4's variety requirement) rather than 3
    // similar-looking starting points. Phase 2 (once this mechanism is confirmed working) fills
    // out the remaining ~12 to reach 15.
    public static readonly IReadOnlyList<CmsThemePreset> All =
    [
        new CmsThemePreset(
            "minimal-neutral",
            "Minimal Neutral",
            "A clean, timeless default - dark neutral text on white, one restrained blue accent.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#0f172a"),
                    new DesignToken("Accent", "#2563eb"),
                    new DesignToken("Surface", "#f8fafc"),
                    new DesignToken("Text", "#0f172a")
                ],
                [
                    new TypeScaleStep("Display", "3rem"),
                    new TypeScaleStep("Heading", "1.75rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Do great work, without the overhead",
                "A clear, focused site for a team that would rather ship than fuss with their website.",
                "Get started")),

        new CmsThemePreset(
            "bold-agency",
            "Bold Agency",
            "High-contrast and confident - near-black backgrounds, a vivid accent, oversized type.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#0a0a0a"),
                    new DesignToken("Accent", "#ff3d00"),
                    new DesignToken("Surface", "#141414"),
                    new DesignToken("Text", "#fafafa")
                ],
                [
                    new TypeScaleStep("Display", "4rem"),
                    new TypeScaleStep("Heading", "2rem"),
                    new TypeScaleStep("Body", "1.05rem")
                ],
                [
                    new SpacingScaleStep("Section", "5rem"),
                    new SpacingScaleStep("Element", "1.75rem")
                ]),
            () => DefaultHomepageLayout(
                "We build brands that refuse to blend in",
                "A full-service creative agency for founders who want to stand out, not fit in.",
                "See our work")),

        new CmsThemePreset(
            "warm-local-business",
            "Warm & Local",
            "Approachable and inviting - warm browns and amber on a cream surface, ideal for a local business.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#7c2d12"),
                    new DesignToken("Accent", "#d97706"),
                    new DesignToken("Surface", "#fffbeb"),
                    new DesignToken("Text", "#451a03")
                ],
                [
                    new TypeScaleStep("Display", "2.75rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "3.5rem"),
                    new SpacingScaleStep("Element", "1.25rem")
                ]),
            () => DefaultHomepageLayout(
                "Your neighborhood's favorite for a reason",
                "Family-owned, community-loved, and open every day to serve you.",
                "Visit us today"))
    ];

    public static CmsThemePreset? Find(string key) =>
        All.FirstOrDefault(preset => string.Equals(preset.Key, key, StringComparison.OrdinalIgnoreCase));

    // A hero (theme-specific copy) followed by 3 reused CmsSectionTemplates sections - the same
    // block palette Workstreams B/C already shipped, so a theme's starter homepage looks
    // composed, not like a single hardcoded block. Every CmsSectionTemplate.Build() call here
    // generates its own fresh ids, same as any other insertion of that template.
    private static PageLayout DefaultHomepageLayout(string headline, string subline, string ctaLabel)
    {
        var heroSection = new LayoutSection
        {
            Label = "Hero",
            ColumnLayout = "full",
            Columns =
            [
                new LayoutColumn
                {
                    Span = 12,
                    Widgets =
                    [
                        new LayoutWidget
                        {
                            WidgetType = "hero",
                            Props = new Dictionary<string, string>
                            {
                                ["headline"] = headline,
                                ["subline"] = subline,
                                ["cta1Label"] = ctaLabel,
                                ["cta1Href"] = "#",
                                ["cta2Label"] = "",
                                ["cta2Href"] = "",
                                ["align"] = "center"
                            }
                        }
                    ]
                }
            ]
        };

        return new PageLayout
        {
            Sections =
            [
                heroSection,
                CmsSectionTemplates.Find("feature-grid")!.Build(),
                CmsSectionTemplates.Find("testimonial-row")!.Build(),
                CmsSectionTemplates.Find("cta-banner")!.Build()
            ]
        };
    }
}
