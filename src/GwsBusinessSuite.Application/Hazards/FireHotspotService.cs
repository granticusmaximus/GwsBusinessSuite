using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.Hazards;

// Fire hotspots for Overwatch (07-11-26): NASA FIRMS's public 24-hour files, which need no key
// (its API does). Every satellite heat detection worldwide in the last day, from VIIRS on NOAA-20
// and MODIS: about 130,000 on 07-11-26 (Suomi NPP's file was empty that day, so it's left out).
// They're grouped into half-degree cells, so the globe draws a few thousand pins rather than every
// detection. Low-confidence detections are dropped. A cell's strength is its fire radiative power
// (MW): field burning is a scatter of weak detections, a big wildfire a cluster of strong ones.
public sealed class FireHotspotService(HttpClient httpClient, IMemoryCache cache, ILogger<FireHotspotService> logger)
{
    private const string CacheKey = "hazards:firms";
    public static readonly IReadOnlyList<(string Name, string Url)> Files =
    [
        ("NOAA-20 VIIRS", "https://firms.modaps.eosdis.nasa.gov/data/active_fire/noaa-20-viirs-c2/csv/J1_VIIRS_C2_Global_24h.csv"),
        ("MODIS", "https://firms.modaps.eosdis.nasa.gov/data/active_fire/modis-c6.1/csv/MODIS_C6_1_Global_24h.csv"),
    ];
    public const double CellDegrees = 0.5;

    public async Task<IReadOnlyList<HazardFeature>> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<HazardFeature>? cached) && cached is not null) return cached;
        var cells = new Dictionary<(int, int), Cell>();
        var read = new List<string>();
        foreach (var (name, url) in Files)
        {
            try
            {
                await using var stream = await httpClient.GetStreamAsync(url, ct);
                using var reader = new StreamReader(stream);
                await AddAsync(reader, name, cells, ct);
                read.Add(name);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "Fire hotspot layer: FIRMS {File} failed", name);
            }
        }
        if (read.Count == 0) return [];
        var features = ToFeatures(cells.Values);
        cache.Set(CacheKey, features, TimeSpan.FromHours(2));
        return features;
    }

    // One FIRMS CSV (VIIRS or MODIS columns) into the cells. VIIRS confidence is l/n/h, MODIS 0-100.
    public static async Task AddAsync(TextReader reader, string source, Dictionary<(int, int), Cell> cells, CancellationToken ct = default)
    {
        var header = await reader.ReadLineAsync(ct);
        if (header is null) return;
        var columns = header.Split(',');
        int Column(string name) => Array.IndexOf(columns, name);
        var (lat, lon, date, time, confidence, frp) = (Column("latitude"), Column("longitude"), Column("acq_date"), Column("acq_time"),
            Column("confidence"), Column("frp"));
        if (lat < 0 || lon < 0 || frp < 0) throw new InvalidDataException($"FIRMS {source}: unexpected columns");
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var fields = line.Split(',');
            if (fields.Length != columns.Length) continue;
            var level = confidence >= 0 ? fields[confidence] : string.Empty;
            if (level is "l" || (int.TryParse(level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) && percent < 30)) continue;
            if (!double.TryParse(fields[lat], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.TryParse(fields[lon], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)) continue;
            var power = double.TryParse(fields[frp], NumberStyles.Float, CultureInfo.InvariantCulture, out var mw) ? mw : 0;
            DateTimeOffset? seen = date >= 0 && time >= 0 && DateTime.TryParseExact($"{fields[date]} {fields[time].PadLeft(4, '0')}", "yyyy-MM-dd HHmm",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                ? new DateTimeOffset(at, TimeSpan.Zero) : null;
            var key = ((int)Math.Floor(latitude / CellDegrees), (int)Math.Floor(longitude / CellDegrees));
            if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new Cell();
            cell.Add(latitude, longitude, power, seen, source);
        }
    }

    // A cell is major from 1,000 MW in all or 100 detections, moderate from 200 MW or 20. Strongest
    // first, so a view with too many to draw keeps the fiercest.
    public static IReadOnlyList<HazardFeature> ToFeatures(IEnumerable<Cell> cells) =>
        cells.OrderByDescending(cell => cell.Power).Select(cell =>
        {
            var latitude = cell.LatitudeSum / cell.Count;
            var longitude = cell.LongitudeSum / cell.Count;
            var severity = cell.Power >= 1000 || cell.Count >= 100 ? "major" : cell.Power >= 200 || cell.Count >= 20 ? "moderate" : "minor";
            var detail = string.Join(" · ", new[]
            {
                string.Create(CultureInfo.InvariantCulture, $"{cell.Power:N0} MW of fire radiative power, the strongest {cell.Strongest:N0} MW"),
                cell.Latest is { } latest ? string.Create(CultureInfo.InvariantCulture, $"latest {latest:HH:mm} UTC") : null,
                $"NASA FIRMS ({string.Join(", ", cell.Sources.Order(StringComparer.Ordinal))})"
            }.Where(part => part is not null));
            return new HazardFeature(
                string.Create(CultureInfo.InvariantCulture, $"hotspot-{Math.Floor(latitude / CellDegrees)}-{Math.Floor(longitude / CellDegrees)}"),
                HazardKinds.Hotspot,
                string.Create(CultureInfo.InvariantCulture, $"Fire hotspots: {cell.Count:N0} {(cell.Count == 1 ? "detection" : "detections")} in the last day"),
                detail, latitude, longitude, severity,
                string.Create(CultureInfo.InvariantCulture, $"https://firms.modaps.eosdis.nasa.gov/map/#d:24hrs;@{longitude:0.00},{latitude:0.00},9.0z"),
                cell.Latest);
        }).ToList();

    public sealed class Cell
    {
        public int Count { get; private set; }
        public double LatitudeSum { get; private set; }
        public double LongitudeSum { get; private set; }
        public double Power { get; private set; }
        public double Strongest { get; private set; }
        public DateTimeOffset? Latest { get; private set; }
        public HashSet<string> Sources { get; } = new(StringComparer.Ordinal);

        public void Add(double latitude, double longitude, double power, DateTimeOffset? seen, string source)
        {
            Count++;
            LatitudeSum += latitude;
            LongitudeSum += longitude;
            Power += power;
            Strongest = Math.Max(Strongest, power);
            if (seen is { } at && (Latest is null || at > Latest)) Latest = at;
            Sources.Add(source);
        }
    }
}
