using System.Text.RegularExpressions;
using FluentAssertions;

namespace GwsBusinessSuite.Tests;

// 27-11-26, found by the Overwatch visual tour: Hazards was switched off while its sources were still
// answering, and the late answer drew quake pins on the globe with the switch off. Every layer that
// fetches and then draws checks its switch again once the answer arrives.
public sealed class OverwatchLateLayerAnswerTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Theory]
    [InlineData("PushHazardsAsync", "_hazardsOn")]
    [InlineData("PushSignsAsync", "_signsOn")]
    [InlineData("PushRoadWeatherAsync", "_roadWeatherOn")]
    [InlineData("PushInternetAsync", "_internetOn")]
    [InlineData("PushConvergenceAsync", "_convergenceOn")]
    [InlineData("PushAirportsAsync", "_airportsOn")]
    [InlineData("PushWmoAsync", "_wmoOn")]
    [InlineData("PushTravelAsync", "_travelOn")]
    [InlineData("PushFiresAsync", "_firesOn")]
    [InlineData("PushOutagesAsync", "_outagesOn")]
    [InlineData("PushShipsAsync", "_shipsOn")]
    [InlineData("PushConflictAsync", "_conflictOn")]
    [InlineData("PushHumanitarianAsync", "_humanitarianOn")]
    [InlineData("PushSatellitesAsync", "_satellitesOn")]
    [InlineData("PushAircraftAsync", "_aircraftOn")]
    [InlineData("PushAirQualityAsync", "_airQualityOn")]
    public void ALateAnswer_ShouldNotDrawALayerThatWasSwitchedOff(string method, string flag)
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor"));
        var start = page.IndexOf($"private async Task {method}(", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"{method} exists");
        var draw = page.IndexOf("JS.InvokeVoidAsync(\"tacticalGlobe.", start, StringComparison.Ordinal);
        var beforeDraw = page[start..draw];
        Regex.IsMatch(beforeDraw, @"= await [^\n]+;\s*\n(\s*//[^\n]*\n)?\s*if \(!" + Regex.Escape(flag) + @"\) return;")
            .Should().BeTrue($"{method} checks {flag} again after its data arrives, before drawing");
    }
}
