using FluentAssertions;
using GwsBusinessSuite.Application.RouteWatch;

namespace GwsBusinessSuite.Tests;

public sealed class RouteDirectionsTests
{
    // A real OSRM response (router.project-osrm.org, 2026-10-10), Macon to downtown Atlanta,
    // trimmed to the fields RouteDirections reads.
    private const string MaconToAtlanta = """
{"code": "Ok", "routes": [{"legs": [{"steps": [{"name": "Mulberry Street", "distance": 22.6, "maneuver": {"bearing_after": 334, "location": [-83.632394, 32.840707], "type": "depart"}}, {"name": "Spring Street", "distance": 849.9, "maneuver": {"bearing_after": 30, "location": [-83.632403, 32.840903], "modifier": "slight right", "type": "new name"}}, {"ref": "US 129; GA 11; GA 49", "name": "North Avenue", "distance": 159.8, "maneuver": {"bearing_after": 35, "location": [-83.62711, 32.847097], "modifier": "straight", "type": "new name"}}, {"name": "", "distance": 484.6, "maneuver": {"bearing_after": 288, "location": [-83.626081, 32.848239], "modifier": "left", "type": "off ramp"}}, {"ref": "I 16; GA 540", "name": "", "distance": 780.2, "maneuver": {"bearing_after": 315, "location": [-83.630044, 32.850954], "modifier": "slight left", "type": "merge"}}, {"destinations": "I 75 North: Atlanta", "name": "", "distance": 837, "maneuver": {"bearing_after": 310, "location": [-83.63614, 32.855731], "modifier": "slight right", "type": "fork"}}, {"ref": "I 75", "name": "", "distance": 99869.3, "maneuver": {"bearing_after": 298, "location": [-83.643426, 32.860089], "modifier": "slight left", "type": "merge"}}, {"destinations": "I 75 North: Atlanta, Birmingham", "ref": "I 75", "name": "", "distance": 29338.2, "maneuver": {"bearing_after": 323, "location": [-84.264793, 33.53927], "modifier": "slight left", "type": "fork"}}, {"exits": "246", "destinations": "Fulton Street, Central Avenue, Georgia State University", "name": "", "distance": 332.7, "maneuver": {"bearing_after": 9, "location": [-84.391954, 33.73275], "modifier": "slight right", "type": "off ramp"}}, {"destinations": "Fulton Street, Zoo Atlanta", "name": "", "distance": 694.4, "maneuver": {"bearing_after": 16, "location": [-84.391443, 33.735718], "modifier": "slight right", "type": "fork"}}, {"name": "Fulton Street Southwest", "distance": 312.1, "maneuver": {"bearing_after": 88, "location": [-84.391157, 33.741918], "modifier": "right", "type": "turn"}}, {"name": "Capitol Avenue Southeast", "distance": 775.4, "maneuver": {"bearing_after": 0, "location": [-84.38779, 33.741889], "modifier": "left", "type": "turn"}}, {"name": "Capitol Avenue Southeast", "distance": 0, "maneuver": {"bearing_after": 0, "location": [-84.387509, 33.748855], "modifier": "left", "type": "arrive"}}]}]}]}
""";

    [Fact]
    public void ParseSteps_ShouldReadARealRouteLikeAMapsApp()
    {
        var steps = RouteDirections.ParseSteps(MaconToAtlanta, ["Macon, GA", "Atlanta, GA"]);

        steps.Select(step => step.Instruction).Should().Equal(
            "Head northwest on Mulberry Street",
            "Continue onto Spring Street",
            "Continue onto North Avenue (US 129 / GA 11 / GA 49)",
            "Take the exit on the left",
            "Merge left onto I 16 / GA 540",
            "Keep right at the fork toward Atlanta",
            "Merge left onto I 75",
            "Keep left at the fork toward Atlanta",
            "Take exit 246 toward Fulton Street",
            "Keep right at the fork toward Fulton Street",
            "Turn right onto Fulton Street Southwest",
            "Turn left onto Capitol Avenue Southeast",
            "Arrive at Atlanta, GA - it's on the left");
        steps[^1].Kind.Should().Be(RouteStepKinds.Arrive);
        steps[^1].DistanceMiles.Should().Be(0);
        steps[6].DistanceMiles.Should().BeApproximately(62.06, 0.05, "the I 75 stretch is about 99.9 km");
        steps.Select(step => step.StartsAtMile).Should().BeInAscendingOrder();
    }

