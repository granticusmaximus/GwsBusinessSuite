using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;

namespace GwsBusinessSuite.Application.RouteWatch;

public static class DepartureRatings
{
    public const string Good = "good";
    public const string Fair = "fair";
    public const string Poor = "poor";
}

// One possible departure time, scored: the lower Score, the easier the drive. Reasons say why.
public sealed record DepartureOption(
    DateTimeOffset DepartUtc,
    DateTimeOffset ArriveUtc,
    double Score,
    string Rating,
    IReadOnlyList<string> Reasons);

public sealed record DeparturePlan(IReadOnlyList<DepartureOption> Options, DepartureOption? Best, string Note);

// A point along the route with its NWS hourly forecast.
public sealed record RouteForecastPoint(double Mile, double Latitude, double Longitude, IReadOnlyList<HourlyForecastPeriod> Hours);

// "When should I leave?": tries departures over the next hours and scores what the drive would
// meet - forecast rain/storms/snow where you'd be at that time, sun glare and darkness, and
// closures still in place when you'd reach them. Drive times are the routing service's own (no
// live traffic forecast exists for free), so rush hour isn't modelled - the plan says so.
public static class DeparturePlanner
{
    public const int WindowHours = 12;
    public const int StepMinutes = 30;
    public const double SampleEveryMiles = 25;
    public const int MaxSamplePoints = 12;

    // Every SampleEveryMiles along the route plus both ends.
    public static IReadOnlyList<(double Mile, RoutePoint Point)> SamplePoints(IReadOnlyList<RoutePoint> path)
    {
        if (path.Count < 2) return [];
        var points = new List<(double, RoutePoint)> { (0, path[0]) };
        var miles = 0d;
        var next = SampleEveryMiles;
        for (var i = 1; i < path.Count; i++)
        {
            miles += StreetMiles(path[i - 1], path[i]);
            if (miles >= next)
            {
                points.Add((miles, path[i]));
                next += SampleEveryMiles;
            }
        }
        if (points[^1].Item1 < miles - 1) points.Add((miles, path[^1]));
        if (points.Count <= MaxSamplePoints) return points;
        var step = (points.Count - 1) / (double)(MaxSamplePoints - 1);
        return Enumerable.Range(0, MaxSamplePoints).Select(i => points[(int)Math.Round(i * step)]).Distinct().ToList();
    }

    public static DeparturePlan Plan(RouteWatchResult route, IReadOnlyList<RouteForecastPoint> forecast, DateTimeOffset nowUtc)
    {
        var first = RoundUp(nowUtc, StepMinutes);
        var options = new List<DepartureOption>();
        for (var depart = first; depart <= first.AddHours(WindowHours); depart = depart.AddMinutes(StepMinutes))
        {
            options.Add(Score(route, forecast, depart, nowUtc));
        }
        var best = options.OrderBy(option => option.Score).ThenBy(option => option.DepartUtc).FirstOrDefault();
        var hasForecast = forecast.Any(point => point.Hours.Count > 0);
        var note = (hasForecast ? "" : "No NWS forecast for this route (outside the US?), so only sun glare, darkness and closures count. ")
            + "Drive times are the routing estimate; rush-hour traffic isn't included.";
        return new DeparturePlan(options, best, note);
    }

