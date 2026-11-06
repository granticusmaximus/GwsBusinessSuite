using FluentAssertions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Web.Components;

namespace GwsBusinessSuite.Tests;

// T5 (06-11-26): sparklines in KPI tiles and metric cards, and the one-time compact offer.
public sealed class SparklineTests
{
    [Fact]
    public void Points_ShouldSpreadDaysAcross_AndScaleToTheLargestOfEitherPeriod()
    {
        SparklineGeometry.Points([0, 5, 10], 10).Should().Be("0,22.5 50,12 100,1.5");
        SparklineGeometry.Points([0, 5, 10], 20).Should().Be("0,22.5 50,17.25 100,12", "a bigger previous period lowers this one");
        SparklineGeometry.Points([0, 0], 0).Should().Be("0,22.5 100,22.5", "all zeros sits on the floor");
        SparklineGeometry.Points([], 1).Should().BeEmpty();
        SparklineGeometry.Max([1, 4], [9, 2]).Should().Be(9);
        SparklineGeometry.Max([1, 4], null).Should().Be(4);
    }

    [Fact]
    public void Describe_ShouldSayTheTotalAndTheLastDay()
    {
        SparklineGeometry.Describe("Clicks, last 14 days", [1, 2, 3], v => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be("Clicks, last 14 days: 6 in all, 3 on the last day");
        SparklineGeometry.Describe("Visitors", [1, 2], v => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), [5, 5])
            .Should().Be("Visitors: 3 in all, 2 on the last day; 10 in the period before");
    }

    [Fact]
    public void CompactOffer_ShouldDefaultToUnanswered_AndOnlyTakeItsTwoValues()
    {
        UserPreferenceRules.DefaultFor(UserPreferenceKeys.DensityOffer).Should().Be(DensityOffers.Unanswered);
        UserPreferenceRules.IsAllowed(UserPreferenceKeys.DensityOffer, DensityOffers.Answered).Should().BeTrue();
        UserPreferenceRules.IsAllowed(UserPreferenceKeys.DensityOffer, "maybe").Should().BeFalse();
    }
}
