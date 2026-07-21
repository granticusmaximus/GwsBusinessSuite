using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiBlockMergeTests
{
    [Fact]
    public void ThreeWayMerge_ShouldMergeEditsToDifferentBlocks()
    {
        var first = Block("First");
        var second = Block("Second");
        var baseline = WikiBlockJson.Serialize([first, second]);
        var local = WikiBlockJson.Serialize([first with { RichText = [new WikiRichTextSpan("Local")] }, second]);
        var remote = WikiBlockJson.Serialize([first, second with { RichText = [new WikiRichTextSpan("Remote")] }]);

        var result = WikiBlockMerge.ThreeWayMerge(baseline, local, remote);

        result.IsSuccess.Should().BeTrue();
        WikiBlockJson.ParseBlocks(result.MergedBlocksJson).Select(block => block.PlainText).Should().Equal("Local", "Remote");
    }

    [Fact]
    public void ThreeWayMerge_ShouldRejectDifferentEditsToSameBlock()
    {
        var block = Block("Base");
        var result = WikiBlockMerge.ThreeWayMerge(
            WikiBlockJson.Serialize([block]),
            WikiBlockJson.Serialize([block with { RichText = [new WikiRichTextSpan("Local")] }]),
            WikiBlockJson.Serialize([block with { RichText = [new WikiRichTextSpan("Remote")] }]));

        result.IsSuccess.Should().BeFalse();
        result.ConflictingBlockIds.Should().Equal(block.Id);
    }

    private static WikiBlock Block(string text) =>
        new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());
}
