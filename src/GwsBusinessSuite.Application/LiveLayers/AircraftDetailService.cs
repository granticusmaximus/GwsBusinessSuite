using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.LiveLayers;

public enum AircraftUse { Unknown, PassengerAirline, Cargo, PrivateCharter, Private, Military }

public sealed record AirportInfo(string Code, string City, double Latitude, double Longitude);

public sealed record TrackPoint(double Latitude, double Longitude, double? AltitudeFeet);

public sealed record RegisteredAircraft(string? Registration, string? Manufacturer, string? Type, string? TypeCode, string? Owner, string? OwnerCode);

public sealed record FlightRoute(string? CallsignIcao, string? CallsignIata, string? AirlineName, string? AirlineIcao,
    AirportInfo? Origin, AirportInfo? Destination);

// Where the flight came from and is going, as far as the evidence goes: each end says where it
// came from ("TRACKED TAKEOFF", "ON FILE", ...), and the note explains anything left out.
public sealed record RoutePlan(AirportInfo? Origin, string? OriginSource, AirportInfo? Destination, string? DestinationSource, string? Note);

public sealed record AircraftDetail(
    string Icao24,
    string Callsign,
    string Use,
    bool IsCommercial,
    string? FlightNumber,
    string? Airline,
    string? Registration,
    string? AircraftType,
    string? TypeCode,
    string? Operator,
    string? TypicalSeats,
    RoutePlan Route,
    IReadOnlyList<TrackPoint> Track);

// What's behind an aircraft on the Overwatch globe: its registration (tail number), type and
// operator from adsbdb.com, the route on file for its flight number (also adsbdb), and the path
// it has actually flown from OpenSky's track endpoint. All three are key-less (verified live
// 2026-10-20). Passenger counts are never broadcast or published per flight, so the closest
// honest figure is the typical seat count for the type. Registered owners of private aircraft
// are left out: on general-aviation planes they're often individuals.
public sealed partial class AircraftDetailService(HttpClient httpClient, IMemoryCache cache, ILogger<AircraftDetailService> logger)
{
    private const string AdsbDbUrl = "https://api.adsbdb.com/v0";
    private const string TrackUrl = "https://opensky-network.org/api/tracks/all";
    private const double EarthRadiusKm = 6371;

    public async Task<AircraftDetail> GetAsync(string icao24, string callsign, double latitude, double longitude, CancellationToken ct = default)
    {
        icao24 = (icao24 ?? string.Empty).Trim().ToLowerInvariant();
        callsign = (callsign ?? string.Empty).Trim().ToUpperInvariant();
        if (!HexPattern().IsMatch(icao24)) icao24 = string.Empty;
        if (!CallsignPattern().IsMatch(callsign)) callsign = string.Empty;

        var aircraftTask = icao24.Length == 0
            ? Task.FromResult<RegisteredAircraft?>(null)
            : CachedAsync($"aircraft-detail:{icao24}", TimeSpan.FromHours(12), $"{AdsbDbUrl}/aircraft/{icao24}", ParseAircraft, ct);
        var routeTask = callsign.Length == 0
            ? Task.FromResult<FlightRoute?>(null)
            : CachedAsync($"aircraft-route:{callsign}", TimeSpan.FromHours(1), $"{AdsbDbUrl}/callsign/{callsign}", ParseRoute, ct);
        var trackTask = icao24.Length == 0 ? Task.FromResult<IReadOnlyList<TrackPoint>>([]) : GetTrackAsync(icao24, ct);
        await Task.WhenAll(aircraftTask, routeTask, trackTask);
        var aircraft = await aircraftTask;
        var route = await routeTask;
        var track = await trackTask;

        // A callsign the route database knows counts as a known operator even when it has no airline name (Flexjet's LXJ).
        var routeOperator = route is null ? null : route.AirlineIcao ?? (AirlineCallsignPattern().Match(callsign) is { Success: true } m ? m.Groups[1].Value : null);
        var use = Classify(icao24, callsign, aircraft?.Registration, aircraft?.TypeCode, routeOperator, aircraft?.Owner, aircraft?.OwnerCode);
        var commercial = use is AircraftUse.PassengerAirline or AircraftUse.Cargo or AircraftUse.PrivateCharter;
        var flightNumber = route?.CallsignIata ?? (commercial && callsign.Length > 0 ? callsign : null);
        if (flightNumber is not null && route?.CallsignIcao is { } icao && icao != flightNumber) flightNumber += $" ({icao})";
        var type = string.Join(' ', new[] { aircraft?.Manufacturer, aircraft?.Type }.Where(s => !string.IsNullOrWhiteSpace(s)));

        return new AircraftDetail(
            icao24, callsign, Describe(use), commercial, flightNumber,
            route?.AirlineName,
            aircraft?.Registration ?? (use is AircraftUse.Private && USRegistrationPattern().IsMatch(callsign) ? callsign : null),
            type.Length > 0 ? type : null,
            aircraft?.TypeCode,
            commercial || use is AircraftUse.Military ? aircraft?.Owner : null,
            use is AircraftUse.PassengerAirline ? TypicalSeatsFor(aircraft?.TypeCode) : null,
            PlanRoute(route, track, latitude, longitude),
            track);
    }

