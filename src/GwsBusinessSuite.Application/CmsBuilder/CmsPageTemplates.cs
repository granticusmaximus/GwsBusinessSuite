using System.Text.Json;

namespace GwsBusinessSuite.Application.CmsBuilder;

// Pre-built, fully composed pages offered by "Create new page > From a pre-built page" - the
// page-level counterpart to CmsSectionTemplates (single sections) and the same code-defined,
// static-list shape as CmsThemePresets. Every template is assembled only from existing widget
// types, and carries no colors of its own: section backgrounds use the light/dark/accent
// variants, so a template always wears whichever theme the site has applied. Build is a Func
// for the same reason as CmsSectionTemplate.Build - each use needs fresh section/widget ids.
public sealed record CmsPageTemplate(
    string Key,
    string Name,
    string Category,
    string Description,
    string DefaultTitle,
    Func<PageLayout> Build);

public static class CmsPageTemplates
{
    public static readonly IReadOnlyList<CmsPageTemplate> All =
    [
        new("about", "About Us", "Company", "Your story, the numbers behind it, the team, and what clients say.", "About Us", About),
        new("services", "Services", "Company", "What you offer, how you work, and a clear call to action.", "Services", Services),
        new("pricing", "Pricing", "Sales", "Plans with a monthly/yearly toggle, common questions, and a final nudge.", "Pricing", Pricing),
        new("contact", "Contact", "Company", "Ways to reach you side by side with a contact form, plus quick answers.", "Contact Us", Contact),
        new("team", "Our Team", "Company", "Introduce the people behind the work, with a hiring call to action.", "Our Team", Team),
        new("faq", "FAQ / Help", "Support", "Grouped answers to common questions and a way to get more help.", "Frequently Asked Questions", FaqPage),
        new("portfolio", "Portfolio / Case Study", "Showcase", "A featured project told as challenge, solution and results.", "Our Work", Portfolio),
        new("blog", "Blog / News", "Content", "Your latest posts pulled in live, with a newsletter signup.", "News", Blog),
        new("landing", "Product Landing Page", "Sales", "A launch page: headline, proof, features, testimonials and signup.", "Introducing Our Product", Landing),
        new("coming-soon", "Coming Soon", "Sales", "A simple holding page that collects emails before you launch.", "Coming Soon", ComingSoon),
        new("plan-a-visit", "Plan Your Visit", "Community", "Times, location and what to expect for first-time visitors, with a visit request form.", "Plan Your Visit", PlanAVisit),
        new("get-involved", "Get Involved", "Community", "Ways to serve, groups to join, upcoming steps and a way to sign up.", "Get Involved", GetInvolved)
    ];

    public static CmsPageTemplate? Find(string key) =>
        All.FirstOrDefault(template => string.Equals(template.Key, key, StringComparison.OrdinalIgnoreCase));

    private static PageLayout About() => Page(
        Hero("Built by people who care about the details",
            "We started with a simple idea: do excellent work, be honest about it, and treat every client like a partner.",
            "Work with us", "/contact", "Meet the team", "/team", image: "about-team.jpg"),
        Section("Our story", "transparent", "lg", "half-half",
            Col(6, Heading("Our story"), RichText(
                "What began as a two-person studio has grown into a team trusted by organizations of every size.\n\n" +
                "We still run the business the way we started it: **small enough to know every client by name**, " +
                "experienced enough to handle the hard problems.")),
            Col(6, Heading("What we believe", "h3"), RichText(
                "- Clear communication beats clever jargon\n- Quality is a habit, not a phase\n- Long relationships matter more than quick wins"))),
        Section("By the numbers", "light", "md", "full", Col(12, Stats(("10", "+", "Years in business"), ("250", "+", "Projects delivered"), ("98", "%", "Client satisfaction")))),
        Section("Team", "transparent", "lg", "full", Col(12, Heading("Meet the team", "h2", "center"), TeamGrid())),
        Section("Testimonials", "light", "lg", "full", Col(12, TestimonialSlider())),
        CtaSection("Let's build something together", "Tell us where you're headed and we'll show you how we can help.", "Start a conversation", "/contact"));

