using System.Globalization;
using System.Text.Json;

namespace GwsBusinessSuite.Application.RouteWatch;

// One turn-by-turn instruction. Kind picks the arrow icon; DistanceMiles is how far you drive
// after this maneuver before the next one; StartsAtMile is where it happens along the trip.
public sealed record RouteStep(
    int Leg,
    string Kind,
    string Instruction,
    double DistanceMiles,
    double StartsAtMile,
    double Latitude,
    double Longitude);

public static class RouteStepKinds
{
    public const string Depart = "depart";
    public const string Straight = "straight";
    public const string Left = "left";
    public const string Right = "right";
    public const string SlightLeft = "slight-left";
    public const string SlightRight = "slight-right";
    public const string UTurn = "uturn";
    public const string Ramp = "ramp";
    public const string Exit = "exit";
    public const string Fork = "fork";
    public const string Merge = "merge";
    public const string Roundabout = "roundabout";
    public const string Arrive = "arrive";
}

// Turns OSRM's route steps (requested with steps=true) into plain-English, Google Maps-style
// directions. OSRM only returns the maneuver data (type, modifier, road name/number, exit,
// signposted destinations); the wording is built here, in the spirit of OSRM's own
// osrm-text-instructions.
public static class RouteDirections
{
    private const double MetersPerMile = 1609.344;

    // stopNames are the trip's stops in order, used for "Arrive at ..." lines.
    public static IReadOnlyList<RouteStep> ParseSteps(string osrmJson, IReadOnlyList<string> stopNames)
    {
        using var doc = JsonDocument.Parse(osrmJson);
        if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0) return [];
        if (!routes[0].TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array) return [];

        var steps = new List<RouteStep>();
        var legCount = legs.GetArrayLength();
        var miles = 0d;
        for (var legIndex = 0; legIndex < legCount; legIndex++)
        {
            if (!legs[legIndex].TryGetProperty("steps", out var legSteps)) continue;
            foreach (var step in legSteps.EnumerateArray())
            {
                var maneuver = step.GetProperty("maneuver");
                var type = Text(maneuver, "type");
                var stepMiles = (step.TryGetProperty("distance", out var d) ? d.GetDouble() : 0) / MetersPerMile;
                var location = maneuver.GetProperty("location");
                var isLastLeg = legIndex == legCount - 1;
                // An intermediate "arrive" is immediately followed by the next leg's "depart" at
                // the same spot; keep the arrive, drop that depart.
                if (type == "depart" && legIndex > 0) { miles += stepMiles; continue; }
                if (type is "notification" or "use lane" && stepMiles < 0.05) { miles += stepMiles; continue; }

                var (kind, instruction) = Describe(
                    type, Text(maneuver, "modifier"), RoadName(step), Text(step, "exits"), Text(step, "destinations"),
                    maneuver.TryGetProperty("exit", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null,
                    maneuver.TryGetProperty("bearing_after", out var bearing) && bearing.ValueKind == JsonValueKind.Number ? bearing.GetDouble() : null,
                    StopName(stopNames, legIndex + 1), isLastLeg);
                steps.Add(new RouteStep(legIndex, kind, instruction, type == "arrive" ? 0 : stepMiles, miles,
                    location[1].GetDouble(), location[0].GetDouble()));
                miles += stepMiles;
            }
        }
        return steps;
    }

