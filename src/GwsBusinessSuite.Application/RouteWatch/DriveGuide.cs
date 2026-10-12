using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.TrafficIncidents;

namespace GwsBusinessSuite.Application.RouteWatch;

public sealed record DriveFix(double Latitude, double Longitude, double? AccuracyMeters = null, double? SpeedMetersPerSecond = null);

public sealed record DriveState(
    double MilesAlong,
    double MilesOff,
    bool OffRoute,
    RouteStep? NextStep,
    double MilesToNextStep,
    double MilesRemaining,
    double MinutesRemaining,
    IReadOnlyList<RouteCamera> CamerasAhead,
    IReadOnlyList<RouteIncident> IncidentsAhead,
    bool Arrived);

// One update: where the driver is now and anything new worth saying out loud.
public sealed record DriveUpdate(DriveState State, IReadOnlyList<string> Announcements);

// Drive mode: follows a planned route with the device's GPS fixes. Pure and per-drive, so the
// page holds one instance and feeds it each fix; nothing here touches the network.
public sealed class DriveGuide
{
    public const double OffRouteMiles = 0.3;
    // A fix this inaccurate says too little about which road the driver is on.
    public const double MaxAccuracyMeters = 150;
    public const double CamerasAheadMiles = 5;
    public const double IncidentWarningMiles = 5;
    public const double ManeuverSoonMiles = 0.5;
    public const double ManeuverNowMiles = 0.1;
    public const double ArrivedMiles = 0.1;
    public const double ClosureCorridorMiles = 0.3;
    private const int OffRouteFixes = 3;

    private readonly RouteWatchResult _route;
    private readonly RouteWatchService.RouteIndex _index;
    // OSRM's step distances and this index's measured length differ slightly; steps are rescaled
    // onto the index so "next maneuver" lines up with where the fix snaps.
    private readonly double _stepScale;
    private readonly List<RouteIncident> _incidents;
    private readonly HashSet<string> _spoken = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownIncidents;
    private double _milesAlong;
    private int _offFixes;
    private bool _offRoute;
    private bool _arrived;
    private bool _started;

    public DriveGuide(RouteWatchResult route)
    {
        _route = route;
        _index = new RouteWatchService.RouteIndex(route.Path);
        _stepScale = route.DistanceMiles > 0 ? _index.TotalMiles / route.DistanceMiles : 1;
        _incidents = route.Incidents.OrderBy(i => i.MilesAlong).ToList();
        _knownIncidents = _incidents.Select(i => Key(i.Incident)).ToHashSet(StringComparer.Ordinal);
    }

    public double TotalMiles => _index.TotalMiles;

    public DriveUpdate Update(DriveFix fix)
    {
        var said = new List<string>();
        if (fix.AccuracyMeters is > MaxAccuracyMeters && _started)
            return new DriveUpdate(State(_offRoute ? OffRouteMiles : 0), said);

        // Search a window around the last position first; fall back to the whole route when the
        // driver isn't near it (a first fix, or after a detour).
        var ahead = Math.Max(3, (fix.SpeedMetersPerSecond ?? 30) * 120 / 1609.344);
        var hit = _started ? _index.Locate(fix.Latitude, fix.Longitude, _milesAlong - 0.5, _milesAlong + ahead) : (0, double.MaxValue);
        if (hit.MilesOff > OffRouteMiles) hit = _index.Locate(fix.Latitude, fix.Longitude);
        _started = true;

        if (hit.MilesOff > OffRouteMiles)
        {
            if (++_offFixes >= OffRouteFixes && !_offRoute)
            {
                _offRoute = true;
                said.Add("You're off the route. Replan from here if you've taken another way.");
            }
            return new DriveUpdate(State(hit.MilesOff), said);
        }

        _offFixes = 0;
        if (_offRoute)
        {
            _offRoute = false;
            said.Add("Back on the route.");
        }
        // A small step backwards is GPS jitter; a bigger one is real (a U-turn, say).
        if (hit.MilesAlong >= _milesAlong || _milesAlong - hit.MilesAlong > 0.05) _milesAlong = hit.MilesAlong;

        var state = State(hit.MilesOff);
        if (state.Arrived)
        {
            if (!_arrived) said.Add($"You have arrived at {_route.To}.");
            _arrived = true;
            return new DriveUpdate(state, said);
        }

        if (state.NextStep is { } step)
        {
            var key = $"{step.Leg}:{step.StartsAtMile:0.000}";
            if (state.MilesToNextStep <= ManeuverNowMiles && _spoken.Add("now:" + key))
                said.Add(step.Instruction + ".");
            else if (state.MilesToNextStep <= ManeuverSoonMiles && state.MilesToNextStep > ManeuverNowMiles * 2 && _spoken.Add("soon:" + key))
                said.Add($"In {MilesText(state.MilesToNextStep)}, {LowerFirst(step.Instruction)}.");
        }

        foreach (var incident in state.IncidentsAhead.Where(i => i.MilesAlong - _milesAlong <= IncidentWarningMiles))
        {
            if (_spoken.Add("incident:" + Key(incident.Incident)))
                said.Add($"{IncidentCategories.SingularLabel(incident.Incident.Category)} reported {MilesText(incident.MilesAlong - _milesAlong)} ahead on {incident.Incident.RoadwayName}.");
        }
        return new DriveUpdate(state, said);
    }

