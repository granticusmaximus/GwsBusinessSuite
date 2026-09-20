using System.Text.Json;

namespace GwsBusinessSuite.Application.CmsBuilder;

// "Add a Section" starter gallery (Page Editor Phase 1) - mirrors SentinelStarterTemplates.cs's
// shape for Sentinel's "+ New page" gallery. Each entry is a factory function rather than a
// static instance, since LayoutSection/LayoutWidget self-generate a unique Id per construction
// and every placement of a template needs its own fresh set of ids.
public sealed record CmsSectionTemplate(string Key, string Icon, string Name, string Description, Func<LayoutSection> Build);

public static class CmsSectionTemplates
{
    public static readonly IReadOnlyList<CmsSectionTemplate> All =
    [
        new CmsSectionTemplate("feature-grid", "🧩", "Feature grid", "Three-column cards for highlighting features", FeatureGrid),
        new CmsSectionTemplate("testimonial-row", "💬", "Testimonial row", "Side-by-side quotes from customers", TestimonialRow),
        new CmsSectionTemplate("cta-banner", "📣", "CTA banner", "Centered headline, copy, and a call-to-action button", CtaBanner),
        new CmsSectionTemplate("team-grid", "🧑‍🤝‍🧑", "Team/people grid", "Photo cards for introducing people", TeamGrid),
        new CmsSectionTemplate("faq", "❓", "FAQ", "An accordion pre-seeded with sample questions", Faq),
        new CmsSectionTemplate("pricing-table", "💳", "Pricing table", "Three-column pricing cards with a button each", PricingTable)
    ];

    public static CmsSectionTemplate? Find(string key) =>
        All.FirstOrDefault(template => string.Equals(template.Key, key, StringComparison.OrdinalIgnoreCase));

    private static LayoutSection FeatureGrid()
    {
        var section = new LayoutSection { Label = "Feature grid", ColumnLayout = "thirds" };
        section.Columns =
        [
            OneWidgetColumn(4, Card("Feature one", "Describe the first feature here.")),
            OneWidgetColumn(4, Card("Feature two", "Describe the second feature here.")),
            OneWidgetColumn(4, Card("Feature three", "Describe the third feature here."))
        ];
        return section;
    }

    private static LayoutSection TestimonialRow()
    {
        var section = new LayoutSection { Label = "Testimonial row", ColumnLayout = "thirds" };
        section.Columns =
        [
            OneWidgetColumn(4, Testimonial("This product changed how we work.", "Alex Rivera", "Operations Lead")),
            OneWidgetColumn(4, Testimonial("Support has been fantastic from day one.", "Jordan Lee", "Customer")),
            OneWidgetColumn(4, Testimonial("Exactly what our team needed.", "Sam Patel", "Founder"))
        ];
        return section;
    }

    private static LayoutSection CtaBanner()
    {
        var section = new LayoutSection { Label = "CTA banner", ColumnLayout = "full" };
        section.Columns =
        [
            new LayoutColumn
            {
                Span = 12,
                Widgets =
                [
                    new LayoutWidget
                    {
                        WidgetType = "heading",
                        Props = new Dictionary<string, string> { ["text"] = "Ready to get started?", ["level"] = "h2", ["align"] = "center" }
                    },
                    new LayoutWidget
                    {
                        WidgetType = "paragraph",
                        Props = new Dictionary<string, string> { ["text"] = "Tell visitors what happens next and give them a clear next step.", ["align"] = "center" }
                    },
                    new LayoutWidget
                    {
                        WidgetType = "button",
                        Props = new Dictionary<string, string> { ["label"] = "Get started", ["href"] = "#", ["variant"] = "primary", ["size"] = "lg", ["align"] = "center" }
                    }
                ]
            }
        ];
        return section;
    }

    private static LayoutSection TeamGrid()
    {
        var section = new LayoutSection { Label = "Team", ColumnLayout = "thirds" };
        section.Columns =
        [
            OneWidgetColumn(4, Card("Jane Doe", "Role or title")),
            OneWidgetColumn(4, Card("John Smith", "Role or title")),
            OneWidgetColumn(4, Card("Alex Kim", "Role or title"))
        ];
        return section;
    }

    private static LayoutSection Faq()
    {
        var section = new LayoutSection { Label = "FAQ", ColumnLayout = "full" };
        var itemsJson = JsonSerializer.Serialize(new[]
        {
            new { question = "What is included?", answer = "Describe what's included here." },
            new { question = "How does billing work?", answer = "Describe billing here." },
            new { question = "Can I cancel anytime?", answer = "Describe your cancellation policy here." }
        });
        section.Columns =
        [
            new LayoutColumn
            {
                Span = 12,
                Widgets =
                [
                    new LayoutWidget
                    {
                        WidgetType = "accordion",
                        Props = new Dictionary<string, string> { ["itemsJson"] = itemsJson }
                    }
                ]
            }
        ];
        return section;
    }

    private static LayoutSection PricingTable()
    {
        var section = new LayoutSection { Label = "Pricing", ColumnLayout = "thirds" };
        section.Columns =
        [
            PricingCard("Starter", "$9/mo"),
            PricingCard("Pro", "$29/mo"),
            PricingCard("Enterprise", "Contact us")
        ];
        return section;
    }

    private static LayoutColumn OneWidgetColumn(int span, LayoutWidget widget) =>
        new() { Span = span, Widgets = [widget] };

    private static LayoutWidget Card(string title, string body) => new()
    {
        WidgetType = "card",
        Props = new Dictionary<string, string> { ["title"] = title, ["body"] = body, ["imageSrc"] = string.Empty, ["link"] = string.Empty }
    };

    private static LayoutWidget Testimonial(string quote, string authorName, string authorRole) => new()
    {
        WidgetType = "testimonial",
        Props = new Dictionary<string, string> { ["quote"] = quote, ["authorName"] = authorName, ["authorRole"] = authorRole }
    };

    private static LayoutColumn PricingCard(string plan, string price)
    {
        var column = new LayoutColumn { Span = 4 };
        column.Widgets =
        [
            new LayoutWidget { WidgetType = "heading", Props = new Dictionary<string, string> { ["text"] = plan, ["level"] = "h3", ["align"] = "center" } },
            new LayoutWidget { WidgetType = "heading", Props = new Dictionary<string, string> { ["text"] = price, ["level"] = "h2", ["align"] = "center" } },
            new LayoutWidget { WidgetType = "button", Props = new Dictionary<string, string> { ["label"] = "Choose plan", ["href"] = "#", ["variant"] = "outline-primary", ["size"] = "md", ["align"] = "center" } }
        ];
        return column;
    }
}
