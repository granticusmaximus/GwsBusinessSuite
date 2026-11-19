using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Hazards;

public sealed record ConflictLayer(IReadOnlyList<HazardFeature> Reported, IReadOnlyList<HazardFeature> Confirmed, TimeSpan ReportedSpan,
    string? ConfirmedRelease);

// Conflict for Overwatch (07-11-26), from two key-less sources:
//   - Reported: GDELT's event files, published every 15 minutes, of events machine-read from news
//     worldwide. Kept: material conflict (bombings, assassinations, military force, mass violence)
//     from 2 or more sources or in 5 or more articles, in an article's lead paragraph (GDELT's
//     IsRootEvent), at a named place more precise than a whole country. (2 sources alone kept 8 of
//     222 such events over two hours on 07-11-26; the articles rule 108, the lead paragraph 74.)
//     Machine reading still mistakes some crime, policy talk and names for conflict and places, so
//     these say "reported", and the page can hide them.
//   - Confirmed: UCDP's candidate events (Uppsala University), human-coded each month with deaths.
//     Its downloads need no token (the API does); the newest monthly file is found on the downloads
//     page. They run a month or more behind.
public sealed partial class ConflictEventService(HttpClient httpClient, IMemoryCache cache, TimeProvider time, ILogger<ConflictEventService> logger)
{
    private const string GdeltIndexUrl = "https://data.gdeltproject.org/gdeltv2/lastupdate.txt";
    private const string GdeltFileUrl = "https://data.gdeltproject.org/gdeltv2/{0}.export.CSV.zip";
    private const string UcdpDownloadsUrl = "https://ucdp.uu.se/downloads/";
    private const string WindowKey = "conflict:gdelt-window";
    private const string CheckedKey = "conflict:gdelt-checked";
    private const string UcdpKey = "conflict:ucdp";
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public static readonly TimeSpan ConfirmedSpan = TimeSpan.FromDays(90);
    // Files fetched per refresh, newest first: the first view has the last few hours and later
    // refreshes fill in the rest of the day.
    public const int FilesPerRefresh = 24;

    // One refresh at a time across every viewer, since the window is shared.
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public async Task<ConflictLayer> GetAsync(CancellationToken ct = default)
    {
        var window = await RefreshGdeltAsync(ct);
        var (confirmed, release) = await GetUcdpAsync(ct);
        var now = time.GetUtcNow();
        List<GdeltEvent> events;
        TimeSpan span;
        lock (window)
        {
            events = window.Events.Where(e => e.Seen >= now - Window).ToList();
            span = window.Files.Count == 0 ? TimeSpan.Zero : now - window.Files.Min();
        }
        return new ConflictLayer(Reported(events), confirmed, span > Window ? Window : span, release);
    }

    private sealed class GdeltWindow
    {
        public List<GdeltEvent> Events { get; } = [];
        public HashSet<DateTimeOffset> Files { get; } = [];
        // B8: articles per country (where the event happened) in each quarter-hour file, every kind
        // of event, for World News' "surging worldwide" line. Same download as the conflict pins.
        public Dictionary<DateTimeOffset, IReadOnlyDictionary<string, int>> CountryArticles { get; } = [];
    }

    // B8 (13-11-26): countries the world's press suddenly writes much more about, from GDELT's
    // quarter-hour event files (the last hour against the rest of the day).
    public async Task<IReadOnlyList<CoverageSurge>> SurgesAsync(CancellationToken ct = default)
    {
        var window = await RefreshGdeltAsync(ct);
        lock (window)
        {
            return CoverageSurgeRules.Find(window.CountryArticles.ToList(), time.GetUtcNow());
        }
    }

