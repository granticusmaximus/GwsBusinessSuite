namespace GwsBusinessSuite.Application.Hazards;

// One thing Overwatch knows about, reduced to what convergence needs. Placed signals have a real
// spot (an earthquake, a fire, a county's power outage, a story placed at a city). Country-wide
// ones (an internet outage, a story placed only at a country) belong to the whole country.
public sealed record ConvergenceSignal(
    string Kind,
    string Title,
    double Latitude,
    double Longitude,
    string? Url = null,
    string? CountryCode = null,
    bool CountryWide = false,
    // Where a story was placed ("Manila, Philippines"), for naming the square.
    string? Place = null);

// A 1° square where several independent kinds of signal meet.
public sealed record ConvergenceCell(
    double South,
    double West,
    IReadOnlyList<string> Kinds,
    int Score,
    string Severity,
    IReadOnlyList<ConvergenceSignal> Signals,
    string Label = "")
{
    public double North => South + 1;
    public double East => West + 1;
    public double CenterLatitude => South + 0.5;
    public double CenterLongitude => West + 0.5;
}

public static class ConvergenceKinds
{
    public const string News = "news";
    public const string Earthquake = "earthquake";
    public const string Wildfire = "wildfire";
    public const string Flooding = "flooding";
    public const string Cyclone = "cyclone";
    public const string Volcano = "volcano";
    public const string PowerOutage = "power outage";
    public const string InternetOutage = "internet outage";
    public const string SevereWeather = "severe weather";

    // Hazard kinds as convergence counts them. A drought is a slow, wide state rather than an
    // event, so it doesn't count. A flooding gauge and a GDACS flood are the same kind.
    public static string? FromHazard(string hazardKind) => hazardKind switch
    {
        HazardKinds.Earthquake => Earthquake,
        HazardKinds.Wildfire => Wildfire,
        HazardKinds.River or HazardKinds.Flood => Flooding,
        HazardKinds.Cyclone => Cyclone,
        HazardKinds.Volcano => Volcano,
        HazardKinds.Internet => InternetOutage,
        _ => null
    };
}

// Geographic convergence (05-11-26, an idea from worldmonitor, written here from its description):
// when independent streams land on the same place at once, it is worth a look even if none of them
// is big alone. The map is cut into 1° squares. A square converges when it holds at least
// MinimumKinds different kinds, at least MinimumPlacedKinds of them placed inside the square. A
// country-wide signal counts in every square of its country, but can't make one converge on its own.
// Aircraft and ships are left out: they are everywhere all the time, not events.
public static class SignalConvergence
{
    public const int MinimumKinds = 3;
    public const int MinimumPlacedKinds = 2;
    public const int MaximumCells = 20;

    public static IReadOnlyList<ConvergenceCell> Find(IEnumerable<ConvergenceSignal> signals, Func<double, double, string?> countryAt)
    {
        var all = signals.ToList();
        var countryWide = all.Where(signal => signal.CountryWide && signal.CountryCode is not null)
            .GroupBy(signal => signal.CountryCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var cells = new List<ConvergenceCell>();
        foreach (var square in all.Where(signal => !signal.CountryWide)
                     .GroupBy(signal => (South: Math.Floor(signal.Latitude), West: Math.Floor(signal.Longitude))))
        {
            var placed = square.ToList();
            var placedKinds = placed.Select(signal => signal.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (placedKinds < MinimumPlacedKinds) continue;

            var countries = placed.Select(signal => signal.CountryCode ?? countryAt(signal.Latitude, signal.Longitude))
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
            var wide = countries.SelectMany(code => countryWide.TryGetValue(code, out var list) ? list : []).ToList();
            var members = placed.Concat(wide).ToList();
            var kinds = members.Select(signal => signal.Kind).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(kind => kind, StringComparer.Ordinal).ToList();
            if (kinds.Count < MinimumKinds) continue;

            var score = Score(kinds.Count, members.Count);
            cells.Add(new ConvergenceCell(square.Key.South, square.Key.West, kinds, score,
                kinds.Count >= 4 || score >= 90 ? "major" : "moderate", members));
        }
        return cells.OrderByDescending(cell => cell.Score).ThenByDescending(cell => cell.Signals.Count)
            .ThenBy(cell => cell.South).ThenBy(cell => cell.West)
            .Take(MaximumCells).ToList();
    }

    // 25 for each distinct kind, plus 2 for each signal up to 25 more, capped at 100.
    public static int Score(int kinds, int signals) => Math.Min(100, kinds * 25 + Math.Min(25, signals * 2));
}