    public static string Describe(AircraftUse use) => use switch
    {
        AircraftUse.PassengerAirline => "COMMERCIAL - PASSENGER AIRLINE",
        AircraftUse.Cargo => "COMMERCIAL - CARGO",
        AircraftUse.PrivateCharter => "PRIVATE - CHARTER / FRACTIONAL",
        AircraftUse.Private => "PRIVATE - GENERAL AVIATION",
        AircraftUse.Military => "MILITARY / GOVERNMENT",
        _ => "NOT IDENTIFIED"
    };

    // Airline flights fly under the airline's 3-letter ICAO code plus a flight number (DAL1536);
    // private flights usually fly under their own registration (N754S). The type then separates an
    // airliner from a business jet flown under a charter or fractional operator's code (NetJets'
    // EJA) or a light aircraft under a flight school's. When the callsign says nothing useful, the
    // owner's airline code on the registration (Delta's DAL) stands in for it.
    public static AircraftUse Classify(string icao24, string callsign, string? registration, string? typeCode, string? airlineIcao,
        string? owner, string? ownerCode = null)
    {
        var prefix = AirlineCallsignPattern().Match(callsign) is { Success: true } m ? m.Groups[1].Value : null;
        if (IsUsMilitaryHex(icao24) || (owner is not null && MilitaryOwnerPattern().IsMatch(owner))
            || (prefix is not null && MilitaryCallsigns.Contains(prefix)))
            return AircraftUse.Military;
        if (callsign.Length > 0 && registration is not null && registration.Replace("-", string.Empty).Equals(callsign, StringComparison.OrdinalIgnoreCase))
            return AircraftUse.Private;
        var operatorCode = airlineIcao ?? prefix ?? (ownerCode is not null && OwnerAirlineCodePattern().IsMatch(ownerCode) ? ownerCode : null);
        if (operatorCode is null)
            return USRegistrationPattern().IsMatch(callsign) || (typeCode is not null && !TypicalSeats.ContainsKey(typeCode))
                ? AircraftUse.Private
                : AircraftUse.Unknown;
        if (CargoOperators.Contains(operatorCode)) return AircraftUse.Cargo;
        if (typeCode is null) return airlineIcao is not null ? AircraftUse.PassengerAirline : AircraftUse.Unknown;
        if (TypicalSeats.ContainsKey(typeCode)) return AircraftUse.PassengerAirline;
        return airlineIcao is not null ? AircraftUse.PrivateCharter : AircraftUse.Private;
    }

    public static string? TypicalSeatsFor(string? typeCode) =>
        typeCode is not null && TypicalSeats.TryGetValue(typeCode, out var seats)
            ? seats.Min == seats.Max ? seats.Min.ToString(CultureInfo.InvariantCulture) : $"{seats.Min}-{seats.Max}"
            : null;

