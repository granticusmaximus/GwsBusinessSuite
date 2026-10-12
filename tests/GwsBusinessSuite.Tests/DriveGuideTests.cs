using FluentAssertions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Application.TrafficIncidents;

namespace GwsBusinessSuite.Tests;

public sealed class DriveGuideTests
{
    // A straight drive due north from 33.70 to 33.80 along -84.39: 6.9 miles.
    private const double Lon = -84.39;
    private static double LatAt(double mile) => 33.70 + mile / 69.0;

    private static TrafficIncident Incident(string id, string category, double mile, double lon = Lon) =>
        new(id, "I 75", "Lanes blocked", category, "major", LatAt(mile), lon, "GDOT", "https://511ga.org", Category: category, SeverityLevel: IncidentSeverityLevels.Major);

    private static RouteWatchResult Route(IReadOnlyList<RouteIncident>? incidents = null)
    {
        var path = Enumerable.Range(0, 70).Select(i => new RoutePoint(LatAt(i * 0.1), Lon)).ToList();
        var camera = new CameraFeed("cam-4", "I-75 at mile 4", LatAt(4), Lon, "https://cams.example/4.jpg", CameraStreamKind.Snapshot, "GDOT", "https://511ga.org");
        return new RouteWatchResult("Macon, GA", "Atlanta, GA", 6.9, 10, path,
            [new RouteCamera(camera, 4, 0)], incidents ?? [], [],
            Legs: [new RouteLeg("Macon, GA", "Forsyth, GA", 3.45, 5, 0), new RouteLeg("Forsyth, GA", "Atlanta, GA", 3.45, 5, 3.45)],
            Stops: [new RouteStop("Macon, GA", LatAt(0), Lon), new RouteStop("Forsyth, GA", LatAt(3.45), Lon), new RouteStop("Atlanta, GA", LatAt(6.9), Lon)],
            Steps: [new RouteStep(0, RouteStepKinds.Depart, "Head north on I 75", 3, 0, LatAt(0), Lon),
                new RouteStep(0, RouteStepKinds.Right, "Turn right onto Main St", 3.9, 3, LatAt(3), Lon),
                new RouteStep(1, RouteStepKinds.Arrive, "Arrive at Atlanta, GA", 0, 6.9, LatAt(6.9), Lon)]);
    }

    [Fact]
    public void Update_ShouldTrackProgress_AndSpeakEachPromptOnce()
    {
        var guide = new DriveGuide(Route([new RouteIncident(Incident("c1", IncidentCategories.Crash, 6), 6, 0)]));

        var start = guide.Update(new DriveFix(LatAt(0), Lon));
        start.State.NextStep!.Instruction.Should().Be("Turn right onto Main St");
        start.State.MilesToNextStep.Should().BeApproximately(3, 0.05);
        start.State.MinutesRemaining.Should().BeApproximately(10, 0.2);
        start.Announcements.Should().BeEmpty("the turn is 3 miles off and the crash 6");

        guide.Update(new DriveFix(LatAt(2.6), Lon)).Announcements.Should().Equal(
            "In half a mile, turn right onto Main St.", "Crash reported 3 miles ahead on I 75.");
        guide.Update(new DriveFix(LatAt(2.62), Lon)).Announcements.Should().BeEmpty("each prompt is said once");
        guide.Update(new DriveFix(LatAt(2.95), Lon)).Announcements.Should().Equal("Turn right onto Main St.");

        var past = guide.Update(new DriveFix(LatAt(3.5), Lon));
        past.State.NextStep!.Kind.Should().Be(RouteStepKinds.Arrive);
        past.State.CamerasAhead.Should().ContainSingle().Which.Camera.Id.Should().Be("cam-4");

        var arrived = guide.Update(new DriveFix(LatAt(6.88), Lon));
        arrived.State.Arrived.Should().BeTrue();
        arrived.Announcements.Should().Equal("You have arrived at Atlanta, GA.");
    }