    [Fact]
    public void ParseSteps_ShouldMarkIntermediateStops_AndSkipTheRepeatedDepart()
    {
        const string twoLegs = """
            {"code":"Ok","routes":[{"legs":[
              {"steps":[{"name":"A St","distance":1000,"maneuver":{"type":"depart","bearing_after":90,"location":[-84,33]}},
                        {"name":"A St","distance":0,"maneuver":{"type":"arrive","modifier":"right","location":[-84.01,33]}}]},
              {"steps":[{"name":"A St","distance":2000,"maneuver":{"type":"depart","bearing_after":90,"location":[-84.01,33]}},
                        {"name":"B St","distance":0,"maneuver":{"type":"arrive","location":[-84.03,33]}}]}]}]}
            """;

        var steps = RouteDirections.ParseSteps(twoLegs, ["Start", "Gas station", "Home"]);

        steps.Select(step => step.Instruction).Should().Equal(
            "Head east on A St", "Arrive at stop: Gas station - it's on the right", "Arrive at Home");
        steps[2].StartsAtMile.Should().BeApproximately(3000 / 1609.344, 0.001);
    }

    [Theory]
    [InlineData("turn", "uturn", "Main St", null, "Make a U-turn onto Main St")]
    [InlineData("turn", "sharp left", "Main St", null, "Turn sharply left onto Main St")]
    [InlineData("turn", "slight right", "", null, "Turn slightly right")]
    [InlineData("end of road", "right", "Elm St", null, "Turn right at the end of the road onto Elm St")]
    [InlineData("roundabout", "right", "Oak Rd", 2, "At the roundabout, take the 2nd exit onto Oak Rd")]
    [InlineData("rotary", "straight", "", 3, "At the roundabout, take the 3rd exit")]
    [InlineData("continue", "straight", "I 85", null, "Continue on I 85")]
    [InlineData("on ramp", "right", "I 285", null, "Take the ramp onto I 285")]
    public void Describe_ShouldWordEachManeuver(string type, string modifier, string road, int? exit, string expected)
    {
        RouteDirections.Describe(type, modifier, road, "", "", exit, null, null, true).Instruction.Should().Be(expected);
    }

    [Theory]
    [InlineData(0.004, "20 ft")]
    [InlineData(0.05, "260 ft")]
    [InlineData(0.42, "0.4 mi")]
    [InlineData(62.06, "62 mi")]
    public void FormatDistance_ShouldUseFeetForShortDistances(double miles, string expected)
    {
        RouteDirections.FormatDistance(miles).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(11, "11th")]
    [InlineData(22, "22nd")]
    public void Ordinal_ShouldHandleTeens(int n, string expected) => RouteDirections.Ordinal(n).Should().Be(expected);
}

public sealed class RouteDirectionsTextTests
{
    [Fact]
    public void Format_ShouldListNumberedStepsAndWarnings_InDrivingOrder()
    {
        var incident = new GwsBusinessSuite.Application.TrafficIncidents.TrafficIncident(
            "i1", "I 75", "Right lane blocked", "crash", "major", 33.5, -84.2, "GDOT", "https://511ga.org", Category: "crash");
        var route = new RouteWatchResult("Macon, GA", "Atlanta, GA", 84.6, 82, [], [],
            [new RouteIncident(incident, 41.2, 0.1)], [],
            Steps:
            [
                new RouteStep(0, RouteStepKinds.Depart, "Head northwest on Mulberry Street", 0.01, 0, 32.84, -83.63),
                new RouteStep(0, RouteStepKinds.Merge, "Merge left onto I 75", 62.06, 1.2, 32.9, -83.7),
                new RouteStep(0, RouteStepKinds.Arrive, "Arrive at Atlanta, GA", 0, 84.6, 33.75, -84.39)
            ]);

        var text = RouteDirectionsText.Format(route, new DateTimeOffset(2026, 10, 10, 21, 5, 0, TimeSpan.FromHours(-4)));

        text.Should().StartWith("Directions: Macon, GA to Atlanta, GA\n85 mi, about 1 h 22 min · 10-10-26 21:05");
        text.Should().Contain("1. Head northwest on Mulberry Street (50 ft)")
            .And.Contain("2. Merge left onto I 75 (62 mi)")
            .And.Contain("3. Arrive at Atlanta, GA")
            .And.Contain("Along the way:\n- Mile 41: ");
        text.Should().NotContain("Arrive at Atlanta, GA (");
    }

    [Fact]
    public void Links_ShouldReopenTheRoute_AndEscapeEachStop()
    {
        string[] stops = ["Macon, GA", " ", "Atlanta & Co"];

        RouteDirectionsText.OverwatchLink(stops).Should().Be("/admin/osint?route=Macon%2C%20GA&route=Atlanta%20%26%20Co");
        RouteDirectionsText.PrintLink(stops).Should().Be("/admin/osint/directions?stop=Macon%2C%20GA&stop=Atlanta%20%26%20Co");
    }

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(82, "1 h 22 min")]
    [InlineData(600, "10 h 00 min")]
    public void FormatDuration_ShouldShowHoursAndMinutes(double minutes, string expected) =>
        RouteDirectionsText.FormatDuration(minutes).Should().Be(expected);
}
