using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

// Overwatch's sun and shadow tool (13-11-26), checked against known positions.
public sealed class SunPositionTests
{
    [Fact]
    public void AtNoonOnTheEquinoxInLondon_TheSunShouldBeDueSouth_AtNinetyMinusTheLatitude()
    {
        var sun = SunPosition.At(51.4779, 0, new DateTimeOffset(2026, 3, 20, 12, 7, 0, TimeSpan.Zero));
        sun.ElevationDegrees.Should().BeApproximately(90 - 51.4779, 1.0);
        sun.AzimuthDegrees.Should().BeApproximately(180, 2.0);
        Math.Abs(((sun.ShadowBearingDegrees + 180) % 360) - 180).Should().BeLessThan(2.0, "a noon shadow in the north points north");
    }

    [Fact]
    public void AtTheJuneSolsticeOnTheTropicOfCancer_TheSunShouldBeOverhead()
    {
        var sun = SunPosition.At(23.44, 0, new DateTimeOffset(2026, 6, 21, 12, 2, 0, TimeSpan.Zero));
        sun.ElevationDegrees.Should().BeGreaterThan(89.0);
        sun.ShadowPerUnitHeight.Should().BeLessThan(0.02);
    }

    [Fact]
    public void AtMidnight_TheSunShouldBeDown_AndCastNoShadow()
    {
        var sun = SunPosition.At(33.749, -84.388, new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.FromHours(-5)));
        sun.ElevationDegrees.Should().BeNegative();
        sun.ShadowPerUnitHeight.Should().BeNull();
    }

    [Fact]
    public void AShadowPointingWest_ShouldMeanMorning()
    {
        var times = SunPosition.TimesForShadow(33.749, -84.388, new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.FromHours(-5)), 290, 5);
        times.Should().NotBeEmpty();
        times.Should().OnlyContain(sun => sun.At.Hour < 12);
    }

    [Theory]
    [InlineData(0, "N")]
    [InlineData(44, "NE")]
    [InlineData(181, "S")]
    [InlineData(350, "N")]
    public void Compass_ShouldNameTheDirection(double degrees, string expected) => SunPosition.Compass(degrees).Should().Be(expected);
}
