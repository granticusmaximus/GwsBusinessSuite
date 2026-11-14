using System.Globalization;

namespace GwsBusinessSuite.Application.Hazards;

// B8 (13-11-26): a country the world's press is suddenly writing much more about, from GDELT's
// quarter-hour event files (every outlet GDELT reads, in 65 languages, not only World News' own
// feeds). Recent is the last hour's articles about events there; Usual is its average hour over
// the rest of the day. The mirror of World News' "unusually quiet" line.
public sealed record CoverageSurge(string Country, int Recent, double Usual)
{
    public double Times => Usual <= 0 ? 0 : Recent / Usual;
}

public static class CoverageSurgeRules
{
    public static readonly TimeSpan Recent = TimeSpan.FromHours(1);
    // At least this much of the day must be in hand before "usual" means anything.
    public static readonly TimeSpan MinimumBaseline = TimeSpan.FromHours(4);
    public const int MinimumRecent = 40;
    public const double SurgeTimes = 3;
    public const int MaxShown = 6;

    public static IReadOnlyList<CoverageSurge> Find(IReadOnlyCollection<KeyValuePair<DateTimeOffset, IReadOnlyDictionary<string, int>>> files, DateTimeOffset now)
    {
        var recentStart = now - Recent;
        // A file is stamped when its quarter-hour ends, so the one stamped an hour ago is older news.
        var baseline = files.Where(file => file.Key <= recentStart).ToList();
        var recent = files.Where(file => file.Key > recentStart).ToList();
        if (recent.Count == 0 || baseline.Count < MinimumBaseline.TotalMinutes / 15) return [];
        var baselineHours = baseline.Count / 4.0;
        return recent.SelectMany(file => file.Value)
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(country => new CoverageSurge(country.Key, country.Sum(pair => pair.Value),
                baseline.Sum(file => file.Value.GetValueOrDefault(country.Key)) / baselineHours))
            // A country with no usual coverage at all is compared with one article an hour.
            .Select(surge => surge with { Usual = Math.Max(1, surge.Usual) })
            .Where(surge => surge.Recent >= MinimumRecent && surge.Times >= SurgeTimes)
            .OrderByDescending(surge => surge.Times)
            .Take(MaxShown)
            .ToList();
    }

    // Articles per country in one GDELT 2.0 export file: root events with a place (ActionGeo_Type
    // 51 set; ActionGeo_FullName 52 ends with the country), summed NumArticles (33).
    public static IReadOnlyDictionary<string, int> CountryArticles(string tsv)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in tsv.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 61 || f[25] != "1" || f[51] is "" or "0" || f[52].Length == 0) continue;
            var country = f[52][(f[52].LastIndexOf(',') + 1)..].Trim();
            if (country.Length == 0 || !int.TryParse(f[33], NumberStyles.Integer, CultureInfo.InvariantCulture, out var articles) || articles <= 0) continue;
            counts[country] = counts.GetValueOrDefault(country) + articles;
        }
        return counts;
    }
}
