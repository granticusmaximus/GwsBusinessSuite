using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SGPdotNET.Exception;
using SGPdotNET.Observation;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record SatellitePoint(double Latitude, double Longitude, double AltitudeKm);

public sealed record SatellitePosition(
    string Id,
    string Name,
    string Group,
    double Latitude,
    double Longitude,
    double AltitudeKm,
    double PeriodMinutes,
    IReadOnlyList<SatellitePoint> Path,
    // Objects sharing this one's orbit (modules and craft docked to a station).
    IReadOnlyList<string> With);

public sealed record SatelliteResult(IReadOnlyList<SatellitePosition> Satellites, DateTimeOffset? ElementsFrom, string? Note);

// Satellites for Overwatch's SPACE group (06-11-26): CelesTrak's orbital elements for the space
// stations and the brightest satellites, key-less, propagated here with SGP4 (SGP.NET). CelesTrak
// updates its elements about every two hours and asks not to be polled more often, so each group
// is fetched at most every RefreshEvery, and the last good copy is kept if a fetch fails. Positions
// are worked out on each refresh from the cached elements, with no call out.
public sealed class SatelliteService(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<SatelliteService> logger)
{
    private const string ElementsUrl = "https://celestrak.org/NORAD/elements/gp.php?FORMAT=tle&GROUP=";
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(2);
    public static readonly TimeSpan KeepLastGood = TimeSpan.FromDays(2);
    public const int PathMinutes = 92;
    public const int PathStepMinutes = 2;
    public const string Stations = "stations";
    public const string Visible = "visual";
    public const string StationGroup = "space station";
    public const string NearStationsGroup = "near the stations";
    public const string VisibleGroup = "visible";

    public static bool IsStation(string name) =>
        name.StartsWith("ISS", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CSS", StringComparison.OrdinalIgnoreCase)
        || name.Contains("TIANGONG", StringComparison.OrdinalIgnoreCase);

    public async Task<SatelliteResult> GetAsync(CancellationToken ct = default)
    {
        var stations = await ElementsAsync(Stations, ct);
        var visible = await ElementsAsync(Visible, ct);
        if (stations.Elements.Count == 0 && visible.Elements.Count == 0)
            return new SatelliteResult([], null, "CelesTrak's orbital elements couldn't be reached just now.");
        var now = time.GetUtcNow().UtcDateTime;
        // CelesTrak's stations group also holds debris and small satellites near them (FREGAT DEB,
        // KNACKSAT-2 on 06-11-26): only the ISS and China's station are stations, with an orbit line.
        var satellites = Positions(stations.Elements, StationGroup, now, withPaths: true)
            .Select(s => IsStation(s.Name) ? s : s with { Group = NearStationsGroup, Path = [] })
            .Concat(Positions(visible.Elements, VisibleGroup, now, withPaths: false))
            .DistinctBy(s => s.Id)
            .ToList();
        return new SatelliteResult(satellites, new[] { stations.FetchedAt, visible.FetchedAt }.Where(t => t is not null).Min(), null);
    }

    private sealed record Cached(IReadOnlyList<(string Name, string Line1, string Line2)> Elements, DateTimeOffset? FetchedAt);

    private async Task<Cached> ElementsAsync(string group, CancellationToken ct)
    {
        var fresh = $"sky:satellites:{group}";
        var lastGood = $"sky:satellites:{group}:last-good";
        if (cache.TryGetValue(fresh, out Cached? cached) && cached is not null) return cached;
        try
        {
            var elements = ParseTle(await httpClient.GetStringAsync(ElementsUrl + group, ct));
            if (elements.Count == 0) throw new InvalidOperationException($"CelesTrak's {group} group came back empty.");
            var loaded = new Cached(elements, time.GetUtcNow());
            cache.Set(fresh, loaded, RefreshEvery);
            cache.Set(lastGood, loaded, KeepLastGood);
            return loaded;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Satellites layer: CelesTrak {Group} request failed", group);
            // Don't ask again straight away; use the last good elements for a while (they stay usable for days).
            var keep = cache.TryGetValue(lastGood, out Cached? previous) && previous is not null ? previous : new Cached([], null);
            cache.Set(fresh, keep, TimeSpan.FromMinutes(20));
            return keep;
        }
    }

    // Three-line elements: a name line, then lines 1 and 2.
    public static IReadOnlyList<(string Name, string Line1, string Line2)> ParseTle(string text)
    {
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList();
        var elements = new List<(string, string, string)>();
        for (var i = 0; i + 2 < lines.Count; i++)
        {
            if (!lines[i + 1].StartsWith("1 ", StringComparison.Ordinal) || !lines[i + 2].StartsWith("2 ", StringComparison.Ordinal)) continue;
            elements.Add((lines[i].Trim(), lines[i + 1], lines[i + 2]));
            i += 2;
        }
        return elements;
    }

    // Where each satellite is at `at`, and (for stations) where it will be over the next orbit.
    // Objects on the very same orbit (a module docked to the ISS has the ISS's elements) are one dot.
    public static IReadOnlyList<SatellitePosition> Positions(IEnumerable<(string Name, string Line1, string Line2)> elements, string group,
        DateTime at, bool withPaths)
    {
        var positions = new List<SatellitePosition>();
        // Line 2's columns 9-63 are the orbit itself; the revolution count and checksum after them
        // differ between a station and the modules docked to it.
        foreach (var orbit in elements.GroupBy(e => e.Line2.Length >= 63 ? e.Line2.Substring(8, 55) : e.Line2))
        {
            var (name, line1, line2) = orbit.First();
            try
            {
                var satellite = new Satellite(name, line1, line2);
                var here = satellite.Predict(at).ToGeodetic();
                var path = new List<SatellitePoint>();
                if (withPaths)
                    for (var minute = 0; minute <= PathMinutes; minute += PathStepMinutes)
                    {
                        var point = satellite.Predict(at.AddMinutes(minute)).ToGeodetic();
                        path.Add(new SatellitePoint(point.Latitude.Degrees, point.Longitude.Degrees, point.Altitude));
                    }
                var revolutionsPerDay = line2.Length >= 63 && double.TryParse(line2.AsSpan(52, 11), NumberStyles.Float, CultureInfo.InvariantCulture, out var revs) ? revs : 0;
                positions.Add(new SatellitePosition(line1.Length >= 7 ? line1[2..7].Trim() : name, name, group,
                    here.Latitude.Degrees, here.Longitude.Degrees, here.Altitude,
                    revolutionsPerDay > 0 ? 1440 / revolutionsPerDay : 0, path,
                    orbit.Skip(1).Select(e => e.Name).ToList()));
            }
            catch (Exception ex) when (ex is SatellitePropagationException or DecayedException or TleException or ArgumentException or FormatException)
            {
                // A decayed or malformed entry is simply left out.
            }
        }
        return positions;
    }
}
