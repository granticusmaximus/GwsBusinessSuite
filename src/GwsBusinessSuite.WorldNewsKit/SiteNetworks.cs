using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// B5 (13-11-26): fake networks. Sites that look independent but are run by one operator usually
// give themselves away in their pages: the same Google Analytics, Tag Manager, AdSense or Meta
// pixel account. Those IDs are read from articles World News already fetches; outlets sharing one
// count as one origin (rules 2026-11-13.3), and the trust panel shows the shared ID as evidence.
// A site registered only weeks ago (RDAP, key-less) is shown as a new site.
public sealed record SiteSignal(string Domain, string Outlet, IReadOnlyList<string> Ids, DateTimeOffset? Registered);

public sealed record SiteNetwork(string Id, IReadOnlyList<string> Outlets, IReadOnlyList<string> Domains);

public static partial class SiteNetworks
{
    // An ID on this many sites or more is a vendor's or a platform's, not an operator's.
    public const int MaxSitesPerId = 25;
    public static readonly TimeSpan NewSite = TimeSpan.FromDays(180);

    // Tracking and ad account IDs in a page's HTML, normalised (upper case), at most 12.
    public static IReadOnlyList<string> Ids(string html)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in GoogleTag().Matches(html)) ids.Add(m.Groups[1].Value.ToUpperInvariant());
        foreach (Match m in TagManager().Matches(html)) ids.Add(m.Value.ToUpperInvariant());
        foreach (Match m in AdSense().Matches(html)) ids.Add("PUB-" + m.Groups[1].Value);
        foreach (Match m in MetaPixel().Matches(html)) ids.Add("FB-" + m.Groups[1].Value);
        return ids.Take(12).ToList();
    }

    // Groups of two or more outlets (on different domains) sharing an ID; one group per set of outlets.
    public static IReadOnlyList<SiteNetwork> Find(IEnumerable<SiteSignal> signals)
    {
        var byId = signals.SelectMany(signal => signal.Ids.Select(id => (Id: id, Signal: signal)))
            .GroupBy(pair => pair.Id, StringComparer.Ordinal)
            .Select(group => (group.Key, Sites: group.Select(pair => pair.Signal).DistinctBy(s => s.Domain, StringComparer.OrdinalIgnoreCase).ToList()))
            .Where(group => group.Sites.Count is >= 2 and < MaxSitesPerId)
            .Where(group => group.Sites.Select(s => s.Outlet).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2)
            .ToList();
        return byId
            .Select(group => new SiteNetwork(group.Key,
                group.Sites.Select(s => s.Outlet).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                group.Sites.Select(s => s.Domain).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .GroupBy(network => string.Join('|', network.Outlets), StringComparer.OrdinalIgnoreCase)
            .Select(same => same.OrderBy(network => network.Id, StringComparer.Ordinal).First())
            .OrderByDescending(network => network.Outlets.Count).ThenBy(network => network.Id, StringComparer.Ordinal)
            .ToList();
    }

    // Outlet -> network name for SourceIndependence: outlets joined through shared IDs (transitively).
    public static IReadOnlyDictionary<string, string> Owners(IReadOnlyList<SiteNetwork> networks)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Root(string outlet)
        {
            while (parent.TryGetValue(outlet, out var up) && !string.Equals(up, outlet, StringComparison.OrdinalIgnoreCase)) outlet = up;
            return outlet;
        }
        foreach (var network in networks)
        {
            foreach (var outlet in network.Outlets) parent.TryAdd(outlet, outlet);
            var first = Root(network.Outlets[0]);
            foreach (var outlet in network.Outlets.Skip(1)) parent[Root(outlet)] = first;
        }
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var network in networks) ids.TryAdd(Root(network.Outlets[0]), network.Id);
        return parent.Keys.ToDictionary(outlet => outlet, outlet => $"Site network ({ids[Root(outlet)]})", StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsNew(SiteSignal signal, DateTimeOffset now) => signal.Registered is { } at && now - at < NewSite;

    // RDAP's answer: the "registration" event's date.
    public static DateTimeOffset? Registered(string rdapJson)
    {
        using var doc = JsonDocument.Parse(rdapJson);
        if (!doc.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in events.EnumerateArray())
        {
            if (e.TryGetProperty("eventAction", out var action) && action.GetString() == "registration"
                && e.TryGetProperty("eventDate", out var date)
                && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return at;
        }
        return null;
    }

    [GeneratedRegex(@"(?:googletagmanager\.com/gtag/js\?id=|gtag\(\s*['""]config['""]\s*,\s*['""])((?:G|AW)-[A-Z0-9]{6,12}|UA-\d{4,10}-\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleTag();

    [GeneratedRegex(@"\bGTM-[A-Z0-9]{4,9}\b")]
    private static partial Regex TagManager();

    [GeneratedRegex(@"\bca-pub-(\d{10,20})\b")]
    private static partial Regex AdSense();

    [GeneratedRegex(@"fbq\(\s*['""]init['""]\s*,\s*['""](\d{10,20})['""]")]
    private static partial Regex MetaPixel();
}
