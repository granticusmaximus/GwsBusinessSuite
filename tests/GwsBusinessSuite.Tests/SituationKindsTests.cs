using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

public sealed class SituationKindsTests
{
    [Fact]
    public void Classify_ShouldPickTheKindWithTheClosestExample_OrOtherWhenNothingIsClose()
    {
        var examples = new Dictionary<string, IReadOnlyList<float[]>>
        {
            [SituationKinds.Transport] = [[1f, 0f, 0f]],
            [SituationKinds.Sport] = [[0f, 1f, 0f]]
        };

        SituationKinds.Classify([9f, 2f, 0f], examples).Should().Be(SituationKinds.Transport, "a sum is compared by direction");
        SituationKinds.Classify([1f, 3f, 0f], examples).Should().Be(SituationKinds.Sport);
        SituationKinds.Classify([0f, 0f, 5f], examples).Should().Be(SituationKinds.Other, "nothing reaches the minimum score");
    }

    [Theory]
    [InlineData("Two high-speed trains collide near Madrid", SituationKinds.Transport)]
    [InlineData("Ebola outbreak tops 10,000 cases", SituationKinds.Health)]
    [InlineData("Cyclone forces evacuations; floods expected", SituationKinds.Disaster)]
    [InlineData("Drone attack on military base", SituationKinds.Conflict)]
    [InlineData("Arsenal stunned in League Cup match", SituationKinds.Sport)]
    [InlineData("Dolores Huerta, labor leader, dies at 96", SituationKinds.Other)]
    public void FromWords_ShouldStandInWhenTheModelIsDown(string headline, string kind)
    {
        SituationKinds.FromWords(headline).Should().Be(kind);
    }

    [Fact]
    public void Surge_ShouldNeedCoverageToDoubleWithSeveralNewOutlets()
    {
        SituationSurge.IsSurging(8, 3).Should().BeTrue();
        SituationSurge.IsSurging(5, 0).Should().BeTrue("a story that's new this hour");
        SituationSurge.IsSurging(6, 3).Should().BeFalse("only three new outlets");
        SituationSurge.IsSurging(30, 20).Should().BeFalse("big, but not doubling");
    }

    [Theory]
    [InlineData(SituationKinds.Disaster, "Magnitude 6.8 earthquake strikes Turkey; aftershock felt", "earthquake", "activity", SituationTag.High)]
    [InlineData(SituationKinds.Disaster, "Wildfire forces evacuations", "wildfire", "fire", SituationTag.Medium)]
    [InlineData(SituationKinds.Transport, "Train derailment near Madrid", "rail", "train-front-fill", SituationTag.High)]
    [InlineData(SituationKinds.Conflict, "Drone and missile attack on Kyiv", "strikes", "crosshair", SituationTag.High)]
    [InlineData(SituationKinds.Cyber, "Ransomware hits hospital chain", "ransomware", "bug-fill", SituationTag.Medium)]
    [InlineData(SituationKinds.Health, "Cholera outbreak spreads", "outbreak", "virus", SituationTag.High)]
    public void Subtypes_ShouldNarrowTheKind_WithTheMatchingIcon(string kind, string text, string subtype, string icon, string confidence)
    {
        var tag = SituationSubtypes.Tag(kind, text);

        tag.Should().Be(new SituationTag(kind, subtype, tag.Label, icon, confidence));
    }

    [Fact]
    public void Subtypes_ShouldFallBackToTheKindsIcon_AndOnlyUseWordsFromTheirOwnKind()
    {
        SituationSubtypes.Tag(SituationKinds.Politics, "Cabinet reshuffle announced")
            .Should().Be(new SituationTag(SituationKinds.Politics, null, SituationKinds.Label(SituationKinds.Politics), "bank2", SituationTag.Low));
        SituationSubtypes.Tag(SituationKinds.Sport, "Earthquake of a result in the derby").Subtype
            .Should().BeNull("a sport story's words don't make it a disaster");
        SituationSubtypes.Tag(null, "Anything").Icon.Should().Be("newspaper");
        SituationSubtypes.Tag(SituationKinds.Transport, "Airport strike grounds flights").Subtype
            .Should().Be("aviation", "two aviation words beat one strike word");
    }

    [Fact]
    public void Subtypes_ShouldOnlyNameIconsInTheLoadedSet()
    {
        // Bootstrap Icons 1.11.3 (App.razor) has no ship or volcano glyph, so those must not appear.
        SituationSubtypes.Icons.Should().NotContain(["ship", "sailboat", "anchor", "volcano"]);
        SituationSubtypes.Icons.Should().OnlyContain(icon => System.Text.RegularExpressions.Regex.IsMatch(icon, "^[a-z0-9-]+$"));
    }

    [Theory]
    [InlineData("U.S. strikes Yemen", new[] { "United States", "Yemen" })]
    [InlineData("US and UK sign deal", new[] { "United States", "United Kingdom" })]
    [InlineData("Help us rebuild, says mayor", new string[0])]
    [InlineData("South Korean exports rise", new[] { "South Korea" })]
    [InlineData("Storm moves on to Spain.", new[] { "Spain" })]
    public void HeadlineCountries_ShouldReadNamesShortFormsAndAdjectives(string headline, string[] countries)
    {
        HeadlineCountries.In(headline).Should().BeEquivalentTo(countries);
    }

    [Fact]
    public void GroupingRules_ShouldOnlyCallItAConflictWhenBothNamePlacesAndNoneIsShared()
    {
        var rules = new GroupingRules();

        rules.Conflicts("Floods in Spain", "Portugal floods").Should().BeTrue();
        rules.Conflicts("Spain and Portugal flood", "Portugal floods").Should().BeFalse("they share Portugal");
        rules.Conflicts("Floods in Spain", "Death toll rises in floods").Should().BeFalse("one names no place");
        new GroupingRules(PlaceGuard: false).Conflicts("Floods in Spain", "Portugal floods").Should().BeFalse();
    }

    [Fact]
    public void GroupingRules_ShouldRaiseTheBarLinearlyOnceAnEventHasBeenQuiet12Hours()
    {
        var rules = new GroupingRules(QuietPenalty: 0.05);
        var window = TimeSpan.FromHours(48);

        rules.Required(0.65, TimeSpan.FromHours(6), window).Should().Be(0.65);
        rules.Required(0.65, TimeSpan.FromHours(30), window).Should().BeApproximately(0.675, 1e-9);
        rules.Required(0.65, TimeSpan.FromHours(60), window).Should().BeApproximately(0.70, 1e-9);
        new GroupingRules(QuietPenalty: 0).Required(0.65, TimeSpan.FromHours(30), window).Should().Be(0.65);
    }

    [Fact]
    public void FeedSchedule_IsDueAfter_ShouldWaitOutTheGap()
    {
        var now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        var gap = TimeSpan.FromMinutes(15);

        FeedSchedule.IsDueAfter(null, gap, now).Should().BeTrue("never read");
        FeedSchedule.IsDueAfter(now.AddMinutes(-5), gap, now).Should().BeFalse();
        FeedSchedule.IsDueAfter(now.AddMinutes(-15), gap, now).Should().BeTrue();
        FeedSchedule.IsDueAfter(now.AddMinutes(-1), TimeSpan.Zero, now).Should().BeTrue("no gap reads every time");
    }
}
