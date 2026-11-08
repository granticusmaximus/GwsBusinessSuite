using System.Globalization;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.LiveLayers;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Gathers what Overwatch already knows about a view and finds where independent signals meet
// (SignalConvergence). Every source here is cached by its own service, so this adds no new calls
// out. Only operational World News kinds count (disaster, transport, conflict, health), as in Brief me.
public sealed class SignalConvergenceService(
    HazardLayerService hazards,
    InternetOutageService internet,
    PowerOutageService power,
    INwsAlertsService weatherAlerts,
    TimeProvider time,
    ILogger<SignalConvergenceService> logger,
    WorldNewsMapService? worldNews = null)
{
    // Small outages are constant across the US; a thousand customers out is an event.
    public const int MinimumCustomersOut = 1_000;
    private static readonly BoundingBox World = new(90, -90, 180, -180);

    public async Task<IReadOnlyList<ConvergenceCell>> GetAsync(BoundingBox bounds, CancellationToken ct = default)
    {
        var since = time.GetUtcNow() - InternetOutageService.Window;
        var hazardTask = Safely(async () => (await hazards.GetHazardsAsync(bounds, ct)).Features, "hazards");
        var internetTask = Safely(() => internet.GetAsync(ct), "internet outages");
        var powerTask = Safely(async () => (await power.GetAsync(bounds, ct)).Areas, "power outages");
        var alertTask = Safely(() => weatherAlerts.GetActiveAlertsAsync(bounds, ct), "weather alerts");
        var newsTask = worldNews is null
            ? Task.FromResult<IReadOnlyList<WorldNewsMapSignal>>([])
            : Safely(() => Task.Run(() => worldNews.Signals(World, since, SituationKinds.Operational), ct), "world news");
        await Task.WhenAll(hazardTask, internetTask, powerTask, alertTask, newsTask);

        var signals = Signals(hazardTask.Result, internetTask.Result, powerTask.Result, alertTask.Result, newsTask.Result, bounds);
        return SignalConvergence.Find(signals, WorldNewsPlaces.CountryAt)
            .Where(cell => cell.North >= bounds.South && cell.South <= bounds.North && cell.East >= bounds.West && cell.West <= bounds.East)
            .Select(cell => cell with { Label = Label(cell) })
            .ToList();
    }

    public static IReadOnlyList<ConvergenceSignal> Signals(
        IEnumerable<HazardFeature> hazardList,
        IEnumerable<HazardFeature> internetOutages,
        IEnumerable<PowerOutageArea> powerOutages,
        IEnumerable<WeatherAlert> alerts,
        IEnumerable<WorldNewsMapSignal> news,
        BoundingBox bounds)
    {
        var signals = new List<ConvergenceSignal>();
        foreach (var hazard in hazardList)
            if (ConvergenceKinds.FromHazard(hazard.Kind) is { } kind)
                signals.Add(new(kind, hazard.Title, hazard.Latitude, hazard.Longitude, hazard.Url));
        // Google traffic alone (minor) is often noise, so it doesn't count.
        foreach (var outage in internetOutages.Where(o => o.Severity != "minor" && o.Id.StartsWith("ioda-", StringComparison.Ordinal)))
            signals.Add(new(ConvergenceKinds.InternetOutage, outage.Title, outage.Latitude, outage.Longitude, outage.Url,
                outage.Id["ioda-".Length..], CountryWide: true));
        foreach (var area in powerOutages.Where(a => a.CustomersOut >= MinimumCustomersOut))
            signals.Add(new(ConvergenceKinds.PowerOutage, string.Create(CultureInfo.InvariantCulture, $"{area.Name}, {area.State}: {area.CustomersOut:N0} customers out"),
                area.Latitude, area.Longitude, null, "US"));
        foreach (var alert in alerts.Where(a => a.Severity is "Extreme" or "Severe"))
        {
            var ring = alert.Rings.FirstOrDefault(r => r.Count > 0);
            if (ring is null) continue;
            signals.Add(new(ConvergenceKinds.SevereWeather, $"NWS {alert.Event}", ring.Average(p => p.Latitude), ring.Average(p => p.Longitude), null, "US"));
        }
        foreach (var story in news)
        {
            var location = story.Location;
            if (location.City is not null)
            {
                if (bounds.Contains(location.Latitude, location.Longitude))
                    signals.Add(new(ConvergenceKinds.News, story.Latest, location.Latitude, location.Longitude, story.Sources.FirstOrDefault()?.Url, location.CountryCode,
                        Place: location.Label));
            }
            else
            {
                signals.Add(new(ConvergenceKinds.News, story.Latest, location.Latitude, location.Longitude, story.Sources.FirstOrDefault()?.Url,
                    location.CountryCode, CountryWide: true));
            }
        }
        return signals;
    }

    // "Manila, Philippines" when a story names the city, else the country and the square's corner.
    public static string Label(ConvergenceCell cell)
    {
        var named = cell.Signals.FirstOrDefault(s => !s.CountryWide && s.Place is not null)?.Place;
        var code = cell.Signals.Where(s => !s.CountryWide).Select(s => s.CountryCode ?? WorldNewsPlaces.CountryAt(s.Latitude, s.Longitude)).OfType<string>().FirstOrDefault();
        var country = WorldNewsPlaces.Instance.Country(code)?.Name;
        var corner = string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(cell.South):0}°{(cell.South >= 0 ? "N" : "S")} {Math.Abs(cell.West):0}°{(cell.West >= 0 ? "E" : "W")}");
        if (named is not null) return named;
        return country is null ? corner : $"{country} ({corner})";
    }

    private async Task<IReadOnlyList<T>> Safely<T>(Func<Task<IReadOnlyList<T>>> load, string what)
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Convergence: couldn't load {What}", what);
            return [];
        }
    }

    // Reporting Blackouts & Silence Detection: detects active hazard/outage clusters with zero news coverage.
    public static IReadOnlyList<(double Latitude, double Longitude, string Reason)> DetectReportingBlackouts(
        IEnumerable<HazardFeature> hazardList,
        IEnumerable<HazardFeature> internetOutages,
        IEnumerable<WorldNewsMapSignal> newsList,
        BoundingBox bounds)
    {
        var blackouts = new List<(double Latitude, double Longitude, string Reason)>();
        var newsPoints = newsList.Select(n => (n.Latitude, n.Longitude)).ToList();
        
        foreach (var outage in internetOutages.Where(o => o.Severity != "minor"))
        {
            bool hasNearbyNews = newsPoints.Any(p => Math.Abs(p.Latitude - outage.Latitude) < 2.0 && Math.Abs(p.Longitude - outage.Longitude) < 2.0);
            if (!hasNearbyNews && bounds.Contains(outage.Latitude, outage.Longitude))
            {
                blackouts.Add((outage.Latitude, outage.Longitude, $"Internet blackout ({outage.Title}) without active news coverage"));
            }
        }

        foreach (var hazard in hazardList.Where(h => h.Severity == "major"))
        {
            bool hasNearbyNews = newsPoints.Any(p => Math.Abs(p.Latitude - hazard.Latitude) < 1.5 && Math.Abs(p.Longitude - hazard.Longitude) < 1.5);
            if (!hasNearbyNews && bounds.Contains(hazard.Latitude, hazard.Longitude))
            {
                blackouts.Add((hazard.Latitude, hazard.Longitude, $"Major {hazard.Kind} hazard ({hazard.Title}) with zero reporting news"));
            }
        }

        return blackouts;
    }
}