    // Callsign route databases go out of date when airlines renumber flights (in a live sample near
    // Atlanta on 2026-10-20, 8 of 11 routes on file were wrong), so the route on file is checked
    // against what the plane is really doing. A track that starts below 10,000 ft shows where the
    // plane took off: that's the origin, and the destination on file is kept only when the plane is
    // heading for it. Without a tracked takeoff, the plane has to be on the line between the origin
    // and destination on file.
    public static RoutePlan PlanRoute(FlightRoute? route, IReadOnlyList<TrackPoint> track, double latitude, double longitude)
    {
        var fileOrigin = route?.Origin;
        var fileDestination = route?.Destination;
        var onFile = fileOrigin is not null && fileDestination is not null ? $"{fileOrigin.Code} → {fileDestination.Code}" : null;
        var takeoff = track.Count > 0 && track[0].AltitudeFeet < 10_000 ? track[0] : null;

        if (takeoff is not null)
        {
            if (onFile is not null && DistanceKm(takeoff.Latitude, takeoff.Longitude, fileOrigin!.Latitude, fileOrigin.Longitude) <= 80)
                return new RoutePlan(fileOrigin, "CONFIRMED BY TRACKED TAKEOFF", fileDestination, "ON FILE", null);
            var departed = NearestAirport(takeoff.Latitude, takeoff.Longitude);
            var departedSource = departed is null ? null : "TRACKED TAKEOFF";
            if (onFile is null)
                return new RoutePlan(departed, departedSource, null, null, "No route on file for this flight, so where it's going isn't known.");
            return IsHeadingFor(takeoff.Latitude, takeoff.Longitude, latitude, longitude, fileDestination!)
                ? new RoutePlan(departed, departedSource, fileDestination, "ON FILE, HEADING THERE",
                    $"The route on file for this flight number ({onFile}) starts somewhere else, but the plane is heading for {fileDestination!.Code}.")
                : new RoutePlan(departed, departedSource, null, null,
                    $"The route on file for this flight number ({onFile}) doesn't match this plane's track, so it's out of date and where it's going isn't known.");
        }

        if (onFile is null) return new RoutePlan(null, null, null, null, null);
        var routeKm = DistanceKm(fileOrigin!.Latitude, fileOrigin.Longitude, fileDestination!.Latitude, fileDestination.Longitude);
        var viaKm = DistanceKm(fileOrigin.Latitude, fileOrigin.Longitude, latitude, longitude)
            + DistanceKm(latitude, longitude, fileDestination.Latitude, fileDestination.Longitude);
        return viaKm <= routeKm * 1.25 + 150
            && Math.Abs(CrossTrackKm(fileOrigin.Latitude, fileOrigin.Longitude, fileDestination.Latitude, fileDestination.Longitude, latitude, longitude)) <= 300
            ? new RoutePlan(fileOrigin, "ON FILE", fileDestination, "ON FILE",
                "Route on file for this flight number. The plane is on that line, but its takeoff wasn't tracked to confirm it.")
            : new RoutePlan(null, null, null, null,
                $"The route on file for this flight number ({onFile}) doesn't fit where this plane is, so it's out of date.");
    }

    // Nearest large or medium airport within 12 km: far enough to cover the first point OpenSky
    // picks up after takeoff at a big field, close enough not to name the neighbouring one.
    public static AirportInfo? NearestAirport(double latitude, double longitude)
    {
        AirportInfo? nearest = null;
        var nearestKm = 12.0;
        foreach (var airport in Airports.Value)
        {
            if (Math.Abs(airport.Latitude - latitude) > 0.2) continue;
            var km = DistanceKm(latitude, longitude, airport.Latitude, airport.Longitude);
            if (km < nearestKm) (nearest, nearestKm) = (airport, km);
        }
        return nearest;
    }

    public static RegisteredAircraft? ParseAircraft(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("aircraft", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        return new RegisteredAircraft(Text(a, "registration"), Text(a, "manufacturer"), Text(a, "type"), Text(a, "icao_type"),
            Text(a, "registered_owner"), Text(a, "registered_owner_operator_flag_code"));
    }

    public static FlightRoute? ParseRoute(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("flightroute", out var r) || r.ValueKind != JsonValueKind.Object)
            return null;
        var airline = r.TryGetProperty("airline", out var al) && al.ValueKind == JsonValueKind.Object ? al : (JsonElement?)null;
        return new FlightRoute(
            Text(r, "callsign_icao"), Text(r, "callsign_iata"),
            airline is { } x ? Text(x, "name") : null, airline is { } y ? Text(y, "icao") : null,
            Airport(r, "origin"), Airport(r, "destination"));
    }

