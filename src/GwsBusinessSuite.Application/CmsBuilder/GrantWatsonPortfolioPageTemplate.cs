namespace GwsBusinessSuite.Application.CmsBuilder;

// Seeded once (see EnsurePortfolioPageAsync in Program.cs) as a real page under the live
// grantwatson-dev site, curated 2026-08-15 from a review of github.com/granticusmaximus's
// repositories - mirrors GrantWatsonHomepageTemplate's shape (a static PageLayout builder
// serialized via CmsBuilderJson.Serialize) rather than introducing a new content-authoring
// pattern. Unlike the homepage template, this one is create-once-and-leave-alone: there is no
// ShouldApplyTemplate reapply check, since a portfolio is exactly the kind of content an admin
// is expected to keep curating by hand afterward (reorder projects, swap descriptions, add new
// ones) - overwriting it on every restart the way the homepage's legacy-content migration does
// would silently discard that editing.
public static class GrantWatsonPortfolioPageTemplate
{
    public const string Slug = "portfolio";
    public const string MetaTitle = "Portfolio | Grant Watson";
    public const string MetaDescription = "A curated selection of software projects by Grant Watson, spanning C#/.NET, TypeScript, Swift, and JavaScript - full-stack apps, real-time systems, and desktop tools.";

    private const string GitHubProfileUrl = "https://github.com/granticusmaximus";

    // ghchart.rshah.org renders a public GitHub contribution heatmap as a plain SVG image from
    // a username alone - no auth, no API token, no backend work on this app's side. Colored to
    // match this site's own accent (CmsSite.AccentColorHex, #f59e0b) for a branded touch.
    private const string ContributionGraphHtml = """
        <div style="text-align:center;padding:1.5rem 1rem;">
          <img src="https://ghchart.rshah.org/f59e0b/granticusmaximus"
               alt="Grant Watson's GitHub contribution graph"
               style="max-width:100%;height:auto;" loading="lazy" />
          <p class="gws-paragraph" style="margin-top:0.75rem;">
            <a href="https://github.com/granticusmaximus" target="_blank" rel="noopener noreferrer">@granticusmaximus on GitHub</a>
          </p>
        </div>
        """;

    public static string CreateBlocksJson() => CmsBuilderJson.Serialize(CreateLayout());

    public static PageLayout CreateLayout() =>
        new()
        {
            Sections =
            [
                Section(
                    label: "Hero",
                    background: "transparent",
                    padding: "lg",
                    columnLayout: "full",
                    Column(
                        HeroWidget(
                            headline: "Portfolio",
                            subline: "A selection of software I've built and shipped - full-stack web apps, real-time systems, desktop tools, and a production business platform - spanning C#/.NET, TypeScript, Swift, and JavaScript.",
                            cta1Label: "View GitHub Profile",
                            cta1Href: GitHubProfileUrl,
                            cta2Label: "Get in Touch",
                            cta2Href: "/contact"))),

                Section(
                    label: "GitHub Activity",
                    background: "transparent",
                    padding: "none",
                    columnLayout: "full",
                    Column(HtmlWidget(ContributionGraphHtml))),

                Section(
                    label: "Projects Row One",
                    background: "transparent",
                    padding: "sm",
                    columnLayout: "thirds",
                    Column(ProjectCard(
                        "GwsBusinessSuite", "C#",
                        "An all-in-one business management platform for content, operations, intelligence, collaboration, and knowledge - Clean Architecture, Blazor Server, .NET MAUI, and EF Core.",
                        "GwsBusinessSuite")),
                    Column(ProjectCard(
                        "GrantOS.Sentinel", "C#",
                        "A local-first AI coding and knowledge companion - Blazor Server wrapped in Electron.NET, talking to a local Ollama server with all data in one SQLite file. No cloud, no API keys, no telemetry.",
                        "GrantOS.Sentinel")),
                    Column(ProjectCard(
                        "GwsMeet", "C#",
                        "A functional Google Meet clone: Blazor Server with SignalR signaling and client-side WebRTC for a full-mesh video grid, screen sharing, persisted chat, and host moderation.",
                        "GwsMeet"))),

                Section(
                    label: "Projects Row Two",
                    background: "transparent",
                    padding: "sm",
                    columnLayout: "thirds",
                    Column(ProjectCard(
                        "GWS-Connect", "TypeScript",
                        "A privacy-first Discord/Slack-style messenger - React 19, Node/Express, Socket.io, and LiveKit calls, with end-to-end encrypted DMs, threads, reactions, and polls.",
                        "GWS-Connect")),
                    Column(ProjectCard(
                        "PodcastDirectory", "TypeScript",
                        "A React + TypeScript app for searching the iTunes/Apple Podcasts API, with a local library, category filters, and an audio player with queueing.",
                        "PodcastDirectory")),
                    Column(ProjectCard(
                        "piping-wheel", "TypeScript",
                        "A digital replica of a mechanical engineer's piping wheel reference tool, with gesture-driven rotation and zoom - live-hosted for a real niche audience.",
                        "piping-wheel"))),

                Section(
                    label: "Projects Row Three",
                    background: "transparent",
                    padding: "sm",
                    columnLayout: "thirds",
                    Column(ProjectCard(
                        "OurWish", "Swift",
                        "A native macOS SwiftUI wish-list app with local SQLite storage via GRDB - fully on-device, no network layer.",
                        "OurWish")),
                    Column(ProjectCard(
                        "Watsyn-Jarvis", "JavaScript",
                        "An engineer-focused workflow app consolidating code hosting, sprint boards, and CRM-style customer tracking, with a React + Firebase + Electron desktop build.",
                        "Watsyn-Jarvis")),
                    Column(ProjectCard(
                        "React-Workboard-with-Authentication", "JavaScript",
                        "A Jira/Trello-style workboard with authentication and authorization, built with React and Firebase Realtime Database.",
                        "React-Workboard-with-Authentication"))),

                Section(
                    label: "Projects Row Four",
                    background: "transparent",
                    padding: "sm",
                    columnLayout: "thirds",
                    Column(ProjectCard(
                        "rocket-chatter", "JavaScript",
                        "A Slack/Rocket.Chat-style messenger with React, Node, Express, and Socket.io - group and direct channels, voice/video call UI, typing indicators, and read receipts.",
                        "rocket-chatter")),
                    Column(ProjectCard(
                        "React-and-Firebase-Authentication-Boilerplate", "JavaScript",
                        "A reusable React + Firebase authentication starter kit.",
                        "React-and-Firebase-Authentication-Boilerplate"))),

                Section(
                    label: "Contact CTA",
                    background: "dark",
                    padding: "lg",
                    columnLayout: "full",
                    Column(
                        HeadingWidget("Want to see more or talk about a project?", "h2", "center"),
                        ParagraphWidget("The full project history, including older work and experiments, is on GitHub.", "center"),
                        ButtonWidget("View GitHub Profile", GitHubProfileUrl, "primary", "center"))),
            ]
        };

