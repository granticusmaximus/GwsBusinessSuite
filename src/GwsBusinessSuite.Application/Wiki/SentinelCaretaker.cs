namespace GwsBusinessSuite.Application.Wiki;

// Caretaker agents (Sentinel item 27): the data behind three scheduled automations - what changed
// this week, owners nudged about stale pages, and orphan pages with links to suggest. Each is an
// automation node (wiki.recentChanges, wiki.stalePages, wiki.orphanPages) that an AI Agent node
// can also call as a tool. Everything is limited to what the workflow's owner can view.

public sealed record SentinelChangedPage(Guid Id, string Title, string? UpdatedBy, DateTimeOffset UpdatedAt);
public sealed record SentinelStalePage(Guid Id, string Title, string Owner, DateTimeOffset LastEdited);
public sealed record SentinelOrphanPage(Guid Id, string Title, IReadOnlyList<SentinelRelatedPage> LinkFrom);

public interface ISentinelCaretakerService
{
    Task<IReadOnlyList<SentinelChangedPage>> RecentChangesAsync(string username, int days, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelStalePage>> StalePagesAsync(string username, int days, CancellationToken cancellationToken = default);
    // One bell notification per owner listing their stale pages, at most once per `days` window. Returns how many were sent.
    Task<int> NudgeOwnersAsync(IReadOnlyList<SentinelStalePage> pages, int days, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SentinelOrphanPage>> OrphanPagesAsync(string username, int limit, CancellationToken cancellationToken = default);
}

public static class SentinelCaretaker
{
    public const string NudgeKind = "stale-nudge";
    public const int MaxListed = 100;

    private static WikiBlock Block(string type, IReadOnlyList<WikiRichTextSpan> text) =>
        new(Guid.NewGuid(), type, 0, text, new Dictionary<string, string>());

    private static WikiRichTextSpan Link(Guid id, string title) =>
        new(string.IsNullOrWhiteSpace(title) ? "Untitled" : title, Link: $"wikilink:{id}");

    public static IReadOnlyList<WikiBlock> ChangesBlocks(IReadOnlyList<SentinelChangedPage> pages, int days)
    {
        var blocks = new List<WikiBlock> { Block(WikiBlockTypes.Paragraph, [new($"{pages.Count} page{(pages.Count == 1 ? "" : "s")} changed in the last {days} days.")]) };
        blocks.AddRange(pages.Take(MaxListed).Select(p => Block(WikiBlockTypes.BulletedListItem,
            [Link(p.Id, p.Title), new($" - {p.UpdatedBy ?? "someone"}, {p.UpdatedAt.ToLocalTime():dd-MM-yy HH:mm}")])));
        return blocks;
    }

    public static IReadOnlyList<WikiBlock> OrphanBlocks(IReadOnlyList<SentinelOrphanPage> pages)
    {
        var blocks = new List<WikiBlock> { Block(WikiBlockTypes.Paragraph, [new($"{pages.Count} page{(pages.Count == 1 ? " has" : "s have")} no links pointing to them. Linking each from a related page makes it findable.")]) };
        foreach (var page in pages.Take(MaxListed))
        {
            var spans = new List<WikiRichTextSpan> { Link(page.Id, page.Title) };
            if (page.LinkFrom.Count > 0)
            {
                spans.Add(new(" - link it from "));
                for (var i = 0; i < page.LinkFrom.Count; i++)
                {
                    if (i > 0) spans.Add(new(", "));
                    spans.Add(Link(page.LinkFrom[i].Id, page.LinkFrom[i].Title));
                }
            }
            blocks.Add(Block(WikiBlockTypes.ToDo, spans));
        }
        return blocks;
    }

    public static string NudgeMessage(IReadOnlyList<SentinelStalePage> pages, int days) =>
        pages.Count == 1
            ? $"\"{pages[0].Title}\" hasn't been edited in over {days} days. Is it still right?"
            : $"{pages.Count} pages you own haven't been edited in over {days} days, starting with \"{pages[0].Title}\". Are they still right?";
}
