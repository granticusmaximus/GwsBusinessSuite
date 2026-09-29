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
    // Phase 1 shipped 3 presets to prove the apply-a-theme mechanism end to end. Phase 2 (below)
    // fills out the remaining 12 to reach the full 15, spanning the niches decision 4 called for
    // plus the market-research niches Workstream C's own research already covered (agency, IT
    // services, SaaS) - each one deliberately distinct in palette/type/voice, not a re-skin of
    // another entry on this list.
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
                "Visit us today")),

        new CmsThemePreset(
            "soft-pastel-wellness",
            "Soft Pastel Wellness",
            "Calm and welcoming - muted mauve and blush pink on a soft cream surface, ideal for wellness/spa practices.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#6b5b73"),
                    new DesignToken("Accent", "#f2a5b8"),
                    new DesignToken("Surface", "#fdf4f5"),
                    new DesignToken("Text", "#4a3f47")
                ],
                [
                    new TypeScaleStep("Display", "2.5rem"),
                    new TypeScaleStep("Heading", "1.4rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "3.5rem"),
                    new SpacingScaleStep("Element", "1.25rem")
                ]),
            () => DefaultHomepageLayout(
                "Feel better, one small step at a time",
                "A calm, welcoming space for your wellness practice - breathe easier, starting today.",
                "Book a session")),

        new CmsThemePreset(
            "dark-tech-saas",
            "Dark Tech SaaS",
            "Dark-mode-first and modern - near-black navy with a bright cyan accent, built for developer-facing products.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#e2e8f0"),
                    new DesignToken("Accent", "#22d3ee"),
                    new DesignToken("Surface", "#0b1220"),
                    new DesignToken("Text", "#e2e8f0")
                ],
                [
                    new TypeScaleStep("Display", "3.25rem"),
                    new TypeScaleStep("Heading", "1.75rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4.5rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Ship faster with infrastructure that just works",
                "The developer platform trusted by teams who'd rather build than babysit servers.",
                "Start free")),

        new CmsThemePreset(
            "classic-editorial",
            "Classic Editorial",
            "A serif-led, newsroom feel - near-black text and a deep masthead red on warm paper white.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#1a1a1a"),
                    new DesignToken("Accent", "#a8342a"),
                    new DesignToken("Surface", "#fdfbf7"),
                    new DesignToken("Text", "#1a1a1a")
                ],
                [
                    new TypeScaleStep("Display", "3.5rem"),
                    new TypeScaleStep("Heading", "1.6rem"),
                    new TypeScaleStep("Body", "1.05rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Stories worth your time",
                "Independent reporting and essays, published without the noise.",
                "Start reading")),

        new CmsThemePreset(
            "ultra-minimal",
            "Ultra Minimal",
            "Stark monochrome and oversized type - black on white, nothing else competing for attention.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#000000"),
                    new DesignToken("Accent", "#000000"),
                    new DesignToken("Surface", "#ffffff"),
                    new DesignToken("Text", "#000000")
                ],
                [
                    new TypeScaleStep("Display", "4.5rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "5rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Less, but better",
                "Everything you need. Nothing you don't.",
                "Explore")),

        new CmsThemePreset(
            "vibrant-startup",
            "Vibrant Startup",
            "Energetic and colorful - deep indigo with a purple/pink accent pair, built for an early-stage product launch.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#1e1b4b"),
                    new DesignToken("Accent", "#8b5cf6"),
                    new DesignToken("AccentAlt", "#ec4899"),
                    new DesignToken("Surface", "#f5f3ff"),
                    new DesignToken("Text", "#1e1b4b")
                ],
                [
                    new TypeScaleStep("Display", "3.75rem"),
                    new TypeScaleStep("Heading", "1.75rem"),
                    new TypeScaleStep("Body", "1.05rem")
                ],
                [
                    new SpacingScaleStep("Section", "4.5rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "The fastest way from idea to launch",
                "Everything early-stage teams need to ship, in one place.",
                "Get started free")),

        new CmsThemePreset(
            "corporate-enterprise",
            "Corporate Enterprise",
            "Conservative and trustworthy - navy and slate blue on a cool gray surface, built for B2B software.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#0f2440"),
                    new DesignToken("Accent", "#1d4ed8"),
                    new DesignToken("Surface", "#f8fafc"),
                    new DesignToken("Text", "#0f2440")
                ],
                [
                    new TypeScaleStep("Display", "2.75rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Enterprise software your whole organization can trust",
                "Secure, compliant, and built to scale with teams of any size.",
                "Request a demo")),

        new CmsThemePreset(
            "it-services-consulting",
            "IT Services & Consulting",
            "Professional and modern - deep teal-blue with a sky-blue accent, built for managed IT/consulting firms.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#0c4a6e"),
                    new DesignToken("Accent", "#0ea5e9"),
                    new DesignToken("Surface", "#f0f9ff"),
                    new DesignToken("Text", "#0c4a6e")
                ],
                [
                    new TypeScaleStep("Display", "2.75rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Technology that works as hard as you do",
                "Managed IT, cloud, and cybersecurity support for growing businesses.",
                "Talk to an expert")),

        new CmsThemePreset(
            "creative-portfolio",
            "Creative Portfolio",
            "Bold and high-contrast - white on near-black with a punchy yellow accent, built for a design studio.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#ffffff"),
                    new DesignToken("Accent", "#fde047"),
                    new DesignToken("Surface", "#0a0a0a"),
                    new DesignToken("Text", "#ffffff")
                ],
                [
                    new TypeScaleStep("Display", "4rem"),
                    new TypeScaleStep("Heading", "1.75rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4.5rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Design that gets noticed",
                "A freelance studio for brands who want to stand out.",
                "View portfolio")),

        new CmsThemePreset(
            "restaurant-hospitality",
            "Restaurant & Hospitality",
            "Warm and appetizing - espresso brown with a burnt-terracotta accent, built for restaurants and hospitality.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#2b1a12"),
                    new DesignToken("Accent", "#c2410c"),
                    new DesignToken("Surface", "#fff7ed"),
                    new DesignToken("Text", "#2b1a12")
                ],
                [
                    new TypeScaleStep("Display", "3rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Good food, good company",
                "Fresh, seasonal dishes served with genuine hospitality.",
                "Reserve a table")),

        new CmsThemePreset(
            "nonprofit-community",
            "Nonprofit & Community",
            "Approachable and earnest - deep green with a leaf-green accent, built for nonprofits and community groups.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#14532d"),
                    new DesignToken("Accent", "#65a30d"),
                    new DesignToken("Surface", "#f7fdf2"),
                    new DesignToken("Text", "#14532d")
                ],
                [
                    new TypeScaleStep("Display", "2.75rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1.05rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Together, we can change more than we think",
                "Join a community working toward a better future for everyone.",
                "Get involved")),

        new CmsThemePreset(
            "real-estate-professional",
            "Real Estate & Professional Services",
            "Trustworthy and refined - navy with a muted-gold accent, built for real estate and professional services.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#1c2b3a"),
                    new DesignToken("Accent", "#b08d57"),
                    new DesignToken("Surface", "#f9f8f6"),
                    new DesignToken("Text", "#1c2b3a")
                ],
                [
                    new TypeScaleStep("Display", "2.75rem"),
                    new TypeScaleStep("Heading", "1.5rem"),
                    new TypeScaleStep("Body", "1rem")
                ],
                [
                    new SpacingScaleStep("Section", "4rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Find a place to call home",
                "Trusted local expertise for buyers, sellers, and everyone in between.",
                "Browse listings")),

        new CmsThemePreset(
            "fitness-sports",
            "Fitness & Sports",
            "Energetic and bold - near-black with a fire-red accent, built for gyms, coaches, and sports programs.",
            new DesignTokenSet(
                [
                    new DesignToken("Primary", "#18181b"),
                    new DesignToken("Accent", "#ef4444"),
                    new DesignToken("Surface", "#fafafa"),
                    new DesignToken("Text", "#18181b")
                ],
                [
                    new TypeScaleStep("Display", "3.5rem"),
                    new TypeScaleStep("Heading", "1.75rem"),
                    new TypeScaleStep("Body", "1.05rem")
                ],
                [
                    new SpacingScaleStep("Section", "4.5rem"),
                    new SpacingScaleStep("Element", "1.5rem")
                ]),
            () => DefaultHomepageLayout(
                "Train harder. Recover smarter.",
                "Personalized coaching and programs built around your goals.",
                "Start your free trial"))
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
