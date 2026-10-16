using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

// Sentinel item 18: how a page becomes slides.
public sealed class SentinelPresentationTests
{
    private static WikiBlock B(string type, string text = "") =>
        new(Guid.NewGuid(), type, 0, [new WikiRichTextSpan(text)], new Dictionary<string, string>());

    private static string Shape(IReadOnlyList<SentinelSlide> slides) =>
        string.Join(" / ", slides.Select(slide => $"{slide.Number}{(slide.IsTitleSlide ? "T" : "")}:{string.Join(",", slide.Blocks.Select(block => block.PlainText))}"));

    [Fact]
    public void Split_ShouldStartASlideAtEachH1H2OrDivider_KeepingH3AndTheIntroOnTheirSlide()
    {
        var slides = SentinelPresentation.Split(
        [
            B(WikiBlockTypes.Paragraph, "intro"),
            B(WikiBlockTypes.Heading1, "Why"),
            B(WikiBlockTypes.Paragraph, "because"),
            B(WikiBlockTypes.Heading3, "detail"),
            B(WikiBlockTypes.Divider),
            B(WikiBlockTypes.Paragraph, "after the line"),
            B(WikiBlockTypes.Heading2, "How"),
            B(WikiBlockTypes.BulletedListItem, "step")
        ]);

        Shape(slides).Should().Be("1T:intro / 2:Why,because,detail / 3:after the line / 4:How,step");
        slides[1].Heading.Should().Be("Why");
        slides[2].Heading.Should().BeNull();
    }

    [Fact]
    public void Split_ShouldDropEmptySlides_ButAlwaysKeepTheTitleSlide()
    {
        var slides = SentinelPresentation.Split(
        [
            B(WikiBlockTypes.Heading1, "First"),
            B(WikiBlockTypes.Divider),
            B(WikiBlockTypes.Paragraph, "   "),
            B(WikiBlockTypes.Divider),
            B(WikiBlockTypes.Heading2, "Second")
        ]);

        Shape(slides).Should().Be("1T: / 2:First / 3:Second", "a divider straight after a heading, or a blank line between two dividers, makes no slide");
        SentinelPresentation.Split([]).Should().ContainSingle().Which.IsTitleSlide.Should().BeTrue();
    }

    [Fact]
    public void RenderedSlides_ShouldNotShowLiveBusinessData()
    {
        var live = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Business, 0, [],
            new Dictionary<string, string> { ["kind"] = SentinelLiveBlockKinds.Contact, ["recordLabel"] = "Sam Client" });
        var slides = SentinelPresentation.Split([B(WikiBlockTypes.Heading1, "Clients"), live, B(WikiBlockTypes.Divider), live]);

        slides.Should().HaveCount(2, "a slide holding only a live block is empty");
        WikiBlockHtmlRenderer.RenderPage(slides[1].Blocks).Should().NotContain("Sam Client");
    }
}
