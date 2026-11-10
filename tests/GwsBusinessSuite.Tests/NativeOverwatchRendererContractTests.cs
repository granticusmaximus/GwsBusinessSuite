using FluentAssertions;
using GwsBusinessSuite.Application.Overwatch;

namespace GwsBusinessSuite.Tests;

public sealed class NativeOverwatchRendererContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void Contract_ShouldCoverEveryNativeLayerAndInteractionPayload()
    {
        var operations = NativeOverwatchOperations.All;

        operations.Should().OnlyHaveUniqueItems();
        operations.Should().Contain(
            NativeOverwatchOperations.SetCameraPins,
            NativeOverwatchOperations.SetMapNotes,
            NativeOverwatchOperations.SetWorldNews,
            NativeOverwatchOperations.SetNewsShading,
            NativeOverwatchOperations.SetWeatherSnapshot,
            NativeOverwatchOperations.SetRadarTimeline,
            NativeOverwatchOperations.SetRoadTraffic,
            NativeOverwatchOperations.SetAircraft,
            NativeOverwatchOperations.SetAirQuality,
            NativeOverwatchOperations.SetPowerOutages,
            NativeOverwatchOperations.SetInternetOutages,
            NativeOverwatchOperations.SetConvergence,
            NativeOverwatchOperations.SetAirportStatus,
            NativeOverwatchOperations.SetTravelAdvice,
            NativeOverwatchOperations.SetFireHotspots,
            NativeOverwatchOperations.SetConflict,
            NativeOverwatchOperations.SetHumanitarian,
            NativeOverwatchOperations.SetSatellites,
            NativeOverwatchOperations.SetShips,
            NativeOverwatchOperations.SetHazards,
            NativeOverwatchOperations.SetRoute,
            NativeOverwatchOperations.SetTimeMachine);
    }

    [Theory]
    [InlineData(90, -90, 180, -180, true)]
    [InlineData(91, -90, 180, -180, false)]
    [InlineData(10, 11, 0, 0, false)]
    public void Bounds_ShouldRejectInvalidViewport(double north, double south, double east, double west, bool valid)
    {
        new NativeOverwatchBounds(north, south, east, west).IsValid.Should().Be(valid);
    }

    [Fact]
    public void Contract_ShouldStayAlignedWithTheWebInteropCommands()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot,
            "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor"));
        var commands = System.Text.RegularExpressions.Regex.Matches(source, @"tacticalGlobe\.(set[A-Z]\w*|clear[A-Z]\w*)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var command in commands)
        {
            NativeOverwatchOperations.All.Should().Contain(command,
                "the native renderer must receive every set/clear payload that the web renderer receives");
        }
    }
}
