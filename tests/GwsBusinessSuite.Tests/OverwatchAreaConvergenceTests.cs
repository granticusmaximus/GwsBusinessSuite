using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// Watched areas: several kinds of signal together (13-11-26).
public sealed class OverwatchAreaConvergenceTests
{
    [Fact]
    public void ConvergenceItems_ShouldAlertForSquaresInsideTheArea_KeyedByTheirKinds()
    {
        var inside = new ConvergenceCell(33, -85, ["news", "earthquake", "power outage"], 80, "major", [], "Atlanta, United States");
        var outside = inside with { South = 40, West = -75 };
        var area = new BoundingBox(35, 32, -83, -86);

        var items = OverwatchAreaService.ConvergenceItems([inside, outside], area, "moderate").ToList();

        items.Should().ContainSingle();
        items[0].Text.Should().Be("3 kinds of signal together near Atlanta, United States: news, earthquake, power outage");
        items[0].Key.Should().Be("convergence:33:-85:earthquake,news,power outage");
        OverwatchAreaService.ConvergenceItems([inside with { Severity = "moderate" }], area, "major").Should().BeEmpty();
    }
}