    public static DepartureOption Score(RouteWatchResult route, IReadOnlyList<RouteForecastPoint> forecast, DateTimeOffset departUtc, DateTimeOffset nowUtc)
    {
        var reasons = new List<string>();
        var score = 0d;
        var totalMiles = route.DistanceMiles;
        DateTimeOffset At(double mile) =>
            departUtc.AddMinutes(DrivingLightAnalyzer.MinutesAtMile(mile, route.Legs, totalMiles, route.DurationMinutes));

        // Weather: each sample point stands for the miles around it.
        var ordered = forecast.OrderBy(point => point.Mile).ToList();
        var wetMiles = 0d;
        string? worstWeather = null;
        var worstWeight = 0d;
        for (var i = 0; i < ordered.Count; i++)
        {
            var point = ordered[i];
            var startMile = i == 0 ? 0 : (ordered[i - 1].Mile + point.Mile) / 2;
            var endMile = i == ordered.Count - 1 ? totalMiles : (point.Mile + ordered[i + 1].Mile) / 2;
            var miles = Math.Max(0, endMile - startMile);
            var when = At(point.Mile);
            var hour = point.Hours.FirstOrDefault(h => h.StartUtc <= when && when < h.EndUtc);
            if (hour is null) continue;
            var chance = (hour.ChanceOfPrecipitationPercent ?? 0) / 100.0;
            var text = hour.ShortForecast ?? string.Empty;
            var winter = ContainsAny(text, "snow", "sleet", "freezing", "ice", "wintry")
                || hour.TemperatureFahrenheit <= 34 && chance >= 0.3;
            var storms = ContainsAny(text, "thunderstorm", "t-storm");
            var weight = chance * (winter ? 3 : storms ? 2 : 1);
            if (ContainsAny(text, "fog")) weight = Math.Max(weight, 0.5);
            score += weight * miles / 10;
            wetMiles += chance >= 0.4 ? miles : 0;
            if (weight > worstWeight && weight >= 0.3)
            {
                worstWeight = weight;
                var kind = winter ? "snow/ice risk" : storms ? "thunderstorms" : ContainsAny(text, "fog") && chance < 0.3 ? "fog" : $"{chance:P0} chance of rain";
                worstWeather = $"{Capitalise(kind)} near mile {point.Mile:0} around {when.ToLocalTime().ToGwsTime()} ({text.Trim()})";
            }
        }
        if (worstWeather is not null) reasons.Add(worstWeather);
        if (wetMiles >= 5) reasons.Add($"About {wetMiles:0} mi likely wet");

        // Sun glare and darkness.
        var light = DrivingLightAnalyzer.Analyze(route.Path, route.Legs, route.DurationMinutes, departUtc);
        var glare = light.Where(s => s.Kind == DrivingLightKind.SunGlare).ToList();
        var glareMiles = glare.Sum(s => s.EndMile - s.StartMile);
        var darkMiles = light.Where(s => s.Kind == DrivingLightKind.Dark).Sum(s => s.EndMile - s.StartMile);
        // Darkness alone is normal night driving: it tips a choice, it doesn't make a drive bad.
        score += glareMiles * 0.08 + darkMiles * 0.008;
        if (glareMiles >= 2)
        {
            var worst = glare.OrderByDescending(s => s.EndMile - s.StartMile).First();
            reasons.Add($"Sun glare for {glareMiles:0} mi ({worst.Heading.ToLowerInvariant()}, {worst.StartUtc.ToLocalTime().ToGwsTime()}-{worst.EndUtc.ToLocalTime().ToGwsTime()})");
        }
        if (darkMiles >= 10) reasons.Add($"{darkMiles:0} mi in the dark");

        // Closures still in place when you'd reach them (crashes are assumed to clear within ~90 min).
        foreach (var item in route.Incidents)
        {
            var reach = At(item.MilesAlong);
            var category = item.Incident.Category;
            var active = item.Incident.EndsAt is not { } ends || ends > reach;
            if (category == IncidentCategories.Crash) active = reach < nowUtc.AddMinutes(90);
            if (!active || category is not (IncidentCategories.Closure or IncidentCategories.Crash)) continue;
            score += category == IncidentCategories.Closure ? 1.5 : 1;
            reasons.Add($"{(category == IncidentCategories.Closure ? "Closure" : "Crash")} at mile {item.MilesAlong:0} ({item.Incident.RoadwayName})");
        }

        var rating = score < 1.5 ? DepartureRatings.Good : score < 5 ? DepartureRatings.Fair : DepartureRatings.Poor;
        if (reasons.Count == 0) reasons.Add("Nothing flagged");
        return new DepartureOption(departUtc, departUtc.AddMinutes(route.DurationMinutes), Math.Round(score, 2), rating, reasons);
    }

    private static DateTimeOffset RoundUp(DateTimeOffset value, int minutes)
    {
        var ticks = TimeSpan.FromMinutes(minutes).Ticks;
        var rounded = (value.UtcTicks + ticks - 1) / ticks * ticks;
        return new DateTimeOffset(rounded, TimeSpan.Zero);
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static double StreetMiles(RoutePoint a, RoutePoint b)
    {
        const double earthMiles = 3958.8;
        var dLat = (b.Latitude - a.Latitude) * Math.PI / 180;
        var dLon = (b.Longitude - a.Longitude) * Math.PI / 180;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(a.Latitude * Math.PI / 180) * Math.Cos(b.Latitude * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * earthMiles * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

// Fetches the forecasts the planner needs (one NWS hourly forecast per sample point, in parallel).
public sealed class DeparturePlannerService(INwsForecastService forecasts, TimeProvider timeProvider)
{
    public async Task<DeparturePlan> PlanAsync(RouteWatchResult route, CancellationToken cancellationToken = default)
    {
        var samples = DeparturePlanner.SamplePoints(route.Path);
        var hours = await Task.WhenAll(samples.Select(async sample =>
        {
            try
            {
                return await forecasts.GetHourlyAsync(sample.Point.Latitude, sample.Point.Longitude, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return (IReadOnlyList<HourlyForecastPeriod>)[];
            }
        }));
        var points = samples.Select((sample, i) => new RouteForecastPoint(sample.Mile, sample.Point.Latitude, sample.Point.Longitude, hours[i])).ToList();
        return DeparturePlanner.Plan(route, points, timeProvider.GetUtcNow());
    }
}