    private static PageLayout Services() => Page(
        Hero("Services that move your business forward",
            "From first idea to ongoing support, we bring strategy, design and engineering under one roof.",
            "Get a quote", "/contact", "See pricing", "/pricing", image: "services-meeting.jpg"),
        Section("What we do", "transparent", "lg", "thirds",
            Col(4, Card("Strategy", "Workshops and roadmaps that turn goals into a plan everyone can follow.")),
            Col(4, Card("Design", "Clean, accessible interfaces your customers will actually enjoy using.")),
            Col(4, Card("Development", "Reliable, well-tested software built to grow with you."))),
        Section("How we work", "light", "lg", "full",
            Col(12, Heading("How we work", "h2", "center"), ProcessSteps(
                ("Discover", "We learn your goals, your users and what success looks like."),
                ("Plan", "A clear scope, timeline and budget - no surprises later."),
                ("Build", "Short cycles with regular demos so you see progress every week."),
                ("Support", "Launch is the beginning; we stay on to improve and maintain.")))),
        CmsSectionTemplates.Find("testimonial-row")!.Build(),
        CtaSection("Not sure where to start?", "Book a free 30-minute call and we'll point you in the right direction.", "Book a call", "/contact"));

    private static PageLayout Pricing() => Page(
        Hero("Simple, transparent pricing", "Pick the plan that fits today and change it anytime as you grow.", "", "", "", "", align: "center"),
        Section("Plans", "transparent", "lg", "full", Col(12, PricingTable())),
        Section("Questions", "light", "lg", "full", Col(12, Heading("Pricing questions", "h2", "center"), Faq(
            ("Can I change plans later?", "Yes - upgrade or downgrade at any time and we'll prorate the difference."),
            ("Is there a free trial?", "Every plan starts with a 14-day trial. No card required."),
            ("Do you offer discounts for nonprofits?", "Yes. Contact us and we'll set you up with nonprofit pricing.")))),
        CtaSection("Need something custom?", "Larger teams and special requirements get a plan built around them.", "Talk to sales", "/contact"));

    private static PageLayout Contact() => Page(
        Hero("We'd love to hear from you", "Questions, ideas or a project in mind - send a message and we'll reply within one business day.", "", "", "", "", image: "contact-office.jpg"),
        Section("Contact", "transparent", "lg", "one-third-two-thirds",
            Col(4, Heading("Get in touch", "h3"), RichText(
                "**Email**  \nhello@example.com\n\n**Phone**  \n(555) 123-4567\n\n**Office hours**  \nMonday-Friday, 9am-5pm")),
            Col(8, Form("Send message",
                ("fullName", "Full Name", "text", true, "name"),
                ("email", "Email Address", "email", true, "email"),
                ("phone", "Phone Number", "tel", false, "phone"),
                ("message", "How can we help?", "textarea", true, "none")))),
        Section("Quick answers", "light", "lg", "full", Col(12, Heading("Quick answers", "h2", "center"), Faq(
            ("How quickly will you respond?", "We reply to every message within one business day."),
            ("Can we meet in person?", "Absolutely - mention it in your message and we'll set up a time."),
            ("Do you work with clients remotely?", "Yes, most of our clients work with us entirely online.")))));

    private static PageLayout Team() => Page(
        Hero("The people behind the work", "A small, experienced team that takes ownership from the first call to the final handoff.", "", "", "", "", image: "team-collaboration.jpg"),
        Section("Team", "transparent", "lg", "full", Col(12, TeamGrid(
            ("Jane Doe", "Founder & CEO"), ("John Smith", "Head of Engineering"), ("Alex Kim", "Design Lead"),
            ("Maria Garcia", "Project Manager"), ("Sam Patel", "Senior Developer"), ("Chris Lee", "Client Success")))),
        Section("Values", "light", "lg", "thirds",
            Col(4, Card("Ownership", "We treat every project as if it were our own business on the line.")),
            Col(4, Card("Curiosity", "We ask why until we truly understand the problem.")),
            Col(4, Card("Kindness", "Great work and good people are not a trade-off."))),
        CtaSection("Want to join us?", "We're always glad to meet talented people. Tell us about yourself.", "See open roles", "/contact"));

    private static PageLayout FaqPage() => Page(
        Hero("How can we help?", "Answers to the questions we hear most often.", "", "", "", "", align: "center"),
        Section("Topics", "transparent", "lg", "full", Col(12, Tabs(
            ("Getting started", "**New here?** Create an account, choose a plan, and follow the setup checklist - most people are up and running in under an hour."),
            ("Billing", "Invoices are sent monthly. You can update your payment method or download past invoices from your account at any time."),
            ("Account", "Change your email, password or notification preferences from the account settings page.")))),
        Section("FAQ", "light", "lg", "full", Col(12, Faq(
            ("How do I reset my password?", "Use the 'Forgot password' link on the sign-in page and follow the email instructions."),
            ("Can I export my data?", "Yes - every account can export its data at any time from settings."),
            ("Do you have a status page?", "Yes, we publish uptime and incident updates on our status page."),
            ("Where can I report a bug?", "Send us a message with steps to reproduce it and we'll take a look right away.")))),
        CtaSection("Still have questions?", "Our team is happy to help with anything not covered here.", "Contact support", "/contact"));

