using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// B6: every rules change runs the test set; a case that no longer gets its right label fails here.
public sealed class WorldNewsGoldSetTests
{
    public static TheoryData<string> CaseNames() => new(GoldSet.Cases.Select(gold => gold.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Case_ShouldGetItsKnownLabel(string name)
    {
        var result = GoldSet.Run(GoldSet.Cases.Single(gold => gold.Name == name));

        Assert.True(result.Passed,
            $"{name}: expected {result.Case.Expected} (alert {result.Case.ExpectedAlert}, independent {result.Case.ExpectedIndependent?.ToString() ?? "any"}), "
            + $"got {result.Got.Level} (alert {result.Alerted}, independent {result.Independent}): {result.Got.Reason}");
    }

    [Fact]
    public void Set_ShouldCoverEveryLabel_AndIncludeRedTeamCases()
    {
        var levels = GoldSet.Cases.Select(gold => gold.Expected).ToHashSet();
        Assert.Equal(Enum.GetValues<StoryConfidenceLevel>().Length, levels.Count);
        Assert.True(GoldSet.Cases.Count(gold => gold.RedTeam) >= 5);
        Assert.Equal(GoldSet.Cases.Count, GoldSet.Cases.Select(gold => gold.Name).Distinct().Count());
    }

    [Fact]
    public void Run_ShouldReportAFailure_WhenAnExpectationIsWrong()
    {
        var wrong = GoldSet.Cases[0] with { Expected = StoryConfidenceLevel.Confirmed };

        Assert.False(GoldSet.Run(wrong).Passed);
    }

    [Theory]
    [InlineData("Factory blast kills 31", 31)]
    [InlineData("Gunman killed 12 at a market", 12)]
    [InlineData("Storm kills 31-year-old hiker", null)]
    public void Deaths_ShouldBeRead_FromKillsAndKilled(string headline, int? expected)
    {
        var deaths = CoverageClaims.Extract(headline, null).FirstOrDefault(figure => figure.Measure == CoverageClaims.Deaths);

        Assert.Equal(expected, deaths is null ? null : (int)deaths.Value);
    }

    [Fact]
    public void RulesChange_ShouldBeInTheChangelog()
    {
        Assert.Equal(StoryConfidenceRules.RulesVersion, TrustMethodology.Changelog[0].Version);
        Assert.Equal("2026-11-13.3", StoryConfidenceRules.RulesVersion);
    }
}
