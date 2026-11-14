using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Tests;

// B8: GDELT's quarter-hour feed as "surging worldwide", and local-first ordering on the story page.
public sealed class WorldNewsPulseTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static string Row(string fullName, int articles, string isRoot = "1", string geoType = "4")
    {
        var f = Enumerable.Repeat(string.Empty, 61).ToArray();
        f[25] = isRoot; f[33] = articles.ToString(System.Globalization.CultureInfo.InvariantCulture); f[51] = geoType; f[52] = fullName;
        return string.Join('\t', f);
    }

    [Fact]
    public void CountryArticles_ShouldSumRootEventsByTheCountryAtTheEndOfThePlace()
    {
        var tsv = string.Join('\n', Row("Kyiv, Kyyiv, Misto, Ukraine", 12), Row("Ukraine", 3), Row("Lviv, Ukraine", 5, isRoot: "0"),
            Row("Paris, Ile-de-France, France", 4), Row("Nowhere", 9, geoType: "0"));

        var counts = CoverageSurgeRules.CountryArticles(tsv);

        Assert.Equal(15, counts["Ukraine"]);
        Assert.Equal(4, counts["France"]);
        Assert.Equal(2, counts.Count);
    }

    private static KeyValuePair<DateTimeOffset, IReadOnlyDictionary<string, int>> File(int minutesAgo, params (string Country, int Articles)[] counts) =>
        new(Now.AddMinutes(-minutesAgo), counts.ToDictionary(c => c.Country, c => c.Articles));

    [Fact]
    public void Find_ShouldFlagACountryWritingThreeTimesItsUsual_AndIgnoreSteadyOnes()
    {
        var files = new List<KeyValuePair<DateTimeOffset, IReadOnlyDictionary<string, int>>>();
        for (var m = 60; m < 60 + 8 * 60; m += 15) files.Add(File(m, ("Sudan", 5), ("France", 30)));   // 20/h and 120/h usually
        for (var m = 0; m < 60; m += 15) files.Add(File(m, ("Sudan", 30), ("France", 31)));              // 120/h and 124/h now

        var surges = CoverageSurgeRules.Find(files, Now);

        var sudan = Assert.Single(surges);
        Assert.Equal("Sudan", sudan.Country);
        Assert.Equal(120, sudan.Recent);
        Assert.Equal(6, sudan.Times, 1);
    }

    [Fact]
    public void Find_ShouldSayNothing_UntilFourHoursOfBaselineAreInHand()
    {
        var files = new List<KeyValuePair<DateTimeOffset, IReadOnlyDictionary<string, int>>>
        {
            File(75, ("Sudan", 1)), File(0, ("Sudan", 500)),
        };

        Assert.Empty(CoverageSurgeRules.Find(files, Now));
    }

    [Fact]
    public void Find_ShouldIgnoreSmallNumbers_EvenWhenTheyJump()
    {
        var files = new List<KeyValuePair<DateTimeOffset, IReadOnlyDictionary<string, int>>>();
        for (var m = 60; m < 60 + 8 * 60; m += 15) files.Add(File(m, ("Tuvalu", 0)));
        files.Add(File(0, ("Tuvalu", 12)));

        Assert.Empty(CoverageSurgeRules.Find(files, Now));
    }

    [Theory]
    [InlineData("United States", "USA", true)]
    [InlineData("United Kingdom", "UK", true)]
    [InlineData("Türkiye", "Turkey", true)]
    [InlineData("France", "Germany", false)]
    [InlineData("International", "France", false)]
    [InlineData("France", null, false)]
    public void IsLocal_ShouldMatchCountriesAcrossCommonNames(string outlet, string? story, bool expected) =>
        Assert.Equal(expected, LocalFirst.IsLocal(outlet, story));

    [Fact]
    public void Order_ShouldPutLocalOutletsFirst_EarliestFirst_AndKeepTheRestInOrder()
    {
        var at = Now;
        var outlets = new[]
        {
            ("BBC", "United Kingdom", at), ("Le Monde", "France", at.AddMinutes(30)), ("CNN", "United States", at.AddMinutes(5)),
            ("France 24", "France", at.AddMinutes(10)), ("DW", "Germany", at.AddMinutes(1)),
        };

        var ordered = LocalFirst.Order(outlets, o => o.Item2, o => o.Item3, "France").Select(o => o.Item1).ToList();

        Assert.Equal(["France 24", "Le Monde", "BBC", "CNN", "DW"], ordered);
        Assert.Equal(outlets.Select(o => o.Item1), LocalFirst.Order(outlets, o => o.Item2, o => o.Item3, null).Select(o => o.Item1));
    }
}
