namespace GwsBusinessSuite.Application.Wiki;

// Built-in "New page" starter gallery (Phase 5.3) - distinct from SentinelPageTemplate (the
// user-generated, save-your-own-page-as-a-template feature managed in SentinelTemplates.razor).
// These are fixed content, never persisted as rows, never editable/deletable through that CRUD
// flow - just a few ready-made starting points offered directly from the "+ New page" control,
// matching Notion's own starter gallery.
public sealed record SentinelStarterTemplate(string Key, string Icon, string Name, string Title, IReadOnlyList<WikiBlock> Blocks);

public static class SentinelStarterTemplates
{
    public static readonly IReadOnlyList<SentinelStarterTemplate> All =
    [
        new SentinelStarterTemplate("meeting-notes", "🗓️", "Meeting notes", "Meeting Notes", MeetingNotesBlocks()),
        new SentinelStarterTemplate("project-tracker", "📋", "Project tracker", "Project Tracker", ProjectTrackerBlocks()),
        new SentinelStarterTemplate("roadmap", "🧭", "Roadmap", "Roadmap", RoadmapBlocks())
    ];

    public static SentinelStarterTemplate? Find(string key) =>
        All.FirstOrDefault(template => string.Equals(template.Key, key, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<WikiBlock> MeetingNotesBlocks() =>
    [
        Heading1("Meeting Notes"),
        Paragraph("Date: · Attendees: "),
        Heading2("Agenda"),
        Bullet("Topic one"),
        Bullet("Topic two"),
        Heading2("Action items"),
        ToDo("Follow up on..."),
        ToDo("Follow up on...")
    ];

    private static IReadOnlyList<WikiBlock> ProjectTrackerBlocks() =>
    [
        Heading1("Project Tracker"),
        Paragraph("Goal: "),
        Heading2("Milestones"),
        ToDo("Kickoff"),
        ToDo("Design review"),
        ToDo("Launch"),
        Heading2("Risks"),
        Bullet("Risk one")
    ];

    private static IReadOnlyList<WikiBlock> RoadmapBlocks() =>
    [
        Heading1("Roadmap"),
        Heading2("Now"),
        Bullet("What's actively in progress"),
        Heading2("Next"),
        Bullet("What's planned after that"),
        Heading2("Later"),
        Bullet("Ideas being considered")
    ];

    private static WikiBlock Paragraph(string text) => TextBlock(WikiBlockTypes.Paragraph, text);
    private static WikiBlock Heading1(string text) => TextBlock(WikiBlockTypes.Heading1, text);
    private static WikiBlock Heading2(string text) => TextBlock(WikiBlockTypes.Heading2, text);
    private static WikiBlock Bullet(string text) => TextBlock(WikiBlockTypes.BulletedListItem, text);
    private static WikiBlock ToDo(string text) => TextBlock(WikiBlockTypes.ToDo, text);

    private static WikiBlock TextBlock(string type, string text) => new(
        Guid.NewGuid(), type, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());
}
