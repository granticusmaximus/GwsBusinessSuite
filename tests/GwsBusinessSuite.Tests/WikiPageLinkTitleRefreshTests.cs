using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;

namespace GwsBusinessSuite.Tests;

public sealed class WikiPageLinkTitleRefreshTests
{
    [Fact]
    public void RefreshPageLinkTitles_ShouldRestampRenamedChild()
    {
        var childId = Guid.NewGuid();
        var json = WikiBlockJson.Serialize([WikiBlockJson.CreatePageLink(childId, "")]);

        var refreshed = WikiBlockJson.ParseBlocks(WikiBlockJson.RefreshPageLinkTitles(json,
            new Dictionary<Guid, (string Title, string? Icon)> { [childId] = ("Project plan", "🗂") }));

        var link = refreshed.Should().ContainSingle().Subject;
        link.Props["pageTitle"].Should().Be("Project plan");
        link.Props["pageIcon"].Should().Be("🗂");
        link.Props["pageId"].Should().Be(childId.ToString());
        link.PlainText.Should().Be("Project plan");
    }

    [Fact]
    public void RefreshPageLinkTitles_ShouldReturnInputUnchanged_WhenTitlesMatchOrPageIsUnknown()
    {
        var known = Guid.NewGuid();
        var json = WikiBlockJson.Serialize(
        [
            WikiBlockJson.CreatePageLink(known, "Same"),
            WikiBlockJson.CreatePageLink(Guid.NewGuid(), "Deleted page")
        ]);

        WikiBlockJson.RefreshPageLinkTitles(json,
                new Dictionary<Guid, (string Title, string? Icon)> { [known] = ("Same", null) })
            .Should().BeSameAs(json);
    }
}