    [Fact]
    public void Update_ShouldCallOffRouteAfterThreeFixes_IgnoreInaccurateOnes_AndNotWalkBackwards()
    {
        var guide = new DriveGuide(Route());
        guide.Update(new DriveFix(LatAt(2), Lon));

        guide.Update(new DriveFix(LatAt(2.1), Lon, AccuracyMeters: 900)).State.MilesAlong.Should().BeApproximately(2, 0.01, "a 900 m fix is ignored");
        guide.Update(new DriveFix(LatAt(1.98), Lon)).State.MilesAlong.Should().BeApproximately(2, 0.01, "jitter doesn't move the driver back");

        guide.Update(new DriveFix(LatAt(2.2), -84.30)).State.OffRoute.Should().BeFalse();
        guide.Update(new DriveFix(LatAt(2.3), -84.30)).State.OffRoute.Should().BeFalse();
        var off = guide.Update(new DriveFix(LatAt(2.4), -84.30));
        off.State.OffRoute.Should().BeTrue();
        off.Announcements.Should().ContainSingle().Which.Should().StartWith("You're off the route");

        guide.Update(new DriveFix(LatAt(2.5), Lon)).Announcements.Should().Contain("Back on the route.");
    }

    [Fact]
    public void AddFreshIncidents_ShouldReportEachNewClosureAheadOnTheRoad_Once()
    {
        var guide = new DriveGuide(Route([new RouteIncident(Incident("known", IncidentCategories.Closure, 5), 5, 0)]));
        guide.Update(new DriveFix(LatAt(3), Lon));
        TrafficIncident[] fresh =
        [
            Incident("known", IncidentCategories.Closure, 5), Incident("new", IncidentCategories.Closure, 5.5),
            Incident("behind", IncidentCategories.Closure, 1), Incident("off-road", IncidentCategories.Closure, 5.5, -84.30),
            Incident("roadwork", IncidentCategories.Roadwork, 5.5)
        ];

        guide.AddFreshIncidents(fresh).Should().ContainSingle().Which.Incident.Id.Should().Be("new");
        guide.AddFreshIncidents(fresh).Should().BeEmpty();
        guide.Update(new DriveFix(LatAt(3.1), Lon)).State.IncidentsAhead.Select(i => i.Incident.Id).Should().Equal("known", "new");
    }

    [Fact]
    public void StopsAhead_ShouldBeTheNextStopOnwards()
    {
        var guide = new DriveGuide(Route());
        guide.Update(new DriveFix(LatAt(1), Lon));
        guide.StopsAhead().Select(s => s.Label).Should().Equal("Forsyth, GA", "Atlanta, GA");
        guide.Update(new DriveFix(LatAt(5), Lon));
        guide.StopsAhead().Select(s => s.Label).Should().Equal("Atlanta, GA");
        guide.RemainingPath().First().Latitude.Should().BeApproximately(LatAt(4.8), 0.002);
    }

    [Fact]
    public void PickDetourVia_ShouldChooseTheAlternativeThatAvoidsTheClosure()
    {
        IReadOnlyList<RoutePoint> main = [new(33.70, Lon), new(33.75, Lon), new(33.80, Lon)];
        IReadOnlyList<RoutePoint> east = [new(33.70, Lon), new(33.75, -84.33), new(33.80, Lon)];

        RouteWatchService.PickDetourVia([main, east], new RoutePoint(33.75, Lon)).Should().Be(new RoutePoint(33.75, -84.33));
        RouteWatchService.PickDetourVia([main, east], new RoutePoint(33.70, Lon)).Should().BeNull("both start at the closure");
        RouteWatchService.PickDetourVia([main], new RoutePoint(33.75, Lon)).Should().BeNull();
    }

    [Theory]
    [InlineData(0.05, "a few hundred feet")]
    [InlineData(0.45, "half a mile")]
    [InlineData(3.4, "3 miles")]
    public void MilesText_ShouldReadNaturally(double miles, string expected) => DriveGuide.MilesText(miles).Should().Be(expected);
}
