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

    [Fact]
    public void PayloadAdapter_ShouldRetainEveryMappedPayloadWithoutRewritingIt()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"privateNote":"only-this-user","pins":[{"lat":42.1,"lon":-71.2}]}""");
        var viewport = new NativeOverwatchViewport(
            "2026-11-10",
            DateTimeOffset.UtcNow,
            new NativeOverwatchViewportRequest(new NativeOverwatchBounds(45, 40, -70, -75), new NativeOverwatchControls(WorldNews: true)),
            [new NativeOverwatchCommand(NativeOverwatchOperations.SetMapNotes, document.RootElement)]);
        var adapter = new NativeOverwatchPayloadAdapter();

        adapter.Apply(viewport, "2026-11-10");

        adapter.Request!.Controls.WorldNews.Should().BeTrue();
        adapter.TryGetPayload(NativeOverwatchOperations.SetMapNotes, out var payload).Should().BeTrue();
        payload.GetProperty("privateNote").GetString().Should().Be("only-this-user");
        payload.GetProperty("pins")[0].GetProperty("lat").GetDouble().Should().Be(42.1);
    }

    [Fact]
    public void PayloadAdapter_ShouldRejectUnknownOperationsAndContractVersions()
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        var request = new NativeOverwatchViewportRequest(new NativeOverwatchBounds(45, 40, -70, -75), new NativeOverwatchControls());
        var adapter = new NativeOverwatchPayloadAdapter();

        Action wrongVersion = () => adapter.Apply(new NativeOverwatchViewport("other", DateTimeOffset.UtcNow, request, []), "2026-11-10");
        Action unknownOperation = () => adapter.Apply(new NativeOverwatchViewport(
            "2026-11-10", DateTimeOffset.UtcNow, request, [new NativeOverwatchCommand("setUntrustedLayer", document.RootElement)]), "2026-11-10");

        wrongVersion.Should().Throw<InvalidOperationException>();
        unknownOperation.Should().Throw<InvalidOperationException>();
    }
}