    private static PageLayout Portfolio() => Page(
        Hero("Work we're proud of", "A closer look at how we help clients solve real problems.", "Start a project", "/contact", "", ""),
        Section("Featured project", "transparent", "lg", "full", Col(12, CaseStudy())),
        Section("Results", "light", "md", "full", Col(12, Stats(("3", "x", "Faster checkout"), ("40", "%", "More conversions"), ("6", " wks", "From kickoff to launch")))),
        Section("Testimonial", "transparent", "lg", "full", Col(12, Testimonial("They understood our business faster than any team we've worked with.", "Jordan Lee", "VP of Product"))),
        CtaSection("Have a project in mind?", "Let's talk about what you're building and how we can help.", "Get in touch", "/contact"));

    private static PageLayout Blog() => Page(
        Hero("News & insights", "Updates, ideas and practical advice from our team.", "", "", "", "", align: "center"),
        Section("Latest posts", "transparent", "lg", "full", Col(12, Widget("posts-grid", ("count", "6"), ("columns", "3"), ("layout", "grid"),
            ("showImage", "true"), ("showExcerpt", "true"), ("showDate", "true"), ("ctaLabel", "Read more")))),
        CmsSectionTemplates.Find("newsletter-signup")!.Build());

    private static PageLayout Landing() => Page(
        Hero("Work smarter with one simple tool",
            "Everything your team needs to plan, track and ship - without the clutter.",
            "Start free trial", "/contact", "See pricing", "/pricing", image: "landing-product.jpg"),
        Section("Proof", "light", "md", "full", Col(12, Stats(("10k", "+", "Teams onboard"), ("4.9", "/5", "Average rating"), ("99.9", "%", "Uptime")))),
        Section("Features", "transparent", "lg", "thirds",
            Col(4, Card("Plan in minutes", "Drag-and-drop boards that keep everyone on the same page.")),
            Col(4, Card("Automate the busywork", "Reminders, hand-offs and reports that run themselves.")),
            Col(4, Card("See what matters", "Dashboards that show progress at a glance."))),
        Section("Testimonials", "light", "lg", "full", Col(12, TestimonialSlider())),
        Signup("signup", "Try it free for 14 days", "No credit card required. Cancel anytime.", "Start my free trial"));

    private static PageLayout ComingSoon() => Page(
        Section("Coming soon", "dark", "xl", "full", Col(12,
            Heading("Something new is on the way", "h1", "center"),
            Paragraph("We're putting the finishing touches on it. Leave your email and you'll be the first to know when we launch.", "center"))),
        Signup("notify", "Get notified at launch", "One email when we go live - nothing else.", "Notify me"));

    private static PageLayout PlanAVisit() => Page(
        Hero("We can't wait to meet you", "Here's everything you need to know before your first visit.", "Plan my visit", "/contact", "", ""),
        Section("Service times", "transparent", "lg", "thirds",
            Col(4, Card("Sunday gatherings", "9:00am and 11:00am. Arrive a few minutes early - we'll save you a seat.")),
            Col(4, Card("Where to find us", "123 Main Street. Free parking is available behind the building.")),
            Col(4, Card("For kids & families", "Safe, fun classes for every age during both gatherings."))),
        Section("What to expect", "light", "lg", "full",
            Col(12, Heading("What to expect", "h2", "center"), ProcessSteps(
                ("Arrive", "Friendly greeters will meet you at the door and help you find your way."),
                ("Connect", "Grab a coffee and meet a few people before things begin."),
                ("Gather", "About an hour of music and a message that applies to everyday life."),
                ("Next steps", "Stop by the welcome desk afterward - we'd love to say hello.")))),
        Section("Plan your visit", "transparent", "lg", "full",
            Col(12, Heading("Let us know you're coming", "h2", "center"),
                Paragraph("We'll have someone ready to welcome you and answer any questions.", "center"),
                Form("Plan my visit",
                    ("fullName", "Full Name", "text", true, "name"),
                    ("email", "Email Address", "email", true, "email"),
                    ("visitDate", "Which Sunday are you planning to visit?", "text", false, "none"),
                    ("guests", "How many in your group (including kids)?", "text", false, "none")))),
        CmsSectionTemplates.Find("faq")!.Build());