    // OpenSky track: { path: [[time, latitude, longitude, baro_altitude (m), true_track, on_ground], ...] }.
    public static IReadOnlyList<TrackPoint> ParseTrack(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array) return [];
        var points = new List<TrackPoint>();
        foreach (var p in path.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 6) continue;
            if (Number(p[1]) is not { } lat || Number(p[2]) is not { } lon) continue;
            var feet = p[5].ValueKind == JsonValueKind.True ? 0 : Number(p[3]) * 3.28084;
            points.Add(new TrackPoint(lat, lon, feet));
        }
        return points;
    }

    private async Task<IReadOnlyList<TrackPoint>> GetTrackAsync(string icao24, CancellationToken ct)
    {
        var key = $"aircraft-track:{icao24}";
        if (cache.TryGetValue(key, out IReadOnlyList<TrackPoint>? cached) && cached is not null) return cached;
        if (cache.TryGetValue(AircraftService.LimitKey, out _)) return [];
        try
        {
            using var response = await httpClient.GetAsync($"{TrackUrl}?icao24={icao24}&time=0", ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                cache.Set(AircraftService.LimitKey, true, TimeSpan.FromMinutes(30));
                return [];
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            response.EnsureSuccessStatusCode();
            var track = ParseTrack(await response.Content.ReadAsStringAsync(ct));
            cache.Set(key, track, TimeSpan.FromSeconds(60));
            return track;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Aircraft detail: OpenSky track request failed for {Icao24}", icao24);
            return [];
        }
    }

    // adsbdb answers 404 for an aircraft or callsign it doesn't know; that's remembered like a hit
    // so a popular unknown isn't looked up again on every click. Failures aren't remembered.
    private async Task<T?> CachedAsync<T>(string key, TimeSpan ttl, string url, Func<string, T?> parse, CancellationToken ct) where T : class
    {
        if (cache.TryGetValue(key, out Lookup<T>? cached) && cached is not null) return cached.Value;
        try
        {
            using var response = await httpClient.GetAsync(url, ct);
            T? value = null;
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                response.EnsureSuccessStatusCode();
                value = parse(await response.Content.ReadAsStringAsync(ct));
            }
            cache.Set(key, new Lookup<T>(value), ttl);
            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Aircraft detail: adsbdb request failed for {Url}", url);
            return null;
        }
    }

    private sealed record Lookup<T>(T? Value);

    private static AirportInfo? Airport(JsonElement route, string name)
    {
        if (!route.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Object) return null;
        if (Number(a, "latitude") is not { } lat || Number(a, "longitude") is not { } lon) return null;
        return new AirportInfo(Text(a, "iata_code") ?? Text(a, "icao_code") ?? string.Empty, Text(a, "municipality") ?? string.Empty, lat, lon);
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? Number(v) : null;

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    // US military aircraft use the top of the US address block (ADF7C8-AFFFFF); civil N-numbers sit below it.
    private static bool IsUsMilitaryHex(string icao24) =>
        int.TryParse(icao24, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex is >= 0xADF7C8 and <= 0xAFFFFF;

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static double CrossTrackKm(double fromLat, double fromLon, double toLat, double toLon, double lat, double lon)
    {
        var d13 = DistanceKm(fromLat, fromLon, lat, lon) / EarthRadiusKm;
        var bearingDifference = Bearing(fromLat, fromLon, lat, lon) - Bearing(fromLat, fromLon, toLat, toLon);
        return Math.Asin(Math.Sin(d13) * Math.Sin(bearingDifference)) * EarthRadiusKm;
    }

    // Heading for an airport: at least 30 km flown from the takeoff point, ahead along and near the
    // great circle from there to the airport, and closer to it than at takeoff.
    private static bool IsHeadingFor(double fromLat, double fromLon, double lat, double lon, AirportInfo to)
    {
        var flownKm = DistanceKm(fromLat, fromLon, lat, lon);
        var totalKm = DistanceKm(fromLat, fromLon, to.Latitude, to.Longitude);
        if (flownKm < 30 || totalKm < 1) return false;
        var bearingDifference = Bearing(fromLat, fromLon, lat, lon) - Bearing(fromLat, fromLon, to.Latitude, to.Longitude);
        return Math.Cos(bearingDifference) > 0
            && Math.Abs(CrossTrackKm(fromLat, fromLon, to.Latitude, to.Longitude, lat, lon)) <= Math.Max(100, totalKm * 0.1)
            && DistanceKm(lat, lon, to.Latitude, to.Longitude) < totalKm;
    }

    // OurAirports' large and medium airports with an IATA code (airports.csv, embedded).
    private static readonly Lazy<AirportInfo[]> Airports = new(() =>
    {
        using var stream = typeof(AircraftDetailService).Assembly.GetManifestResourceStream("GwsBusinessSuite.Application.LiveLayers.airports.csv")
            ?? throw new InvalidOperationException("airports.csv isn't embedded in the Application assembly.");
        using var reader = new StreamReader(stream);
        var airports = new List<AirportInfo>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(',');
            airports.Add(new AirportInfo(f[0], f[1], double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture)));
        }
        return [.. airports];
    });

    private static double Bearing(double lat1, double lon1, double lat2, double lon2)
    {
        var dLon = ToRadians(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(ToRadians(lat2));
        var x = Math.Cos(ToRadians(lat1)) * Math.Sin(ToRadians(lat2)) - Math.Sin(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Cos(dLon);
        return Math.Atan2(y, x);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    [GeneratedRegex("^[0-9a-f]{6}$")]
    private static partial Regex HexPattern();

    [GeneratedRegex("^[A-Z0-9]{2,8}$")]
    private static partial Regex CallsignPattern();

    [GeneratedRegex("^([A-Z]{3})[0-9][0-9A-Z]{0,4}$")]
    private static partial Regex AirlineCallsignPattern();

    // adsbdb fills the owner's flag code with the type code (G650) when the owner isn't an airline.
    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex OwnerAirlineCodePattern();

    [GeneratedRegex("^N[1-9][0-9A-Z]{0,4}$")]
    private static partial Regex USRegistrationPattern();

    [GeneratedRegex(@"\b(air force|navy|army|marine corps|coast guard|national guard|department of defen[cs]e|ministry of defen[cs]e|military)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MilitaryOwnerPattern();

    // Common military and state callsign prefixes (USAF Reach, US Navy, US Army, Special Air Mission, Canada, UK, Germany, France, Italy, Belgium).
    private static readonly FrozenSet<string> MilitaryCallsigns =
        new[] { "RCH", "CNV", "PAT", "SAM", "CFC", "RRR", "GAF", "FAF", "IAM", "BAF" }.ToFrozenSet();

    // ICAO codes of the main all-cargo airlines.
    private static readonly FrozenSet<string> CargoOperators = new[]
    {
        "FDX", "UPS", "GTI", "ABX", "ATN", "CKS", "CLX", "BOX", "GEC", "CAO", "PAC", "NCA", "AJT", "DHK", "DHX", "BCS",
        "LCO", "KYE", "WGN", "SOO", "AMF", "MTN", "CKK", "ICV", "MPH", "CFS"
    }.ToFrozenSet();

    // Typical seat counts for the types airlines fly passengers on (the range covers the common
    // cabin layouts; a given airline's layout sits somewhere inside it).
    private static readonly FrozenDictionary<string, (int Min, int Max)> TypicalSeats = new Dictionary<string, (int, int)>
    {
        ["A318"] = (107, 132), ["A319"] = (124, 156), ["A19N"] = (124, 160), ["A320"] = (150, 186), ["A20N"] = (150, 194),
        ["A321"] = (185, 236), ["A21N"] = (180, 244), ["A332"] = (210, 290), ["A333"] = (250, 335), ["A338"] = (220, 260),
        ["A339"] = (260, 300), ["A343"] = (250, 300), ["A346"] = (300, 380), ["A359"] = (300, 350), ["A35K"] = (350, 410),
        ["A388"] = (480, 575), ["BCS1"] = (100, 135), ["BCS3"] = (120, 160),
        ["B712"] = (106, 117), ["B733"] = (126, 149), ["B734"] = (146, 168), ["B735"] = (108, 132), ["B736"] = (108, 130),
        ["B737"] = (126, 149), ["B738"] = (162, 189), ["B739"] = (177, 220), ["B37M"] = (138, 172), ["B38M"] = (162, 210),
        ["B39M"] = (178, 220), ["B3XM"] = (188, 230), ["B744"] = (366, 416), ["B748"] = (410, 467), ["B752"] = (200, 239),
        ["B753"] = (243, 280), ["B762"] = (181, 224), ["B763"] = (211, 269), ["B764"] = (245, 304), ["B772"] = (300, 400),
        ["B77L"] = (300, 320), ["B77W"] = (350, 396), ["B779"] = (400, 426), ["B788"] = (230, 250), ["B789"] = (250, 300),
        ["B78X"] = (300, 330),
        ["CRJ1"] = (50, 50), ["CRJ2"] = (50, 50), ["CRJ7"] = (65, 78), ["CRJ9"] = (76, 90), ["CRJX"] = (97, 104),
        ["E135"] = (37, 37), ["E145"] = (50, 50), ["E170"] = (70, 78), ["E75L"] = (76, 88), ["E75S"] = (76, 88),
        ["E190"] = (96, 114), ["E195"] = (100, 124), ["E290"] = (96, 114), ["E295"] = (120, 146),
        ["DH8A"] = (37, 39), ["DH8B"] = (37, 40), ["DH8C"] = (50, 56), ["DH8D"] = (68, 90),
        ["AT43"] = (46, 50), ["AT45"] = (46, 50), ["AT72"] = (68, 74), ["AT75"] = (68, 74), ["AT76"] = (68, 78),
        ["SF34"] = (30, 34), ["D328"] = (30, 33), ["B190"] = (19, 19), ["C208"] = (9, 12), ["C402"] = (8, 9),
        ["SU95"] = (87, 103), ["C919"] = (158, 192)
    }.ToFrozenDictionary();
}