    private static LayoutWidget ProjectCard(string name, string language, string description, string repoSlug) =>
        CardWidget(name, $"**{language}**\n\n{description}\n\n[View on GitHub →](https://github.com/granticusmaximus/{repoSlug})");

    private static LayoutSection Section(string label, string background, string padding, string columnLayout, params LayoutColumn[] columns) =>
        new()
        {
            Label = label,
            Background = background,
            Padding = padding,
            ColumnLayout = columnLayout,
            Columns = columns.ToList()
        };

    private static LayoutColumn Column(params LayoutWidget[] widgets) =>
        new()
        {
            Widgets = widgets.ToList()
        };

    private static LayoutWidget HeroWidget(string headline, string subline, string cta1Label, string cta1Href, string cta2Label, string cta2Href) =>
        new()
        {
            WidgetType = "hero",
            Props = new Dictionary<string, string>
            {
                ["headline"] = headline,
                ["subline"] = subline,
                ["cta1Label"] = cta1Label,
                ["cta1Href"] = cta1Href,
                ["cta2Label"] = cta2Label,
                ["cta2Href"] = cta2Href,
                ["align"] = "center"
            }
        };

    private static LayoutWidget HeadingWidget(string text, string level, string align = "left") =>
        new()
        {
            WidgetType = "heading",
            Props = new Dictionary<string, string>
            {
                ["text"] = text,
                ["level"] = level,
                ["align"] = align
            }
        };

    private static LayoutWidget ParagraphWidget(string text, string align = "left") =>
        new()
        {
            WidgetType = "paragraph",
            Props = new Dictionary<string, string>
            {
                ["text"] = text,
                ["align"] = align
            }
        };

    private static LayoutWidget ButtonWidget(string label, string href, string variant, string align = "left") =>
        new()
        {
            WidgetType = "button",
            Props = new Dictionary<string, string>
            {
                ["label"] = label,
                ["href"] = href,
                ["variant"] = variant,
                ["align"] = align
            }
        };

    private static LayoutWidget CardWidget(string title, string body) =>
        new()
        {
            WidgetType = "card",
            Props = new Dictionary<string, string>
            {
                ["title"] = title,
                ["body"] = body
            }
        };

    private static LayoutWidget HtmlWidget(string content) =>
        new()
        {
            WidgetType = "html",
            Props = new Dictionary<string, string>
            {
                ["content"] = content
            }
        };
}
