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

    // Phase 3: pins show what a camera offers by shape (not colour alone), the window says it in
    // words, and LIVE VIDEO ONLY filters on the server so the Mac map gets it too.
    [Fact]
    public void OverwatchPins_ShouldShowWhatACameraOffers_AndFilterToLiveVideo()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var js = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/wwwroot/js/tactical-globe.js"));
        var page = File.ReadAllText(Path.Combine(root, "src/GwsBusinessSuite.Web/Components/Pages/BusinessSuite/OverwatchGrid.razor"));

        js.Should().Contain("function cameraPinImage(pin, selected, alert)")
            .And.Contain("image: cameraPinImage(pin, entry.selectedCameras.has(pin.id))",
                "new camera pins take their shape from the camera")
            .And.Contain("entity.billboard.image = cameraPinImage(entity._tacticalGlobeCamera, selected, entity._tacticalGlobeAlertSeverity)",
                "selecting a pin keeps its shape")
            .And.Contain("tg-stream-panel-availability");
        page.Should().Contain("mode = availability.Mode.ToString()")
            .And.Contain("state = availability.State.ToString()")
            .And.Contain("include: CameraFilter()")
            .And.Contain("LIVE VIDEO ONLY [");
    }

    // The Overwatch "cameras in view" list: working first, nearest, or most recently seen working.
    [Fact]
    public void InViewList_ShouldSortByWorking_Nearest_OrLastSeenWorking()
    {
        var rows = new (string Id, CameraAvailability Availability, double Miles)[]
        {
            ("broken-near", new(CameraViewingMode.StillImage, CameraAvailabilityState.Unavailable, "x", null), 1),
            ("working-old", new(CameraViewingMode.StillImage, CameraAvailabilityState.AvailableNow, "x", T0.AddHours(-1)), 5),
            ("working-new", new(CameraViewingMode.StillImage, CameraAvailabilityState.AvailableNow, "x", T0), 9),
            ("unchecked", new(CameraViewingMode.LiveVideo, CameraAvailabilityState.Unconfirmed, "x", null), 2),
        };
        string[] Order(string sort, int take = 10) =>
            CameraAvailability.Sort(rows, sort, r => r.Availability, r => r.Miles, take).Select(r => r.Id).ToArray();

        Order(CameraAvailability.SortWorking).Should().Equal("working-old", "working-new", "unchecked", "broken-near");
        Order(CameraAvailability.SortNearest).Should().Equal("broken-near", "unchecked", "working-old", "working-new");
        Order(CameraAvailability.SortRecent).Should().Equal("working-new", "working-old", "broken-near", "unchecked");
        Order(CameraAvailability.SortWorking, take: 2).Should().HaveCount(2);
    }
}
