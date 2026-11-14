using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// #7: the story timeline merges reports, statements, fact-checks, label changes and edits in order.
public sealed class WorldNewsStoryTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 6, 10, 0, TimeSpan.Zero);

    private static WorldArticle R(string id, string outlet, string title, int minutes) =>
        new(id, $"https://{id}.example/a", title, outlet, "English", "United Kingdom", T0.AddMinutes(minutes), "test");

    [Fact]
    public void Build_ShouldTellTheStoryInOrder()
    {
        var reports = new[]
        {
            R("a", "BBC News", "Dam bursts after storm", 0),
            R("b", "The Guardian", "Dam failure floods valley", 20),
            R("c", "Sky News", "Valley flooded as dam gives way", 30),
            R("d", "Al Jazeera", "Dam collapse", 70),
            R("e", "GOV.UK", "Statement on the dam failure", 90),
            R("f", "Full Fact", "Viral dam video is from 2019: misleading", 200),
        };
        var labels = new[]
        {
            new LabelSnapshot(1, "e1", "SingleSource", "One outlet so far.", 1, 1, "v", T0.AddMinutes(5), "", "h1"),
            new LabelSnapshot(2, "e1", "SingleSource", "One outlet so far.", 1, 1, "v", T0.AddMinutes(15), "h1", "h2"),
            new LabelSnapshot(3, "e1", "Confirmed", "3 independent sources.", 3, 3, "v", T0.AddMinutes(35), "h2", "h3"),
        };
        var edits = new[] { new ArticleEdit("b", "Dam failure floods valley", "Dam failure floods valley, 3 dead", "update", T0.AddMinutes(120)) };

        var timeline = StoryTimeline.Build(reports, labels, edits);

        Assert.Equal(
            [StoryTimeline.Report, StoryTimeline.Label, StoryTimeline.Outlets, StoryTimeline.Label, StoryTimeline.Outlets, StoryTimeline.Official, StoryTimeline.Edit, StoryTimeline.FactCheck],
            timeline.Select(e => e.Kind));
        Assert.Contains("BBC News", timeline[0].Text);
        Assert.Equal("2 more outlets: The Guardian, Sky News", timeline[2].Text);
        Assert.Equal("Label changed from Single source to Confirmed: 3 independent sources.", timeline[3].Text);
        Assert.Contains("(Misleading)", timeline[^1].Text);
        Assert.Contains("The Guardian updated its headline", timeline[6].Text);
    }

    [Fact]
    public void Build_ShouldNameAtMostFiveOutletsAWave() =>
        Assert.EndsWith("and 2 more",
            StoryTimeline.Build([R("0", "First", "x", 0), .. Enumerable.Range(1, 7).Select(i => R($"{i}", $"Outlet {i}", "x", 2 + i))])[1].Text);
}
