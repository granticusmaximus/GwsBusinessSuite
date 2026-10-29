using FluentAssertions;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

public sealed class CoverageClaimsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Two high-speed trains collide in Spain, at least 64 dead", "deaths", 64, ClaimQualifier.AtLeast)]
    [InlineData("64 Killed As 2 Trains Collide In Spain", "deaths", 64, ClaimQualifier.Exact)]
    [InlineData("Death toll rises to 68 in Spain train crash", "deaths", 68, ClaimQualifier.Exact)]
    [InlineData("Ebola outbreak tops 10,000 cases as deaths near 4,900", "deaths", 4900, ClaimQualifier.About)]
    [InlineData("Ebola outbreak tops 10,000 cases as deaths near 4,900", "cases", 10000, ClaimQualifier.AtLeast)]
    [InlineData("Más de 100.000 evacuados en India por el ciclón", "evacuated", 100000, ClaimQualifier.AtLeast)]
    [InlineData("France: 1 300 interpellations après les manifestations", "arrested", 1300, ClaimQualifier.Exact)]
    [InlineData("Mindestens 12 Tote nach Unwetter", "deaths", 12, ClaimQualifier.AtLeast)]
    [InlineData("Police on Oct. 26 arrested three suspects", "arrested", 3, ClaimQualifier.Exact)]
    public void Extract_ShouldReadTheFigureAndWhatItCounts(string headline, string measure, long value, ClaimQualifier qualifier)
    {
        CoverageClaims.Extract(headline, null).Should().Contain(f => f.Measure == measure && f.Value == value && f.Qualifier == qualifier);
    }

    [Theory]
    [InlineData("Court hears two cases against the minister")]
    [InlineData("10,000 confirmed cases as deaths mount")]
    [InlineData("Released on Oct. 26 after appeal")]
    public void Extract_ShouldNotReadFiguresThatArentThere(string headline)
    {
        CoverageClaims.Extract(headline, null).Where(f => f.Measure is "deaths" or "cases" or "arrested")
            .Should().NotContain(f => f.Measure == "deaths" || (f.Measure == "cases" && !headline.Contains("confirmed")));
    }

    [Fact]
    public void Extract_ShouldKeepAPersonsAgeWithTheirName()
    {
        CoverageClaims.Extract("Stephen Hubbard, 72, released from Russian captivity", null)
            .Should().ContainSingle(f => f.Measure == "age:Stephen Hubbard" && f.Value == 72);
        CoverageClaims.Extract("74-year-old Stephen Hubbard returns home", null)
            .Should().ContainSingle(f => f.Measure == "age:Stephen Hubbard" && f.Value == 74);
    }

    [Fact]
    public void For_ShouldShowWhereCoverageDisagrees_WithEachValuesOwnSources()
    {
        var claims = CoverageClaims.For([
            Report("Reuters", "At least 1,000 arrested in French protests", 0),
            Report("AFP", "Over 1,000 detained as protests spread", 1),
            Report("Le Monde", "1 300 interpellations lors des manifestations", 2),
            Report("BFM", "1.300 personnes interpellées", 3),
            Report("BBC", "Stephen Hubbard, 72, freed", 0),
            Report("CNN", "74-year-old Stephen Hubbard freed", 1)
        ]);

        var arrests = claims.Single(c => c.Measure == "arrested");
        arrests.Disputed.Should().BeFalse("\"at least 1,000\" and \"over 1,000\" are both true if 1,300 were arrested");
        arrests.Values.Select(v => (v.Value, v.Outlets)).Should().Contain((1300, 2));

        var age = claims.Single(c => c.Measure == "age:Stephen Hubbard");
        age.Disputed.Should().BeTrue("72 and 74 can't both be right");
        age.Corroborated.Should().BeFalse("one outlet each");
        age.Values.Single(v => v.Value == 74).Sources.Single().Article.Outlet.Should().Be("CNN");
        age.Values.Single(v => v.Value == 74).Sources.Single().Excerpt.Should().Contain("74-year-old");
    }

    [Fact]
    public void For_ShouldSayATollMayHaveRisen_WhenTheLowerFiguresCameFirst()
    {
        var claims = CoverageClaims.For([
            Report("NDTV", "41 killed as trains collide", 0),
            Report("Al Jazeera", "Train crash: 41 dead", 1),
            Report("BBC", "Death toll rises to 64", 5),
            Report("DW", "64 killed in Spain train crash", 6)
        ]);

        var deaths = claims.Single(c => c.Measure == "deaths");
        deaths.Corroborated.Should().BeTrue();
        deaths.MayHaveRisen.Should().BeTrue();
        deaths.Values.Select(v => v.Value).Should().Equal(64, 41);
    }

    [Fact]
    public void For_ShouldIgnoreAFigureOnlyOneOutletGives_AndCallAgreementConsensus()
    {
        CoverageClaims.For([Report("A", "12 injured in blast", 0), Report("A", "12 injured", 1)]).Should().BeEmpty();
        CoverageClaims.For([Report("A", "12 injured in blast", 0), Report("B", "Blast leaves 12 injured", 1)])
            .Single().Consensus.Should().BeTrue();
    }

    private static WorldArticle Report(string outlet, string title, int hoursLater) =>
        new($"{outlet}-{hoursLater}-{title.GetHashCode()}", $"https://example.test/{Guid.NewGuid():N}", title, outlet, "English", "Somewhere",
            Now.AddHours(hoursLater), "test");
}
