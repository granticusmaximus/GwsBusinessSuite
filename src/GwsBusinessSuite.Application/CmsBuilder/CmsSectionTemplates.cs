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
        new CmsSectionTemplate("pricing-table", "💳", "Pricing table", "Three-column pricing cards with a button each", PricingTable),
        new CmsSectionTemplate("newsletter-signup", "📧", "Newsletter signup", "Email capture styled as an accent-background callout", NewsletterSignup),
        new CmsSectionTemplate("quote-request-form", "📝", "Quote request form", "A form pre-seeded with contact + project details fields", QuoteRequestForm),
        new CmsSectionTemplate("appointment-request-form", "🗓️", "Appointment request form", "A form pre-seeded with contact + preferred-time fields", AppointmentRequestForm)
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

    // Workstream C, Tier 2 - promoted to the first-class "cta-banner" widget type (was 3
    // separate heading/paragraph/button widgets). Only affects NEW insertions of this template;
    // any page that already has the old 3-widget composition keeps it, unchanged.
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
                        WidgetType = "cta-banner",
                        Props = new Dictionary<string, string>
                        {
                            ["headline"] = "Ready to get started?",
                            ["body"] = "Tell visitors what happens next and give them a clear next step.",
                            ["buttonLabel"] = "Get started",
                            ["buttonHref"] = "#",
                            ["buttonVariant"] = "primary",
                            ["align"] = "center"
                        }
                    }
                ]
            }
        ];
        return section;
    }

    // Workstream C, Tier 2 - promoted to the first-class "team-grid" widget type (was 3
    // separate Card widgets in 3 columns). Same non-breaking-for-existing-pages note as
    // CtaBanner above.
    private static LayoutSection TeamGrid()
    {
        var section = new LayoutSection { Label = "Team", ColumnLayout = "full" };
        var itemsJson = JsonSerializer.Serialize(new[]
        {
            new { name = "Jane Doe", role = "Role or title", photoUrl = "", linkedinUrl = "", twitterUrl = "", emailAddress = "" },
            new { name = "John Smith", role = "Role or title", photoUrl = "", linkedinUrl = "", twitterUrl = "", emailAddress = "" },
            new { name = "Alex Kim", role = "Role or title", photoUrl = "", linkedinUrl = "", twitterUrl = "", emailAddress = "" }
        });
        section.Columns =
        [
            new LayoutColumn
            {
                Span = 12,
                Widgets = [new LayoutWidget { WidgetType = "team-grid", Props = new Dictionary<string, string> { ["itemsJson"] = itemsJson } }]
            }
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
                        Props = new Dictionary<string, string> { ["itemsJson"] = itemsJson, ["isFaq"] = "true" }
                    }
                ]
            }
        ];
        return section;
    }

    // Workstream C, Tier 3 - promoted to the first-class "pricing-table" widget type (was 3
    // fixed Card-less heading/heading/button columns with no monthly/yearly concept at all).
    // Only affects NEW insertions of this template; existing pages keep their old composition.
    private static LayoutSection PricingTable()
    {
        var section = new LayoutSection { Label = "Pricing", ColumnLayout = "full" };
        var itemsJson = JsonSerializer.Serialize(new[]
        {
            new { name = "Starter", monthlyPrice = "$9/mo", yearlyPrice = "$90/yr", features = "1 project\nEmail support", ctaLabel = "Choose plan", ctaHref = "#", highlighted = false },
            new { name = "Pro", monthlyPrice = "$29/mo", yearlyPrice = "$290/yr", features = "Unlimited projects\nPriority support\nAdvanced analytics", ctaLabel = "Choose plan", ctaHref = "#", highlighted = true },
            new { name = "Enterprise", monthlyPrice = "Contact us", yearlyPrice = "Contact us", features = "Custom contract\nDedicated support", ctaLabel = "Contact sales", ctaHref = "#", highlighted = false }
        });
        section.Columns =
        [
            new LayoutColumn
            {
                Span = 12,
                Widgets = [new LayoutWidget { WidgetType = "pricing-table", Props = new Dictionary<string, string> { ["itemsJson"] = itemsJson, ["yearlyDiscountLabel"] = "Save ~15%" } }]
            }
        ];
        return section;
    }

    // An "Email signup" widget (double opt-in, feeds an article-alert campaign) - the page editor
    // asks which campaign as soon as this template is inserted.
    private static LayoutSection NewsletterSignup()
    {
        var section = new LayoutSection { Label = "Newsletter signup", ColumnLayout = "full", Background = "accent" };
        section.Columns =
        [
            new LayoutColumn
            {
                Span = 12,
                Widgets =
                [
                    new LayoutWidget
                    {
                        WidgetType = "email-signup",
                        Props = new Dictionary<string, string>
                        {
                            ["campaignId"] = string.Empty,
                            ["heading"] = "Stay in the loop",
                            ["description"] = "Get an email whenever a new article is published.",
                            ["showFirstName"] = "true",
                            ["buttonLabel"] = "Subscribe",
                            ["consentText"] = "No spam. Unsubscribe any time.",
                            ["successMessage"] = "Almost done - check your inbox and click the link to confirm.",
                            ["align"] = "center"
                        }
                    }
                ]
            }
        ];
        return section;
    }

    // Workstream C, Tier 1 (form presets) - identical pattern to NewsletterSignup above: no new
    // renderer/widget code, just a pre-seeded generic "form" widget. Field "role" values (email/
    // name/phone/company) match Program.cs's ResolveFormFieldMetadata / FormSubmissionService's
    // structured columns - see CmsBuilderEditor.razor's FormFieldRoles for the full set.
    private static LayoutSection QuoteRequestForm()
    {
        var section = new LayoutSection { Label = "Quote request form", ColumnLayout = "full" };
        var fieldsJson = JsonSerializer.Serialize(new[]
        {
            new { key = "fullName", label = "Full Name", type = "text", required = true, optionsJson = "", role = "name" },
            new { key = "email", label = "Email Address", type = "email", required = true, optionsJson = "", role = "email" },
            new { key = "phone", label = "Phone Number", type = "tel", required = false, optionsJson = "", role = "phone" },
            new { key = "company", label = "Company (if applicable)", type = "text", required = false, optionsJson = "", role = "company" },
            new { key = "projectDetails", label = "Tell us about your project", type = "textarea", required = true, optionsJson = "", role = "none" }
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
                        WidgetType = "heading",
                        Props = new Dictionary<string, string> { ["text"] = "Request a Quote", ["level"] = "h2", ["align"] = "center" }
                    },
                    new LayoutWidget
                    {
                        WidgetType = "paragraph",
                        Props = new Dictionary<string, string> { ["text"] = "Tell us a bit about what you need and we'll follow up with pricing.", ["align"] = "center" }
                    },
                    new LayoutWidget
                    {
                        WidgetType = "form",
                        Props = new Dictionary<string, string> { ["fieldsJson"] = fieldsJson, ["submitLabel"] = "Request Quote", ["autoCreateContact"] = "true" }
                    }
                ]
            }
        ];
        return section;
    }

    private static LayoutSection AppointmentRequestForm()
    {
        var section = new LayoutSection { Label = "Appointment request form", ColumnLayout = "full" };
        var timeOptionsJson = JsonSerializer.Serialize(new[] { "Morning", "Afternoon", "Evening" });
        var fieldsJson = JsonSerializer.Serialize(new[]
        {
            new { key = "fullName", label = "Full Name", type = "text", required = true, optionsJson = "", role = "name" },
            new { key = "email", label = "Email Address", type = "email", required = true, optionsJson = "", role = "email" },
            new { key = "phone", label = "Phone Number", type = "tel", required = true, optionsJson = "", role = "phone" },
            new { key = "preferredDate", label = "Preferred Date", type = "text", required = true, optionsJson = "", role = "none" },
            new { key = "preferredTime", label = "Preferred Time", type = "select", required = true, optionsJson = timeOptionsJson, role = "none" },
            new { key = "notes", label = "Anything we should know beforehand?", type = "textarea", required = false, optionsJson = "", role = "none" }
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
                        WidgetType = "heading",
                        Props = new Dictionary<string, string> { ["text"] = "Request an Appointment", ["level"] = "h2", ["align"] = "center" }
                    },
                    new LayoutWidget
                    {
                        WidgetType = "paragraph",
                        Props = new Dictionary<string, string> { ["text"] = "Let us know when works for you and we'll confirm a time.", ["align"] = "center" }
                    },
                    new LayoutWidget
                    {
                        WidgetType = "form",
                        Props = new Dictionary<string, string> { ["fieldsJson"] = fieldsJson, ["submitLabel"] = "Request Appointment", ["autoCreateContact"] = "true" }
                    }
                ]
            }
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

}