    // Incidents fetched during the drive: returns the closures on the road ahead that weren't
    // known before (and remembers them, so each is reported once).
    public IReadOnlyList<RouteIncident> AddFreshIncidents(IEnumerable<TrafficIncident> fresh)
    {
        var added = new List<RouteIncident>();
        foreach (var incident in fresh)
        {
            if (incident.Category != IncidentCategories.Closure || _knownIncidents.Contains(Key(incident))) continue;
            var hit = _index.Locate(incident.Latitude, incident.Longitude, _milesAlong, double.MaxValue);
            if (hit.MilesOff > ClosureCorridorMiles || hit.MilesAlong <= _milesAlong) continue;
            _knownIncidents.Add(Key(incident));
            var onRoute = new RouteIncident(incident, hit.MilesAlong, hit.MilesOff);
            _incidents.Add(onRoute);
            added.Add(onRoute);
        }
        _incidents.Sort((a, b) => a.MilesAlong.CompareTo(b.MilesAlong));
        return added;
    }

    // The part of the route still to drive, for fetching fresh incidents along it.
    public BoundingBox RemainingBounds()
    {
        var points = RemainingPath();
        const double Pad = 0.02;
        return new BoundingBox(points.Max(p => p.Latitude) + Pad, points.Min(p => p.Latitude) - Pad,
            points.Max(p => p.Longitude) + Pad, points.Min(p => p.Longitude) - Pad);
    }

    public IReadOnlyList<RoutePoint> RemainingPath()
    {
        var remaining = new List<RoutePoint>();
        var walked = 0d;
        for (var i = 0; i < _route.Path.Count; i++)
        {
            if (i > 0) walked += Miles(_route.Path[i - 1], _route.Path[i]);
            if (walked >= _milesAlong - 0.2) remaining.Add(_route.Path[i]);
        }
        return remaining.Count >= 2 ? remaining : _route.Path.TakeLast(2).ToList();
    }

    // The stops still ahead (the next one and after), for replanning from where the driver is.
    public IReadOnlyList<RouteStop> StopsAhead()
    {
        var stops = _route.Stops ?? [];
        var legs = _route.Legs ?? [];
        var along = _milesAlong / _stepScale;
        var next = 1;
        while (next < legs.Count && legs[next].StartsAtMile <= along) next++;
        return stops.Skip(Math.Min(next, Math.Max(1, stops.Count - 1))).ToList();
    }

    private DriveState State(double milesOff)
    {
        var steps = _route.Steps ?? [];
        var next = steps.FirstOrDefault(s => s.StartsAtMile * _stepScale > _milesAlong + 0.01 && s.Kind != RouteStepKinds.Depart);
        var remaining = Math.Max(0, _index.TotalMiles - _milesAlong);
        var minutes = _index.TotalMiles > 0 ? _route.DurationMinutes * remaining / _index.TotalMiles : 0;
        var cameras = _route.Cameras
            .Where(c => c.MilesAlong >= _milesAlong && c.MilesAlong - _milesAlong <= CamerasAheadMiles && c.Camera.StreamKind == CameraStreamKind.Snapshot)
            .Take(2).ToList();
        var incidents = _incidents.Where(i => i.MilesAlong > _milesAlong).ToList();
        return new DriveState(_milesAlong, milesOff, _offRoute, next, next is null ? remaining : next.StartsAtMile * _stepScale - _milesAlong,
            remaining, minutes, cameras, incidents, !_offRoute && _started && remaining <= ArrivedMiles);
    }

    public static string MilesText(double miles) => miles switch
    {
        < 0.15 => "a few hundred feet",
        < 0.35 => "a quarter mile",
        < 0.65 => "half a mile",
        < 0.9 => "three quarters of a mile",
        < 1.5 => "1 mile",
        _ => $"{Math.Round(miles):0} miles"
    };

    private static string LowerFirst(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1])
        ? char.ToLowerInvariant(text[0]) + text[1..]
        : text;

    private static string Key(TrafficIncident incident) => $"{incident.SourceName}:{incident.Id}";

    private static double Miles(RoutePoint a, RoutePoint b)
    {
        var cos = Math.Cos((a.Latitude + b.Latitude) / 2 * Math.PI / 180);
        var dx = (b.Longitude - a.Longitude) * 69.172 * cos;
        var dy = (b.Latitude - a.Latitude) * 69.0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
