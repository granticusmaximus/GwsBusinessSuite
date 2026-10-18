using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.CameraIntel;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.LiveLayers;

public sealed record Ship(long Mmsi, string Name, double Latitude, double Longitude, double? SpeedKnots,
    double? CourseDegrees, double? HeadingDegrees, string? ShipType, string? Destination, DateTimeOffset SeenAt);

public sealed record ShipResult(IReadOnlyList<Ship> Ships, string? Note);

// Live ships for the Overwatch globe from aisstream.io (free, needs Overwatch:AisStreamApiKey).
// aisstream is a push-only WebSocket, not a request/response API, so this is a singleton holding one
// shared connection: subscribed to the areas the page has asked about in the last few minutes, it
// keeps each vessel's last known position. Anchored ships only report every few minutes, so a
// snapshot connection per request would miss most of them. The connection closes after a few idle
// minutes and the key never leaves the server.
public sealed class ShipService(string? apiKey, ILogger<ShipService> logger, TimeProvider timeProvider) : IDisposable
{
    private const string StreamUrl = "wss://stream.aisstream.io/v0/stream";
    public const double MaxSquareDegrees = 100;
    public const int MaxShips = 500;
    public const int MaxAreas = 4;
    private static readonly TimeSpan AreaIdle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShipStale = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstDataWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<long, Ship> _ships = new();
    // Static data (name, type, destination) can arrive before or after a position.
    private readonly ConcurrentDictionary<long, (string? Name, string? Type, string? Destination)> _details = new();
    private readonly Dictionary<string, (BoundingBox Box, DateTimeOffset LastAsked)> _areas = [];
    private readonly Lock _gate = new();
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource _subscriptionChanged = NewSignal();
    private string? _connectionProblem;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<ShipResult> GetAsync(BoundingBox bounds, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ShipResult([], "Ships need an aisstream.io key (OVERWATCH_AISSTREAM_API_KEY in the server's .env).");
        if ((bounds.North - bounds.South) * (bounds.East - bounds.West) > MaxSquareDegrees)
            return new ShipResult([], "Zoom in to see ships (about 10° × 10° at a time).");

        var isNewArea = TrackArea(bounds);
        EnsureRunning();
        // A new area has no ships yet: give the stream a moment so the first answer isn't empty.
        if (isNewArea && !InView(bounds).Any())
        {
            try { await Task.Delay(FirstDataWait, timeProvider, ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        var ships = ShipsIn(bounds);
        string? note = ships.Count == 0
            ? _connectionProblem ?? "No ships reporting here yet (positions arrive over the next few minutes)."
            : null;
        return new ShipResult(ships, note);
    }

    // Adds or refreshes an area of interest; true when the subscription has to change.
    private bool TrackArea(BoundingBox bounds)
    {
        // Whole degrees, so small pans reuse the same subscription.
        var box = new BoundingBox(Math.Min(90, Math.Ceiling(bounds.North)), Math.Max(-90, Math.Floor(bounds.South)),
            Math.Min(180, Math.Ceiling(bounds.East)), Math.Max(-180, Math.Floor(bounds.West)));
        var key = $"{box.North}:{box.South}:{box.East}:{box.West}";
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            var isNew = !_areas.ContainsKey(key);
            _areas[key] = (box, now);
            var changed = isNew;
            foreach (var stale in _areas.Where(area => now - area.Value.LastAsked > AreaIdle).Select(area => area.Key).ToList())
                changed |= _areas.Remove(stale);
            while (_areas.Count > MaxAreas)
                changed |= _areas.Remove(_areas.MinBy(area => area.Value.LastAsked).Key);
            if (changed) _subscriptionChanged.TrySetResult();
            return isNew;
        }
    }

    // The freshest ships last heard inside the box (at most MaxShips).
    public IReadOnlyList<Ship> ShipsIn(BoundingBox bounds) =>
        InView(bounds).OrderByDescending(ship => ship.SeenAt).Take(MaxShips).ToList();

    private IEnumerable<Ship> InView(BoundingBox bounds)
    {
        var cutoff = timeProvider.GetUtcNow() - ShipStale;
        return _ships.Values.Where(ship => ship.SeenAt >= cutoff
            && ship.Latitude <= bounds.North && ship.Latitude >= bounds.South
            && ship.Longitude <= bounds.East && ship.Longitude >= bounds.West);
    }

    private void EnsureRunning()
    {
        lock (_gate)
        {
            if (_runCts is not null) return;
            _runCts = new CancellationTokenSource();
            _ = RunAsync(_runCts.Token);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && SubscribedAreas() is { Count: > 0 })
            {
                try
                {
                    await StreamOnceAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Ship layer: aisstream.io connection dropped");
                    _connectionProblem = "aisstream.io didn't answer just now; ships come back shortly.";
                    await Task.Delay(ReconnectDelay, timeProvider, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_gate) { _runCts = null; }
            // Asked about again while closing: start over rather than leave the layer dead.
            if (!ct.IsCancellationRequested && SubscribedAreas().Count > 0) EnsureRunning();
        }
    }

    // One connection: subscribe, read until the areas change (resubscribe on the same socket) or
    // all go idle (close), or the socket drops (the caller reconnects).
    private async Task StreamOnceAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(new Uri(StreamUrl), connectTimeout.Token);

        var areas = SubscribedAreas();
        // aisstream closes the socket unless the subscription arrives within 3 seconds.
        await SendSubscriptionAsync(socket, areas, ct);
        _connectionProblem = null;

        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        var receive = socket.ReceiveAsync(buffer, ct);
        while (true)
        {
            Task changed;
            lock (_gate) { changed = _subscriptionChanged.Task; }
            using var idleCheck = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var idle = Task.Delay(TimeSpan.FromMinutes(1), timeProvider, idleCheck.Token);
            var done = await Task.WhenAny(receive, changed, idle);
            idleCheck.Cancel();

            if (done != receive)
            {
                lock (_gate) { if (_subscriptionChanged.Task.IsCompleted) _subscriptionChanged = NewSignal(); }
                var current = SubscribedAreas();
                if (current.Count == 0)
                {
                    await CloseQuietlyAsync(socket);
                    return;
                }
                if (!current.SequenceEqual(areas))
                {
                    areas = current;
                    await SendSubscriptionAsync(socket, areas, ct);
                }
                continue;
            }

            var result = await receive;
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // aisstream sends its reason (e.g. a bad key) in the close description.
                logger.LogWarning("Ship layer: aisstream.io closed the stream ({Status} {Reason})", result.CloseStatus, result.CloseStatusDescription);
                _connectionProblem = "aisstream.io refused the connection (check the key); retrying shortly.";
                await Task.Delay(ReconnectDelay, timeProvider, ct);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                // A message can be text or binary; both carry UTF-8 JSON.
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                Ingest(text);
            }
            receive = socket.ReceiveAsync(buffer, ct);
        }
    }

    private List<BoundingBox> SubscribedAreas()
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            return _areas.Values.Where(area => now - area.LastAsked <= AreaIdle).Select(area => area.Box).ToList();
        }
    }

    private Task SendSubscriptionAsync(ClientWebSocket socket, IReadOnlyList<BoundingBox> areas, CancellationToken ct)
    {
        var json = BuildSubscription(apiKey!, areas);
        return socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    // Written by hand (no reflection serializer): { APIKey, BoundingBoxes: [[[lat, lon], [lat, lon]]], FilterMessageTypes }.
    public static string BuildSubscription(string key, IReadOnlyList<BoundingBox> areas)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("APIKey", key);
            json.WriteStartArray("BoundingBoxes");
            foreach (var box in areas)
            {
                json.WriteStartArray();
                json.WriteStartArray(); json.WriteNumberValue(box.South); json.WriteNumberValue(box.West); json.WriteEndArray();
                json.WriteStartArray(); json.WriteNumberValue(box.North); json.WriteNumberValue(box.East); json.WriteEndArray();
                json.WriteEndArray();
            }
            json.WriteEndArray();
            json.WriteStartArray("FilterMessageTypes");
            foreach (var type in new[] { "PositionReport", "StandardClassBPositionReport", "ShipStaticData" }) json.WriteStringValue(type);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "idle", timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
    }

    // Applies one aisstream message to the vessel store. Public for tests.
    public void Ingest(string json)
    {
        AisMessage? parsed;
        try { parsed = ParseMessage(json); }
        catch (JsonException) { return; }
        if (parsed is null) return;

        if (parsed.IsStatic)
        {
            _details[parsed.Mmsi] = (parsed.Name, parsed.ShipType, parsed.Destination);
            if (_ships.TryGetValue(parsed.Mmsi, out var known))
                _ships[parsed.Mmsi] = known with
                {
                    Name = string.IsNullOrWhiteSpace(parsed.Name) ? known.Name : parsed.Name,
                    ShipType = parsed.ShipType, Destination = parsed.Destination
                };
            return;
        }
        if (parsed.Latitude is not { } lat || parsed.Longitude is not { } lon) return;
        _details.TryGetValue(parsed.Mmsi, out var details);
        var name = !string.IsNullOrWhiteSpace(details.Name) ? details.Name
            : !string.IsNullOrWhiteSpace(parsed.Name) ? parsed.Name
            : parsed.Mmsi.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ships[parsed.Mmsi] = new Ship(parsed.Mmsi, name, lat, lon, parsed.SpeedKnots, parsed.CourseDegrees,
            parsed.HeadingDegrees, details.Type, details.Destination, timeProvider.GetUtcNow());

        // Bound the store: drop ships nobody has heard from in a while.
        if (_ships.Count > 20_000)
        {
            var cutoff = timeProvider.GetUtcNow() - ShipStale;
            foreach (var stale in _ships.Where(ship => ship.Value.SeenAt < cutoff).Select(ship => ship.Key).ToList())
                _ships.TryRemove(stale, out _);
        }
    }

    public sealed record AisMessage(long Mmsi, bool IsStatic, string? Name, double? Latitude, double? Longitude,
        double? SpeedKnots, double? CourseDegrees, double? HeadingDegrees, string? ShipType, string? Destination);

    // aisstream message: { MessageType, MetaData: { MMSI, ShipName, latitude, longitude }, Message: { <MessageType>: {...} } }.
    public static AisMessage? ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var type = Str(root, "MessageType");
        if (type is null) return null;
        var meta = Prop(root, "MetaData") ?? Prop(root, "Metadata");
        JsonElement? body = Prop(root, "Message") is { } message ? Prop(message, type) : null;

        var mmsi = (meta is { } m ? Num(m, "MMSI") : null) ?? (body is { } b0 ? Num(b0, "UserID") : null);
        if (mmsi is not { } id || id <= 0) return null;
        var metaName = meta is { } mn ? Clean(Str(mn, "ShipName")) : null;

        if (type == "ShipStaticData")
        {
            if (body is not { } s) return null;
            var shipType = Num(s, "Type") is { } code ? ShipTypeName((int)code) : null;
            return new AisMessage((long)id, true, Clean(Str(s, "Name")) ?? metaName, null, null, null, null, null,
                shipType, Clean(Str(s, "Destination")));
        }
        if (type is not ("PositionReport" or "StandardClassBPositionReport") || body is not { } p) return null;

        var lat = Num(p, "Latitude") ?? (meta is { } ml ? Num(ml, "latitude") : null);
        var lon = Num(p, "Longitude") ?? (meta is { } mo ? Num(mo, "longitude") : null);
        // AIS "not available" values: lat 91, lon 181, speed 102.3, course 360, heading 511.
        if (lat is not { } la || lon is not { } lo || Math.Abs(la) > 90 || Math.Abs(lo) > 180 || (la == 0 && lo == 0))
            return null;
        var speed = Num(p, "Sog") is { } sog && sog < 102.2 ? sog : (double?)null;
        var course = Num(p, "Cog") is { } cog && cog < 360 ? cog : (double?)null;
        var heading = Num(p, "TrueHeading") is { } hdg && hdg < 360 ? hdg : (double?)null;
        return new AisMessage((long)id, false, metaName, la, lo, speed, course, heading, null, null);
    }

    // ITU-R M.1371 ship type codes, grouped the way people talk about them.
    public static string? ShipTypeName(int code) => code switch
    {
        30 => "Fishing",
        31 or 32 => "Towing",
        33 => "Dredging",
        35 => "Military",
        36 => "Sailing",
        37 => "Pleasure craft",
        >= 40 and <= 49 => "High-speed craft",
        50 => "Pilot",
        51 => "Search and rescue",
        52 => "Tug",
        55 => "Law enforcement",
        58 => "Medical",
        >= 60 and <= 69 => "Passenger",
        >= 70 and <= 79 => "Cargo",
        >= 80 and <= 89 => "Tanker",
        _ => null
    };

    private static JsonElement? Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string? Str(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double? Num(JsonElement element, string name) =>
        Prop(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var number) ? number : null;

    // AIS pads names with spaces and '@'.
    private static string? Clean(string? text) => text?.Trim().TrimEnd('@').Trim() is { Length: > 0 } cleaned ? cleaned : null;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
            _runCts = null;
        }
    }
}
