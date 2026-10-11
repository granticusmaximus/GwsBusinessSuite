using System.Text;
using GwsBusinessSuite.Application.Abstractions;

namespace GwsBusinessSuite.Application.RouteWatch;

// Directions as plain text (Copy/Share in Overwatch) and the links that reopen a route: the
// Overwatch page itself (?route=...) and its print page (/admin/osint/directions?stop=...).
public static class RouteDirectionsText
{
    public static string Format(RouteWatchResult route, DateTimeOffset generatedAt)
    {
        var text = new StringBuilder();
        text.AppendLine($"Directions: {route.From} to {route.To}");
        text.AppendLine($"{route.DistanceMiles:N0} mi, about {FormatDuration(route.DurationMinutes)} · {generatedAt.ToGwsDateTime()}");
        if (route.Legs is { Count: > 1 } legs)
        {
            foreach (var leg in legs) text.AppendLine($"  {leg.From} to {leg.To}: {leg.DistanceMiles:N0} mi, {FormatDuration(leg.DurationMinutes)}");
        }
        text.AppendLine();
        var number = 1;
        foreach (var step in route.Steps ?? [])
        {
            text.Append(number++).Append(". ").Append(step.Instruction);
            if (step.DistanceMiles > 0) text.Append(" (").Append(RouteDirections.FormatDistance(step.DistanceMiles)).Append(')');
            text.AppendLine();
        }
        var warnings = Warnings(route).ToList();
        if (warnings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Along the way:");
            foreach (var warning in warnings) text.AppendLine("- " + warning);
        }
        return text.ToString().TrimEnd();
    }

    // Incidents, weather alerts, icy roads and message signs on the route, in driving order.
    public static IEnumerable<string> Warnings(RouteWatchResult route)
    {
        foreach (var alert in route.Alerts) yield return $"Weather alert: {alert.Event} ({alert.AreaDescription})";
        var items = new List<(double Mile, string Text)>();
        items.AddRange(route.Incidents.Select(i => (i.MilesAlong,
            $"Mile {i.MilesAlong:0}: {TrafficIncidents.IncidentCategories.Label(i.Incident.Category)} - {i.Incident.RoadwayName}: {i.Incident.Description}")));
        items.AddRange((route.RoadWeather ?? []).Where(r => r.Station.Risk == RoadWeather.RoadIceRisk.IceLikely)
            .Select(r => (r.MilesAlong, $"Mile {r.MilesAlong:0}: ice likely near {r.Station.Name}")));
        items.AddRange((route.Signs ?? []).Select(s => (s.MilesAlong, $"Mile {s.MilesAlong:0}: sign says \"{s.Sign.Text}\"")));
        foreach (var item in items.OrderBy(i => i.Mile)) yield return item.Text;
    }

    public static string OverwatchLink(IEnumerable<string> stops) => "/admin/osint?" + Query("route", stops);

    public static string PrintLink(IEnumerable<string> stops) => "/admin/osint/directions?" + Query("stop", stops);

    public static string FormatDuration(double minutes)
    {
        var total = (int)Math.Round(minutes);
        return total < 60 ? $"{total} min" : $"{total / 60} h {total % 60:00} min";
    }

    private static string Query(string name, IEnumerable<string> stops) =>
        string.Join('&', stops.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => $"{name}={Uri.EscapeDataString(s.Trim())}"));
}
