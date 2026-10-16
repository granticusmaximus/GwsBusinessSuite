namespace GwsBusinessSuite.Application.Wiki;

// Presentation mode (Sentinel item 18): a page shown as slides. The first slide is the page's
// title with whatever comes before the first heading; after that every Heading 1 or Heading 2
// starts a new slide, and so does a divider (which is not shown itself). Heading 3 and everything
// else stay on the slide they're on. Slides that would be empty are left out.
public sealed record SentinelSlide(int Number, string? Heading, IReadOnlyList<WikiBlock> Blocks, bool IsTitleSlide);

public static class SentinelPresentation
{
    public static IReadOnlyList<SentinelSlide> Split(IReadOnlyList<WikiBlock> blocks)
    {
        var groups = new List<(string? Heading, List<WikiBlock> Blocks, bool IsTitle)> { (null, [], true) };
        foreach (var block in blocks)
        {
            if (block.Type == WikiBlockTypes.Divider)
            {
                groups.Add((null, [], false));
                continue;
            }
            if (block.Type is WikiBlockTypes.Heading1 or WikiBlockTypes.Heading2)
            {
                groups.Add((block.PlainText.Trim(), [block], false));
                continue;
            }
            groups[^1].Blocks.Add(block);
        }

        // The title slide always stays (it carries the page title); other empty slides go.
        var kept = groups.Where(group => group.IsTitle || group.Blocks.Any(HasContent)).ToList();
        return kept.Select((group, index) => new SentinelSlide(index + 1, group.Heading, group.Blocks, group.IsTitle)).ToList();
    }

    // Live business blocks never render outside the editor, and empty paragraphs are spacing.
    private static bool HasContent(WikiBlock block) =>
        block.Type != WikiBlockTypes.Business
        && !(block.Type == WikiBlockTypes.Paragraph && string.IsNullOrWhiteSpace(block.PlainText));
}