    public static (string Kind, string Instruction) Describe(
        string type,
        string modifier,
        string road,
        string exits,
        string destinations,
        int? roundaboutExit,
        double? bearingAfter,
        string? stopName,
        bool isFinalStop)
    {
        var onto = road.Length > 0 ? $" onto {road}" : string.Empty;
        var toward = destinations.Length > 0 ? $" toward {FirstDestination(destinations)}" : string.Empty;
        var side = modifier.Contains("left", StringComparison.Ordinal) ? "left" : "right";
        switch (type)
        {
            case "depart":
                var heading = bearingAfter is { } b ? Compass(b) : null;
                return (RouteStepKinds.Depart,
                    $"Head {heading ?? "out"}{(road.Length > 0 ? $" on {road}" : string.Empty)}");
            case "arrive":
                var where = string.IsNullOrWhiteSpace(stopName) ? "your destination" : stopName;
                var sideText = modifier is "left" or "right" ? $" - it's on the {modifier}" : string.Empty;
                return (RouteStepKinds.Arrive, isFinalStop ? $"Arrive at {where}{sideText}" : $"Arrive at stop: {where}{sideText}");
            case "merge":
                return (RouteStepKinds.Merge, $"Merge {(modifier.Length > 0 ? side + " " : string.Empty)}{(road.Length > 0 ? $"onto {road}" : "onto the road")}".Replace("  ", " ").TrimEnd());
            case "on ramp":
                return (RouteStepKinds.Ramp, road.Length > 0 && toward.Length == 0
                    ? $"Take the ramp{onto}"
                    : $"Take the ramp on the {side}{(toward.Length > 0 ? toward : onto)}");
            case "off ramp":
                var exitText = exits.Length > 0 ? $"exit {exits.Split(';')[0]}" : $"the exit on the {side}";
                return (RouteStepKinds.Exit, $"Take {exitText}{(toward.Length > 0 ? toward : onto)}");
            case "fork":
                return (RouteStepKinds.Fork, $"Keep {side} at the fork{(toward.Length > 0 ? toward : onto)}");
            case "end of road":
                return (side == "left" ? RouteStepKinds.Left : RouteStepKinds.Right, $"Turn {side} at the end of the road{onto}");
            case "roundabout":
            case "rotary":
                var nth = roundaboutExit is { } n and > 0 ? $"take the {Ordinal(n)} exit" : "take the exit";
                return (RouteStepKinds.Roundabout, $"At the roundabout, {nth}{onto}");
            case "new name":
                return (RouteStepKinds.Straight, road.Length > 0 ? $"Continue onto {road}" : "Continue straight");
            case "continue":
                if (modifier is "straight" or "") return (RouteStepKinds.Straight, road.Length > 0 ? $"Continue on {road}" : "Continue straight");
                if (modifier == "uturn") return (RouteStepKinds.UTurn, $"Make a U-turn{onto}");
                return (KindFor(modifier), $"Keep {side}{onto}");
            default: // turn, roundabout turn, exit roundabout, exit rotary, notification, use lane
                return modifier switch
                {
                    "uturn" => (RouteStepKinds.UTurn, $"Make a U-turn{onto}"),
                    "straight" or "" => (RouteStepKinds.Straight, road.Length > 0 ? $"Continue straight onto {road}" : "Continue straight"),
                    "slight left" or "slight right" => (KindFor(modifier), $"Turn slightly {side}{onto}"),
                    "sharp left" or "sharp right" => (KindFor(modifier), $"Turn sharply {side}{onto}"),
                    _ => (KindFor(modifier), $"Turn {side}{onto}")
                };
        }
    }

    // "Peachtree Street (US 19)", or just the number/name when only one is known.
    private static string RoadName(JsonElement step)
    {
        var name = Text(step, "name");
        // OSRM separates several route numbers with ";" ("I 16; GA 540").
        var number = string.Join(" / ", Text(step, "ref").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > 0 && number.Length > 0 && !name.Contains(number, StringComparison.OrdinalIgnoreCase)) return $"{name} ({number})";
        return name.Length > 0 ? name : number;
    }

    private static string KindFor(string modifier) => modifier switch
    {
        "left" or "sharp left" => RouteStepKinds.Left,
        "right" or "sharp right" => RouteStepKinds.Right,
        "slight left" => RouteStepKinds.SlightLeft,
        "slight right" => RouteStepKinds.SlightRight,
        "uturn" => RouteStepKinds.UTurn,
        _ => RouteStepKinds.Straight
    };

    // OSRM lists signposted destinations as "Atlanta, Macon" or "I 75: Atlanta"; the first is enough.
    private static string FirstDestination(string destinations)
    {
        var first = destinations.Split(',')[0].Trim();
        return first.Contains(':') ? first[(first.IndexOf(':') + 1)..].Trim() is { Length: > 0 } place ? place : first : first;
    }

    public static string Compass(double bearing)
    {
        string[] names = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];
        return names[(int)Math.Round(((bearing % 360) + 360) % 360 / 45) % 8];
    }

    public static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" }
    };

    // "0.4 mi", or feet under a tenth of a mile, as phones show it.
    public static string FormatDistance(double miles) => miles < 0.1
        ? $"{Math.Max(10, Math.Round(miles * 5280 / 10) * 10).ToString("0", CultureInfo.InvariantCulture)} ft"
        : $"{miles.ToString(miles < 10 ? "0.0" : "0", CultureInfo.InvariantCulture)} mi";

    private static string? StopName(IReadOnlyList<string> stopNames, int index) =>
        index >= 0 && index < stopNames.Count ? stopNames[index] : null;

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;
}
