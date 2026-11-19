using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Tests;

// 19-11-26: camera availability (plan phases 3-4). What a camera can show, whether it's working,
// and World News listing working cameras first.
public sealed class CameraAvailabilityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 14, 5, 0, TimeSpan.Zero);

    private static CameraFeed Still(string id, double lat = 47.6, double lon = -122.3) =>
        new(id, id, lat, lon, $"https://cams.example/{id}.jpg", CameraStreamKind.Snapshot, "WSDOT", "https://wsdot.example");

    [Fact]
    public void StillImage_ShouldFollowItsHealthChecks()
    {
        var camera = Still("a");

        CameraAvailability.For(camera, null).State.Should().Be(CameraAvailabilityState.Unconfirmed);

        var live = CameraAvailability.For(camera, new("a", CameraHealthStatus.Live, "The image changed.", T0));
        live.State.Should().Be(CameraAvailabilityState.AvailableNow);
        live.LastConfirmed.Should().Be(T0);
        live.ModeLabel.Should().Be("Still image");
        live.CanCapture.Should().BeTrue();

        foreach (var broken in new[] { CameraHealthStatus.Frozen, CameraHealthStatus.Stale, CameraHealthStatus.Offline })
        {
            var availability = CameraAvailability.For(camera, new("a", broken, "Same image for 45 minutes.", T0));
            availability.State.Should().Be(CameraAvailabilityState.Unavailable, broken.ToString());
            availability.LastConfirmed.Should().BeNull("a broken camera wasn't seen working at that check");
            availability.CanCapture.Should().BeFalse();
        }
    }

    [Fact]
    public void LiveVideo_ShouldBeUnconfirmed_AndNeverCaptured()
    {
        var hls = Still("v") with { StreamKind = CameraStreamKind.Hls, StreamUrl = "https://cams.example/v.m3u8" };

        var availability = CameraAvailability.For(hls, new("v", CameraHealthStatus.Live, "x", T0));

        availability.Mode.Should().Be(CameraViewingMode.LiveVideo);
        availability.State.Should().Be(CameraAvailabilityState.Unconfirmed);
        availability.CanCapture.Should().BeFalse("Incident Capture stores still frames only");
    }

    [Fact]
    public void WorldNewsNearbyCameras_ShouldListWorkingOnesFirst_ThenNearest_WithinTheLimit()
    {
        // Story at (47.6, -122.3); about 0.07 miles per 0.001 degree of latitude.
        var cameras = new[]
        {
            Still("broken-nearest", 47.6001),
            Still("unchecked-near", 47.602),
            Still("working-far", 47.65),
            Still("working-near", 47.61),
            Still("too-far", 48.5),
        };
        var health = new Dictionary<string, CameraHealthReport>
        {
            ["broken-nearest"] = new("broken-nearest", CameraHealthStatus.Offline, "The last 3 checks failed.", T0),
            ["working-far"] = new("working-far", CameraHealthStatus.Live, "Changed.", T0),
            ["working-near"] = new("working-near", CameraHealthStatus.Live, "Changed.", T0),
            ["too-far"] = new("too-far", CameraHealthStatus.Live, "Changed.", T0),
        };

        var nearby = WorldNewsContextService.NearbyCameras(47.6, -122.3, cameras, id => health.GetValueOrDefault(id));

        nearby.Select(c => c.Camera.Id).Should().Equal("working-near", "working-far", "unchecked-near", "broken-nearest");
        nearby.Should().OnlyContain(c => c.Miles <= WorldNewsContextService.NearbyMiles);
        nearby[0].Availability!.StateLabel.Should().Be("Available now");
    }

    [Fact]
    public void WorldNewsPanel_ShouldLabelCamerasAsVisualContext_NotEvidence()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var js = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));

        js.Should().Contain("VISUAL CONTEXT · NEARBY CAMERAS")
            .And.Contain("They don\\'t verify the report.")
            .And.Contain("SEEN WORKING ");
    }
}