    private static PageLayout GetInvolved() => Page(
        Hero("Find your place", "There's room for everyone here. Explore ways to connect, serve and grow.", "Sign up to serve", "/contact", "", "", image: "get-involved-volunteer.jpg"),
        Section("Ways to get involved", "transparent", "lg", "thirds",
            Col(4, Card("Serve", "Join a team that welcomes guests, helps with kids, or runs events.")),
            Col(4, Card("Join a group", "Small groups meet weekly across the city for friendship and support.")),
            Col(4, Card("Give back", "Partner with local organizations making a difference in our community."))),
        Section("Getting started", "light", "lg", "full",
            Col(12, Heading("Getting started is easy", "h2", "center"), ProcessSteps(
                ("Explore", "Look through the opportunities and pick what interests you."),
                ("Sign up", "Fill out the short form below and we'll be in touch."),
                ("Jump in", "Meet your team leader and start whenever you're ready.")))),
        Section("Sign up", "transparent", "lg", "full",
            Col(12, Heading("Sign up", "h2", "center"),
                Form("Count me in",
                    ("fullName", "Full Name", "text", true, "name"),
                    ("email", "Email Address", "email", true, "email"),
                    ("phone", "Phone Number", "tel", false, "phone"),
                    ("interest", "What are you interested in?", "textarea", false, "none")))),
        CtaSection("Questions about getting involved?", "We'd be glad to help you find the right fit.", "Contact us", "/contact"));

    // ── Composition helpers ───────────────────────────────────────────────────

    private static PageLayout Page(params LayoutSection[] sections) => new() { Sections = [.. sections] };

    private static LayoutSection Section(string label, string background, string padding, string columnLayout, params LayoutColumn[] columns) =>
        new() { Label = label, Background = background, Padding = padding, ColumnLayout = columnLayout, Columns = [.. columns] };

    private static LayoutColumn Col(int span, params LayoutWidget[] widgets) => new() { Span = span, Widgets = [.. widgets] };

    private static LayoutWidget Widget(string type, params (string Key, string Value)[] props) =>
        new() { WidgetType = type, Props = props.ToDictionary(p => p.Key, p => p.Value) };

    // Bundled stock photos (wwwroot/img/page-templates, see its CREDITS.md) - local files, so a
    // pre-built page never depends on a third-party image host.
    private const string Photos = "/img/page-templates/";

    // An image switches the hero to its "split" layout (text beside the photo).
    private static LayoutSection Hero(string headline, string subline, string cta1Label, string cta1Href, string cta2Label, string cta2Href, string align = "left", string image = "") =>
        Section("Hero", "transparent", "xl", "full", Col(12, Widget("hero",
            ("headline", headline), ("subline", subline),
            ("cta1Label", cta1Label), ("cta1Href", cta1Href), ("cta2Label", cta2Label), ("cta2Href", cta2Href),
            ("align", align), ("layout", image.Length > 0 ? "split" : "default"), ("backgroundVideoUrl", ""), ("posterImageUrl", ""),
            ("overlayOpacity", "40"), ("splitImageUrl", image.Length > 0 ? Photos + image : ""), ("splitImagePosition", "right"))));

    private static LayoutSection CtaSection(string headline, string body, string buttonLabel, string buttonHref) =>
        Section("Call to action", "accent", "lg", "full", Col(12, Widget("cta-banner",
            ("headline", headline), ("body", body), ("buttonLabel", buttonLabel), ("buttonHref", buttonHref),
            ("buttonVariant", "primary"), ("align", "center"))));

    // An Email signup widget: the editor asks which campaign it feeds when the page first opens it.
    private static LayoutSection Signup(string anchorLabel, string headline, string body, string submitLabel) =>
        Section(anchorLabel, "accent", "lg", "full", Col(12, Widget("email-signup",
            ("campaignId", ""), ("heading", headline), ("description", body), ("showFirstName", "true"),
            ("buttonLabel", submitLabel), ("consentText", "No spam. Unsubscribe any time."),
            ("successMessage", "Almost done - check your inbox and click the link to confirm."), ("align", "center"))));

    private static LayoutWidget Heading(string text, string level = "h2", string align = "left") =>
        Widget("heading", ("text", text), ("level", level), ("align", align));

    private static LayoutWidget Paragraph(string text, string align = "left") =>
        Widget("paragraph", ("text", text), ("align", align));

