using FluentAssertions;
using GwsBusinessSuite.Application.LiveLayers;

namespace GwsBusinessSuite.Tests;

// Overwatch chokepoints (IMF PortWatch), 13-11-26.
public sealed class ChokepointServiceTests
{
    private static readonly IReadOnlyDictionary<string, (string Name, double Lat, double Lon)> Places =
        ChokepointService.ParsePlaces("""{"features":[{"attributes":{"portid":"chokepoint4","portname":"Bab el-Mandeb Strait","lat":12.79,"lon":43.35}}]}""");

    private static IEnumerable<ChokepointService.Day> Days(int normal, int lastWeek)
    {
        var end = new DateOnly(2026, 11, 8);
        for (var i = 0; i < 37; i++)
            yield return new ChokepointService.Day("chokepoint4", end.AddDays(-i), i < 7 ? lastWeek : normal, 5);
    }

    [Fact]
    public void ASharpFallInTransits_ShouldBeMajor_AndSaySo()
    {
        var pin = ChokepointService.Build(Places, Days(normal: 50, lastWeek: 20)).Single();

        pin.Kind.Should().Be("chokepoint");
        pin.Severity.Should().Be("major");
        pin.Detail.Should().Contain("Transits 08-11-26: 20").And.Contain("-60%");
        pin.Latitude.Should().Be(12.79);
    }

    [Theory]
    [InlineData(50, 38, "moderate")]
    [InlineData(50, 48, "minor")]
    [InlineData(50, 70, "minor")]
    public void Severity_ShouldFollowTheFallOnTheMonthBefore(int normal, int lastWeek, string expected) =>
        ChokepointService.Build(Places, Days(normal, lastWeek)).Single().Severity.Should().Be(expected);

    [Fact]
    public void ParseDays_ShouldReadRowsAndThePagingFlag()
    {
        var (days, more) = ChokepointService.ParseDays("""
            {"exceededTransferLimit":true,"features":[{"attributes":{"portid":"chokepoint1","date":"2026-11-08","n_total":43,"n_tanker":17}}]}
            """);
        more.Should().BeTrue();
        days.Should().ContainSingle().Which.Should().Be(new ChokepointService.Day("chokepoint1", new DateOnly(2026, 11, 8), 43, 17));
    }
}