    private async Task<GdeltWindow> RefreshGdeltAsync(CancellationToken ct)
    {
        var window = cache.GetOrCreate(WindowKey, entry => { entry.Priority = CacheItemPriority.NeverRemove; return new GdeltWindow(); })!;
        if (cache.TryGetValue(CheckedKey, out _)) return window;
        await RefreshGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CheckedKey, out _)) return window;
            var index = await httpClient.GetStringAsync(GdeltIndexUrl, ct);
            if (LatestStamp(index) is not { } latest) return window;
            var now = time.GetUtcNow();
            List<DateTimeOffset> wanted;
            lock (window)
            {
                wanted = Enumerable.Range(0, (int)(Window.TotalMinutes / 15))
                    .Select(i => latest.AddMinutes(-15 * i))
                    .Where(stamp => stamp > now - Window && !window.Files.Contains(stamp))
                    .Take(FilesPerRefresh)
                    .ToList();
            }
            var results = new (DateTimeOffset Stamp, IReadOnlyList<GdeltEvent>? Events, IReadOnlyDictionary<string, int>? Countries)[wanted.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, wanted.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (i, token) =>
                {
                    try
                    {
                        var url = string.Format(CultureInfo.InvariantCulture, GdeltFileUrl, wanted[i].ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                        await using var zip = await httpClient.GetStreamAsync(url, token);
                        using var buffer = new MemoryStream();
                        await zip.CopyToAsync(buffer, token);
                        buffer.Position = 0;
                        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
                        using var reader = new StreamReader(archive.Entries[0].Open(), Encoding.UTF8);
                        var tsv = await reader.ReadToEndAsync(token);
                        results[i] = (wanted[i], ParseGdeltExport(tsv), CoverageSurgeRules.CountryArticles(tsv));
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                                   || (ex is TaskCanceledException && !token.IsCancellationRequested))
                    {
                        // A missing quarter-hour (GDELT skips some) is tried again on a later refresh.
                        logger.LogDebug(ex, "GDELT file {Stamp} failed", wanted[i]);
                        results[i] = (wanted[i], null, null);
                    }
                });
            lock (window)
            {
                foreach (var (stamp, events, countries) in results.Where(r => r.Events is not null))
                {
                    if (!window.Files.Add(stamp)) continue;
                    window.Events.AddRange(events!);
                    window.CountryArticles[stamp] = countries!;
                }
                window.Events.RemoveAll(e => e.Seen < now - Window);
                window.Files.RemoveWhere(stamp => stamp < now - Window);
                foreach (var old in window.CountryArticles.Keys.Where(stamp => stamp < now - Window).ToList()) window.CountryArticles.Remove(old);
            }
            cache.Set(CheckedKey, true, TimeSpan.FromMinutes(5));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Conflict layer: GDELT's index failed");
        }
        finally
        {
            RefreshGate.Release();
        }
        return window;
    }

    private async Task<(IReadOnlyList<HazardFeature> Features, string? Release)> GetUcdpAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(UcdpKey, out (IReadOnlyList<HazardFeature>, string?) cached)) return cached;
        try
        {
            var release = NewestCandidateFile(await httpClient.GetStringAsync(UcdpDownloadsUrl, ct));
            if (release is null) return ([], null);
            await using var stream = await httpClient.GetStreamAsync(release, ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var result = (Confirmed(ParseUcdp(reader)), Path.GetFileNameWithoutExtension(new Uri(release).AbsolutePath));
            cache.Set(UcdpKey, result, TimeSpan.FromHours(12));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Conflict layer: UCDP's candidate events failed");
            return ([], null);
        }
    }

    public sealed record GdeltEvent(string Code, string Place, double Latitude, double Longitude, int Sources, int Articles, string Url, DateTimeOffset Seen,
        string Actor1 = "", string Actor2 = "");

    // "<size> <md5> http://data.gdeltproject.org/gdeltv2/20261107211500.export.CSV.zip"
    public static DateTimeOffset? LatestStamp(string index)
    {
        var match = ExportStamp().Match(index);
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? new DateTimeOffset(at, TimeSpan.Zero) : null;
    }

    // GDELT 2.0's event table: tab-separated, 61 columns. The ones used: IsRootEvent 25, EventCode 26,
    // EventRootCode 28, QuadClass 29, NumSources 32, NumArticles 33, ActionGeo_Type 51,
    // ActionGeo_FullName 52, ActionGeo_Lat 56, ActionGeo_Long 57, DATEADDED 59, SOURCEURL 60.
    public static IReadOnlyList<GdeltEvent> ParseGdeltExport(string tsv)
    {
        var events = new List<GdeltEvent>();
        foreach (var line in tsv.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f.Length < 61 || f[25] != "1" || f[29] != "4" || Label(f[26]) is null || f[51] is "" or "0" or "1" || f[52].Length == 0) continue;
            int.TryParse(f[32], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sources);
            int.TryParse(f[33], NumberStyles.Integer, CultureInfo.InvariantCulture, out var articles);
            if (sources < 2 && articles < 5) continue;
            if (!double.TryParse(f[56], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(f[57], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
            if (!DateTime.TryParseExact(f[59], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var added)) continue;
            events.Add(new GdeltEvent(f[26], f[52], lat, lon, Math.Max(sources, 1), Math.Max(articles, 1), f[60], new DateTimeOffset(added, TimeSpan.Zero),
                Actor(f[6]), Actor(f[16])));
        }
        return events;
    }

    // CAMEO codes kept, as words. 180-182 and 184 (unconventional violence, abduction, assault, human
    // shields) are mostly crime in the news, so they're left out.
    private static string? Label(string code) => code switch
    {
        _ when code.StartsWith("183", StringComparison.Ordinal) => "Bombing",
        "185" => "Attempted assassination",
        "186" => "Assassination",
        "190" => "Military force",
        "191" => "Blockade",
        "192" => "Occupation",
        "193" => "Small-arms fighting",
        "194" => "Artillery or tank fire",
        "195" => "Air strike",
        "196" => "Ceasefire violation",
        _ when code.StartsWith("20", StringComparison.Ordinal) && code.Length >= 3 => "Mass violence",
        _ => null
    };

    // One pin per place and kind: the events, articles and sources added up, the newest article
    // linked. Major from 20 articles, moderate from 6.
    public static IReadOnlyList<HazardFeature> Reported(IEnumerable<GdeltEvent> events) =>
        events.GroupBy(e => (Label(e.Code), Math.Round(e.Latitude, 1), Math.Round(e.Longitude, 1)))
            .Select(group =>
            {
                var newest = group.MaxBy(e => e.Seen)!;
                var articles = group.Sum(e => e.Articles);
                var place = newest.Place;
                // 19-11-26 (Grant: say what's going on without opening the link): who against whom, in words.
                var who = group.Where(e => e.Actor1.Length > 0).GroupBy(e => (e.Actor1, e.Actor2)).MaxBy(g => g.Sum(e => e.Articles))?.Key;
                var what = who is { } pair
                    ? $"{group.Key.Item1} by {pair.Actor1}{(pair.Actor2.Length > 0 && pair.Actor2 != pair.Actor1 ? $" against {pair.Actor2}" : "")} in {place}."
                    : $"{group.Key.Item1} in {place}.";
                return new HazardFeature(
                    string.Create(CultureInfo.InvariantCulture, $"gdelt-{group.Key.Item1}-{group.Key.Item2}-{group.Key.Item3}").Replace(' ', '-'),
                    HazardKinds.Conflict, $"{group.Key.Item1} reported - {place}",
                    what + "\n" + string.Create(CultureInfo.InvariantCulture,
                        $"{group.Count()} {(group.Count() == 1 ? "report" : "reports")} in {articles} news {(articles == 1 ? "article" : "articles")} in the last day, machine-read by GDELT and not confirmed. The newest article is linked below."),
                    newest.Latitude, newest.Longitude, articles >= 20 ? "major" : articles >= 6 ? "moderate" : "minor", newest.Url, newest.Seen,
                    NewsQuery(group.Key.Item1, who?.Actor1, who?.Actor2, place));
            })
            .OrderByDescending(f => f.ObservedAt)
            .ToList();

    // The newest monthly candidate file on UCDP's downloads page ("GEDEvent_v26_0_9.csv": release
    // 26, month 9); the quarterly files beside it repeat the same events.
    public static string? NewestCandidateFile(string html) =>
        CandidateLink().Matches(html)
            .Select(m => (Url: m.Groups["url"].Value, Release: int.Parse(m.Groups["release"].Value, CultureInfo.InvariantCulture),
                Month: int.Parse(m.Groups["month"].Value, CultureInfo.InvariantCulture)))
            .OrderByDescending(x => x.Release).ThenByDescending(x => x.Month)
            .Select(x => x.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? x.Url : new Uri(new Uri(UcdpDownloadsUrl), x.Url).AbsoluteUri)
            .FirstOrDefault();

    public sealed record UcdpEvent(string Conflict, string ConflictId, int Violence, string Country, double Latitude, double Longitude,
        DateOnly Start, DateOnly End, int Deaths, string SideA = "", string SideB = "", string Where = "", string Region = "",
        int CivilianDeaths = 0, string Headline = "", string Outlet = "");

    public static IReadOnlyList<UcdpEvent> ParseUcdp(TextReader reader)
    {
        using var rows = ReadCsv(reader).GetEnumerator();
        if (!rows.MoveNext()) return [];
        var header = rows.Current;
        int Column(string name) => Array.IndexOf(header, name);
        var (conflict, conflictId, violence, country, lat, lon, start, end, best) = (Column("conflict_name"), Column("conflict_new_id"),
            Column("type_of_violence"), Column("country"), Column("latitude"), Column("longitude"), Column("date_start"), Column("date_end"), Column("best"));
        if (new[] { conflict, lat, lon, end, best }.Any(i => i < 0)) throw new IOException("UCDP: unexpected columns");
        // What happened, in words (19-11-26): the sides, the place, civilian deaths, and the headline and
        // outlet of the report UCDP coded it from. Optional: an older file without them still loads.
        var (sideA, sideB, where, region, civilians, headline, outlet) = (Column("side_a"), Column("side_b"), Column("where_description"),
            Column("adm_1"), Column("deaths_civilians"), Column("source_headline"), Column("source_office"));
        var events = new List<UcdpEvent>();
        while (rows.MoveNext())
        {
            var f = rows.Current;
            if (f.Length != header.Length
                || !double.TryParse(f[lat], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(f[lon], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                || !DateOnly.TryParse(f[end].Split(' ')[0], CultureInfo.InvariantCulture, out var ended)) continue;
            var started = start >= 0 && DateOnly.TryParse(f[start].Split(' ')[0], CultureInfo.InvariantCulture, out var s) ? s : ended;
            int.TryParse(f[best], NumberStyles.Integer, CultureInfo.InvariantCulture, out var deaths);
            int.TryParse(violence >= 0 ? f[violence] : "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind);
            int.TryParse(civilians >= 0 ? f[civilians] : "", NumberStyles.Integer, CultureInfo.InvariantCulture, out var civilianDeaths);
            string Text(int i) => i >= 0 ? f[i].Trim() : "";
            events.Add(new UcdpEvent(Readable(f[conflict]), conflictId >= 0 ? f[conflictId] : "", kind, country >= 0 ? f[country] : "", latitude, longitude,
                started, ended, deaths, Readable(Text(sideA)), Readable(Text(sideB)), Text(where), Text(region), civilianDeaths,
                First(Text(headline)), First(Text(outlet))));
        }
        return events;
    }

    // The last 90 days of the release, by half-degree cell: deaths added up (UCDP's best estimate),
    // named for the conflict with the most deaths there. Major from 25 deaths, moderate from 5.
    public static IReadOnlyList<HazardFeature> Confirmed(IReadOnlyList<UcdpEvent> events)
    {
        if (events.Count == 0) return [];
        var newest = events.Max(e => e.End);
        var from = newest.AddDays(-(int)ConfirmedSpan.TotalDays);
        return events.Where(e => e.End >= from)
            .GroupBy(e => ((int)Math.Floor(e.Latitude * 2), (int)Math.Floor(e.Longitude * 2)))
            .Select(group =>
            {
                var deaths = group.Sum(e => e.Deaths);
                var main = group.GroupBy(e => (e.Conflict, e.ConflictId)).MaxBy(g => g.Sum(e => e.Deaths) * 1000 + g.Count())!.Key;
                var first = group.Min(e => e.Start);
                var last = group.Max(e => e.End);
                var kinds = string.Join(", ", group.Select(e => e.Violence switch { 1 => "state-based", 2 => "non-state", 3 => "one-sided", _ => null })
                    .Where(k => k is not null).Distinct());
                var latest = group.OrderByDescending(e => e.End).ThenByDescending(e => e.Deaths).First();
                var span = first == last ? $"on {first:dd-MM-yy}" : $"from {first:dd-MM-yy} to {last:dd-MM-yy}";
                var detail = Describe(latest) + "\n" + string.Create(CultureInfo.InvariantCulture,
                    $"Here over the last 90 days: {deaths:N0} {(deaths == 1 ? "death" : "deaths")} in {group.Count()} {(group.Count() == 1 ? "event" : "events")} {span}")
                    + (kinds.Length > 0 ? $" ({kinds} violence)" : string.Empty) + ". Confirmed by UCDP (Uppsala University), which codes events monthly.";
                return new HazardFeature(
                    string.Create(CultureInfo.InvariantCulture, $"ucdp-{group.Key.Item1}-{group.Key.Item2}"), HazardKinds.ConflictConfirmed,
                    $"{main.Conflict}{(group.First().Country is { Length: > 0 } country && !main.Conflict.Contains(country, StringComparison.OrdinalIgnoreCase) ? $" ({country})" : string.Empty)}",
                    detail, group.Average(e => e.Latitude), group.Average(e => e.Longitude),
                    deaths >= 25 ? "major" : deaths >= 5 ? "moderate" : "minor",
                    main.ConflictId.Length > 0 ? $"https://ucdp.uu.se/conflict/{Uri.EscapeDataString(main.ConflictId)}" : "https://ucdp.uu.se/",
                    // UCDP dates have no time: shown as a time they read as the evening before in the
                    // Americas ("AS OF 31-08-26 20:00" for 01-09-26). The dates are in the summary instead.
                    null,
                    NewsQuery(latest.SideA, latest.SideB, latest.Where, latest.Country));
            })
            .OrderByDescending(f => f.Severity switch { "major" => 2, "moderate" => 1, _ => 0 })
            .ToList();
    }

    // One event in words: "05-09-26, Kurin district of Zahedan County (Sistan and Baluchestan province):
    // fighting between the Government of Iran and Jaish al-Adl. 2 killed. Reported as "..." (BBC Monitoring)."
    public static string Describe(UcdpEvent e)
    {
        var place = e.Where.Length > 0
            ? e.Where + (e.Region.Length > 0 && !e.Where.Contains(e.Region, StringComparison.OrdinalIgnoreCase) ? $" ({e.Region})" : "")
            : e.Region.Length > 0 ? e.Region : e.Country;
        var what = (e.Violence, e.SideA.Length > 0, e.SideB.Length > 0) switch
        {
            (3, true, _) => $"{e.SideA} attacked civilians",
            (1 or 2, true, true) => $"fighting between {e.SideA} and {e.SideB}",
            _ => e.Conflict.Length > 0 ? $"violence in the {e.Conflict} conflict" : "political violence",
        };
        var killed = e.Deaths switch
        {
            0 => "No deaths recorded.",
            _ => string.Create(CultureInfo.InvariantCulture, $"{e.Deaths:N0} killed")
                 + (e.CivilianDeaths > 0 ? string.Create(CultureInfo.InvariantCulture, $", {e.CivilianDeaths:N0} of them civilians") : "") + ".",
        };
        var reported = e.Headline.Length > 0
            ? $" Reported as \"{Shorten(e.Headline, 180)}\"{(e.Outlet.Length > 0 ? $" ({e.Outlet})" : "")}."
            : "";
        return string.Create(CultureInfo.InvariantCulture, $"Latest, {e.End:dd-MM-yy}, {place}: ") + what + ". " + killed + reported;
    }

    // UCDP names an armed group nobody has identified "XXX" and a number ("XXX652 - Civilians"):
    // shown as words (19-11-26).
    public static string Readable(string name) =>
        UnidentifiedGroup().Replace(name, "Unidentified armed group");

    [GeneratedRegex(@"\bXXX\d+\b")]
    private static partial Regex UnidentifiedGroup();

    // The words World News is searched with for a pin: the sides and the place, without UCDP's
    // placeholders ("Civilians", an unidentified group), each word once.
    public static string NewsQuery(params string?[] parts)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Civilians", "Unidentified armed group", "Government of" };
        var words = parts.Where(p => !string.IsNullOrWhiteSpace(p) && !skip.Contains(p!.Trim()))
            .SelectMany(p => Regex.Split(p!.Replace("Government of ", "", StringComparison.OrdinalIgnoreCase), @"[\s,;:()\-]+"))
            .Where(w => w.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(' ', words);
    }

    // UCDP lists several sources with ";" between them; the first is enough for the summary.
    private static string First(string list) => list.Split(';', 2)[0].Trim();

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    // GDELT's actor names are upper-case ("ISRAEL", "MILITARY"): shown in title case.
    private static string Actor(string name) =>
        string.IsNullOrWhiteSpace(name) ? "" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Trim().ToLowerInvariant());

    // RFC 4180: quoted fields may hold commas, doubled quotes and line breaks (UCDP's source text does).
    public static IEnumerable<string[]> ReadCsv(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else quoted = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n')
            {
                fields.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                yield return fields.ToArray();
                fields.Clear();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString().TrimEnd('\r'));
            yield return fields.ToArray();
        }
    }

    [GeneratedRegex(@"/(\d{14})\.export\.CSV\.zip")]
    private static partial Regex ExportStamp();

    [GeneratedRegex(@"href=""(?<url>[^""]*candidateged/GEDEvent_v(?<release>\d+)_0_(?<month>\d+)\.csv)""", RegexOptions.IgnoreCase)]
    private static partial Regex CandidateLink();
}