    private static LayoutWidget RichText(string markdown) => Widget("richtext", ("content", markdown));

    private static LayoutWidget Card(string title, string body) =>
        Widget("card", ("title", title), ("body", body), ("imageSrc", ""), ("link", ""));

    private static LayoutWidget Testimonial(string quote, string authorName, string authorRole) =>
        Widget("testimonial", ("quote", quote), ("authorName", authorName), ("authorRole", authorRole));

    private static LayoutWidget Stats(params (string Value, string Suffix, string Label)[] items) =>
        Widget("stats", ("itemsJson", Json(items.Select(i => new { value = i.Value, suffix = i.Suffix, label = i.Label }))));

    private static LayoutWidget ProcessSteps(params (string Title, string Description)[] steps) =>
        Widget("process-steps", ("itemsJson", Json(steps.Select(s => new { title = s.Title, description = s.Description }))));

    private static LayoutWidget Tabs(params (string Label, string Content)[] tabs) =>
        Widget("tabs", ("itemsJson", Json(tabs.Select(t => new { label = t.Label, content = t.Content }))));

    private static LayoutWidget Faq(params (string Question, string Answer)[] items) =>
        Widget("accordion", ("itemsJson", Json(items.Select(i => new { question = i.Question, answer = i.Answer }))), ("isFaq", "true"));

    private static LayoutWidget TeamGrid(params (string Name, string Role)[] people)
    {
        var members = people.Length > 0 ? people : [("Jane Doe", "Founder"), ("John Smith", "Lead Engineer"), ("Alex Kim", "Designer")];
        return Widget("team-grid", ("itemsJson", Json(members.Select(p => new
        {
            name = p.Name, role = p.Role, photoUrl = "", linkedinUrl = "", twitterUrl = "", emailAddress = ""
        }))));
    }

    private static LayoutWidget TestimonialSlider() => Widget("testimonial-slider", ("itemsJson", Json(new[]
    {
        new { quote = "They delivered exactly what we needed, on time and on budget.", authorName = "Alex Rivera", authorRole = "Operations Lead", avatarUrl = "" },
        new { quote = "Responsive, thoughtful and genuinely invested in our success.", authorName = "Jordan Lee", authorRole = "Founder", avatarUrl = "" },
        new { quote = "The best partner we've worked with - we'll be back for the next project.", authorName = "Sam Patel", authorRole = "Director", avatarUrl = "" }
    })));

    private static LayoutWidget PricingTable() => Widget("pricing-table", ("yearlyDiscountLabel", "Save ~15%"), ("itemsJson", Json(new object[]
    {
        new { name = "Starter", monthlyPrice = "$19/mo", yearlyPrice = "$190/yr", features = "1 project\nCore features\nEmail support", ctaLabel = "Choose Starter", ctaHref = "/contact", highlighted = false },
        new { name = "Growth", monthlyPrice = "$49/mo", yearlyPrice = "$490/yr", features = "10 projects\nAdvanced features\nPriority support", ctaLabel = "Choose Growth", ctaHref = "/contact", highlighted = true },
        new { name = "Business", monthlyPrice = "$99/mo", yearlyPrice = "$990/yr", features = "Unlimited projects\nTeam permissions\nDedicated manager", ctaLabel = "Choose Business", ctaHref = "/contact", highlighted = false }
    })));

    private static LayoutWidget CaseStudy() => Widget("case-study",
        ("title", "Rebuilding checkout for a growing retailer"), ("clientName", "Example Co."), ("imageUrl", Photos + "portfolio-dashboard.jpg"),
        ("summary", "A slow, confusing checkout was costing sales. We rebuilt it from the ground up in six weeks."),
        ("sectionsJson", Json(new[]
        {
            new { heading = "The Challenge", body = "Nearly half of shoppers abandoned their carts at checkout, and mobile was the worst." },
            new { heading = "The Solution", body = "A streamlined, single-page checkout with saved details and faster payment options." },
            new { heading = "The Results", body = "Conversions rose 40% within the first month, with the biggest gains on mobile." }
        })),
        ("externalUrl", ""), ("externalLabel", "Visit project"));

    private static LayoutWidget Form(string submitLabel, params (string Key, string Label, string Type, bool Required, string Role)[] fields) =>
        Widget("form", ("submitLabel", submitLabel), ("autoCreateContact", "true"), ("fieldsJson", Json(fields.Select(f => new
        {
            key = f.Key, label = f.Label, type = f.Type, required = f.Required, optionsJson = "", role = f.Role
        }))));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
