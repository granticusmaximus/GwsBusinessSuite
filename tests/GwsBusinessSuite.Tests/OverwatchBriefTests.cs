using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// 19-11-26 (Grant): an Overwatch item opened in World News shows what it is, SentinelGPT's executive
// summary, the report, and what others are reporting.
public sealed class OverwatchBriefTests
{
    private static readonly OverwatchReport Report = new("conflict-confirmed", "Conflict (confirmed)", "Iran: Government (Iran)",
        "Latest, 05-09-26, Kurin district of Zahedan County: fighting between Government of Iran and Jaish al-Adl. 2 killed.",
        "minor", null, "https://ucdp.uu.se/conflict/123", "Iran Jaish al Adl Kurin", 30);

    private static FoundStory Story(string id, string headline, double? similarity, int words) =>
        new(new EventOverview(id, headline, 6, 4, ["Iran", "United Kingdom"], ["English"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, headline),
            similarity, words, null, null, []);

    [Fact]
    public void Prompt_ShouldCarryTheReport_AndEachStoryWithItsSummary()
    {
        var prompt = OverwatchBrief.Prompt(Report, [
            new BriefCoverage(Story("e1", "Deadly clashes in Sistan-Baluchestan", 0.7, 2), "Two Basij members were killed near Zahedan."),
            new BriefCoverage(Story("e2", "Iran border tensions", 0.48, 0), null)]);

        prompt.Should().StartWith("REPORT (Conflict (confirmed), severity minor): Iran: Government (Iran)")
            .And.Contain("fighting between Government of Iran and Jaish al-Adl")
            .And.Contain("1. Deadly clashes in Sistan-Baluchestan (4 outlets, Iran, United Kingdom; close match)")
            .And.Contain("   Two Basij members were killed near Zahedan.")
            .And.Contain("2. Iran border tensions (4 outlets, Iran, United Kingdom; loose match)");
        OverwatchBrief.SystemPrompt.Should().Contain("Use only what you are given").And.Contain("doesn't confirm it yet");
    }

    [Fact]
    public void Prompt_ShouldSaySo_WhenTheNewsHasNothing()
    {
        OverwatchBrief.Prompt(Report, []).Should().Contain("NEWS COVERAGE: no World News story found in the last 30 days.");
    }

    [Fact]
    public void SearchWordsDaysAndLinks_ShouldSuitTheItem()
    {
        OverwatchBrief.DaysFor("conflict-confirmed").Should().Be(30, "UCDP runs a month behind");
        OverwatchBrief.DaysFor("conflict").Should().Be(3);
        OverwatchBrief.DaysFor("earthquake").Should().Be(7);
        OverwatchBrief.QueryFor("Air strike reported - Kharkiv, Kharkiv, Ukraine", null).Should().Be("Air strike Kharkiv, Kharkiv, Ukraine");
        OverwatchBrief.QueryFor("anything", "Iran Jaish al Adl").Should().Be("Iran Jaish al Adl");
        OverwatchBrief.SafeLink("https://ucdp.uu.se/conflict/1").Should().Be("https://ucdp.uu.se/conflict/1");
        OverwatchBrief.SafeLink("javascript:alert(1)").Should().BeNull("a link from the address bar is only shown if it's a web link");
    }

    [Fact]
    public void Order_ShouldListCloseMatchesFirst()
    {
        var ordered = OverwatchBrief.Order([Story("loose", "a", 0.46, 0), Story("close", "b", 0.6, 0), Story("words", "c", null, 3)]);
        ordered.Select(s => s.Event.Id).Should().Equal("close", "words", "loose");
    }

    [Fact]
    public void BriefPage_ShouldBeAdminOnly_AtTheAddressOverwatchOpens()
    {
        var page = typeof(GwsBusinessSuite.Web.Components.Pages.BusinessSuite.WorldNewsOverwatchBrief);
        System.Reflection.CustomAttributeExtensions.GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>(page)
            .Select(a => a.Template).Should().Contain("/admin/world-news/brief");
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(page)!
            .Policy.Should().Be("AdminOnly");
    }
}
