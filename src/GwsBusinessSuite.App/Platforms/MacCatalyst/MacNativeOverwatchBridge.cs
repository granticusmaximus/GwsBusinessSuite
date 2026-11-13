using System.Globalization;
using System.Text.Json;
using CoreLocation;
using Foundation;
using GwsBusinessSuite.Application.Overwatch;
using MapKit;
using UIKit;
using WebKit;

namespace GwsBusinessSuite.App;

internal sealed class MacNativeOverwatchBridge : WKScriptMessageHandler
{
    public const string Name = "gwsNativeOverwatch";
    private readonly WeakReference<WKWebView> _webView;
    private readonly MKMapView _map;
    private readonly MapDelegate _delegate;
    // Each layer's pins keyed by their payload JSON, so a refresh with mostly the same pins only
    // removes and adds what changed (MapKit relays out every pin on each add/remove).
    private readonly Dictionary<string, Dictionary<string, IMKAnnotation>> _annotations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IMKOverlay>> _overlays = new(StringComparer.Ordinal);
    private CancellationTokenSource? _regionChangeDebounce;
    private bool _pickPointMode;
    private bool _hasInitialRegion;

    private MacNativeOverwatchBridge(WKWebView webView)
    {
        _webView = new(webView);
        _map = new MKMapView(webView.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
            MapType = MKMapType.HybridFlyover,
            ShowsCompass = true,
            ShowsScale = true,
            PitchEnabled = true,
            RotateEnabled = true,
            Hidden = true
        };
        _delegate = new MapDelegate(this);
        _map.Delegate = _delegate;
        webView.Superview?.InsertSubviewBelow(_map, webView);
    }

    public static void Attach(WKWebView webView)
    {
        var controller = webView.Configuration.UserContentController;
        controller.RemoveScriptMessageHandler(Name);
        controller.AddScriptMessageHandler(new MacNativeOverwatchBridge(webView), Name);
    }

    public override void DidReceiveScriptMessage(WKUserContentController controller, WKScriptMessage message)
    {
        if (!IsTrustedSender(message) || message.Body is not NSDictionary body) return;
        var operation = (body["operation"] as NSString)?.ToString();
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 80) return;
        var arguments = body["arguments"] is NSArray array ? array.Select(ToPlainValue).ToArray() : [];
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Apply(operation, arguments);
            }
            catch (Exception ex)
            {
                // A bad payload must not take the app down or fail without a trace.
                Report($"{operation} failed: {ex.GetType().Name}: {ex.Message}", ex.StackTrace);
            }
        });
    }

    // What the map did with each click, and anything that failed, in the app's container
    // (~/Library/Containers/net.gwsapp.businesssuite/Data/Library/gws-overwatch-map.log). The
    // only way to see why a click on the native map did nothing; restarts past 256 KB.
    private static readonly string MapLogPath = Path.Combine(FileSystem.Current.AppDataDirectory, "gws-overwatch-map.log");

    private static void MapLog(string message)
    {
        try
        {
            var file = new FileInfo(MapLogPath);
            if (file.Exists && file.Length > 256 * 1024) file.Delete();
            File.AppendAllText(MapLogPath, $"{DateTime.Now:dd-MM-yy HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // A failure worth fixing: the local log, and the server's log through the page
    // (js/client-errors.js, Origin=mac-app), so it is seen without reading this Mac's disk.
    private void Report(string message, string? stack = null)
    {
        MapLog(message);
        var json = JsonSerializer.Serialize(new { origin = "mac-app", kind = "native", message = "Overwatch map: " + message, stack });
        _ = SendAsync($"window.gwsClientLog && window.gwsClientLog.report({json})");
    }

    private void Apply(string operation, object?[] arguments)
    {
        switch (operation)
        {
            case "init": Show(arguments); return;
            case "layout": Place(arguments); return;
            case "dispose": _pickPointMode = false; ClearAll(); _radarVisible = false; RefreshRadar(); _map.Hidden = true; return;
            case "flyTo":
            case "flyToStreetLevel": FlyTo(arguments); return;
            case "flyToBounds": FlyToBounds(arguments); return;
            case "zoom": Zoom(arguments); return;
            case "zoomBy": ZoomBy(arguments); return;
            case "pan": Pan(arguments); return;
            case "setThreeD": SetThreeD(arguments); return;
            case "setNightLights": SetNightLights(arguments); return;
            case "setSatelliteScene": SetSatelliteScene(arguments); return;
            case "clearSatelliteScene": SetSatelliteScene([]); return;
            case "setMeasure": SetMeasure(arguments); return;
            case "clearMeasure": SetMeasure([]); return;
            case "startPickPoint": _pickPointMode = true; return;
            case "cancelPickPoint": _pickPointMode = false; return;
            case "mapTap": MapTap(arguments); return;
            case "setRadarTimeline": SetRadarTimeline(arguments); return;
            case "setRadarVisible": _radarVisible = arguments.LastOrDefault() is true; RefreshRadar(); return;
            case "home":
                _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D(20, 0), new MKCoordinateSpan(140, 340)), true);
                return;
        }
        if (operation.StartsWith("clear", StringComparison.Ordinal)) { Clear("set" + operation[5..]); return; }
        if (operation.StartsWith("set", StringComparison.Ordinal)) Render(operation, arguments);
    }

    // Puts the map under the page's map container: [x, y, width, height, viewport width, viewport
    // height] in CSS pixels. Sent at init and again whenever the container moves or resizes
    // ('layout', 12-11-26); a stale frame put every click ~30 pt away from the pin drawn there.
    private void Place(object?[] arguments)
    {
        if (!_webView.TryGetTarget(out var webView)) return;
        if (arguments.Length < 6
            || !AsDouble(arguments[0], out var x) || !AsDouble(arguments[1], out var y)
            || !AsDouble(arguments[2], out var width) || !AsDouble(arguments[3], out var height)
            || !AsDouble(arguments[4], out var viewportWidth) || viewportWidth <= 0
            || !AsDouble(arguments[5], out var viewportHeight) || viewportHeight <= 0
            || width <= 0 || height <= 0) return;
        var scaleX = webView.Bounds.Width / viewportWidth;
        var scaleY = webView.Bounds.Height / viewportHeight;
        var frame = new CoreGraphics.CGRect(webView.Frame.X + x * scaleX, webView.Frame.Y + y * scaleY, width * scaleX, height * scaleY);
        if (frame != _map.Frame) _map.Frame = frame;
    }

    private void Show(object?[] arguments)
    {
        if (!_webView.TryGetTarget(out var webView)) return;
        if (_map.Superview is null) webView.Superview?.InsertSubviewBelow(_map, webView);
        _map.Frame = webView.Frame;
        Place(arguments);
        _map.Hidden = false;
        webView.Opaque = false;
        webView.BackgroundColor = UIColor.Clear;
        webView.ScrollView.BackgroundColor = UIColor.Clear;
        _ = webView.EvaluateJavaScriptAsync("document.documentElement.style.setProperty('background-color','transparent','important');document.body.style.setProperty('background-color','transparent','important');");
        if (!_hasInitialRegion)
        {
            _hasInitialRegion = true;
            _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D(20, 0), new MKCoordinateSpan(140, 340)), false);
        }
    }

    private void Render(string operation, object?[] arguments)
    {
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is null) { Clear(operation); return; }
        var items = Items(payload).Select(item => (Key: JsonSerializer.Serialize(item), Item: item)).ToList();
        var signature = string.Join('\n', items.Select(entry => entry.Key));
        // The page re-sends every layer after each move; an unchanged layer costs nothing.
        if (_signatures.TryGetValue(operation, out var last) && last == signature) return;
        _signatures[operation] = signature;

        var previous = _annotations.Remove(operation, out var existing) ? existing : new Dictionary<string, IMKAnnotation>(StringComparer.Ordinal);
        var next = new Dictionary<string, IMKAnnotation>(StringComparer.Ordinal);
        var added = new List<IMKAnnotation>();
        var overlays = new List<IMKOverlay>();
        foreach (var (itemKey, item) in items)
        {
            if (TryCoordinate(item, out var coordinate))
            {
                var key = itemKey;
                while (next.ContainsKey(key)) key += "#";
                if (previous.Remove(key, out var kept))
                {
                    next[key] = kept;
                }
                else
                {
                    var annotation = new NativeAnnotation(operation, item, coordinate)
                    {
                        Title = ReadString(item, "title", "name", "label", "callsign", "code", "id") ?? operation[3..],
                        Subtitle = ReadString(item, "summary", "description", "status", "source", "kind")
                    };
                    next[key] = annotation;
                    added.Add(annotation);
                }
            }
            foreach (var coordinates in Paths(item))
            {
                if (coordinates.Length < 2) continue;
                IMKOverlay overlay = coordinates.Length >= 3 && IsArea(operation)
                    ? MKPolygon.FromCoordinates(coordinates)
                    : MKPolyline.FromCoordinates(coordinates);
                overlays.Add(overlay);
            }
        }
        if (previous.Count > 0) _map.RemoveAnnotations(previous.Values.ToArray());
        if (added.Count > 0) _map.AddAnnotations(added.ToArray());
        if (next.Count > 0) _annotations[operation] = next;

        if (_overlays.Remove(operation, out var oldOverlays) && oldOverlays.Count > 0) _map.RemoveOverlays(oldOverlays.ToArray());
        if (overlays.Count > 0)
        {
            _map.AddOverlays(overlays.ToArray());
            _overlays[operation] = overlays;
        }
    }

    private static IEnumerable<Dictionary<string, object?>> Items(object payload)
    {
        if (payload is Dictionary<string, object?> item) yield return item;
        if (payload is not object?[] items) yield break;
        foreach (var value in items) if (value is Dictionary<string, object?> entry) yield return entry;
    }

    private static bool TryCoordinate(Dictionary<string, object?> item, out CLLocationCoordinate2D coordinate)
    {
        coordinate = default;
        if (!TryDouble(item, out var latitude, "lat", "latitude") || !TryDouble(item, out var longitude, "lon", "lng", "longitude")) return false;
        if (latitude is < -90 or > 90 || longitude is < -180 or > 180) return false;
        coordinate = new CLLocationCoordinate2D(latitude, longitude);
        return true;
    }

    private static IEnumerable<CLLocationCoordinate2D[]> Paths(Dictionary<string, object?> item)
    {
        foreach (var key in new[] { "path", "points", "positions", "polygon", "ring", "coordinates" })
        {
            if (!item.TryGetValue(key, out var value) || value is not object?[] values) continue;
            var coordinates = values.Select(Pair).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            if (coordinates.Length > 1) yield return coordinates;
        }
    }

    private static CLLocationCoordinate2D? Pair(object? value)
    {
        if (value is Dictionary<string, object?> item && TryCoordinate(item, out var coordinate)) return coordinate;
        if (value is not object?[] { Length: >= 2 } pair || !AsDouble(pair[0], out var longitude) || !AsDouble(pair[1], out var latitude)) return null;
        return new CLLocationCoordinate2D(latitude, longitude);
    }

    private void FlyTo(object?[] arguments)
    {
        var offset = arguments.FirstOrDefault() is string ? 1 : 0;
        if (!AsDouble(arguments.ElementAtOrDefault(offset), out var latitude) || !AsDouble(arguments.ElementAtOrDefault(offset + 1), out var longitude)) return;
        var span = AsDouble(arguments.ElementAtOrDefault(offset + 2), out var height) ? Math.Clamp(height / 55_000, 0.002, 160) : 0.08;
        _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D(latitude, longitude), new MKCoordinateSpan(span, span)), true);
    }

    private void FlyToBounds(object?[] arguments)
    {
        var offset = arguments.FirstOrDefault() is string ? 1 : 0;
        if (!AsDouble(arguments.ElementAtOrDefault(offset), out var west) || !AsDouble(arguments.ElementAtOrDefault(offset + 1), out var south)
            || !AsDouble(arguments.ElementAtOrDefault(offset + 2), out var east) || !AsDouble(arguments.ElementAtOrDefault(offset + 3), out var north)) return;
        var delta = east >= west ? east - west : 360 - west + east;
        var longitude = west + delta / 2;
        if (longitude > 180) longitude -= 360;
        _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D((north + south) / 2, longitude),
            new MKCoordinateSpan(Math.Max(0.002, north - south), Math.Max(0.002, delta))), true);
    }

    private void Zoom(object?[] arguments)
    {
        var factor = arguments.LastOrDefault() is true ? 0.5 : 2;
        var region = _map.Region;
        region.Span = new MKCoordinateSpan(Math.Clamp(region.Span.LatitudeDelta * factor, 0.001, 180),
            Math.Clamp(region.Span.LongitudeDelta * factor, 0.001, 360));
        _map.SetRegion(region, true);
    }

    // deltaX/deltaY are fractions of the viewport, summed per frame by the page. Moving the centre
    // (not SetRegion) keeps the zoom exactly as it is; SetRegion re-fits the span on every call.
    private void Pan(object?[] arguments)
    {
        if (!AsDouble(arguments.ElementAtOrDefault(0), out var deltaX)
            || !AsDouble(arguments.ElementAtOrDefault(1), out var deltaY)) return;
        var center = _map.ConvertCoordinate(_map.CenterCoordinate, _map);
        var target = _map.ConvertPoint(new CoreGraphics.CGPoint(
            center.X - deltaX * _map.Bounds.Width,
            center.Y - deltaY * _map.Bounds.Height), _map);
        if (!IsValid(target)) return;
        _map.SetCenterCoordinate(target, false);
    }

    // Smooth wheel/pinch zoom: factor > 1 zooms out. x/y (0-1) is the pointer, which stays put
    // when zoomed in close enough for the flat projection to hold.
    private void ZoomBy(object?[] arguments)
    {
        if (!AsDouble(arguments.ElementAtOrDefault(0), out var factor) || factor <= 0) return;
        factor = Math.Clamp(factor, 0.25, 4);
        var camera = _map.Camera;
        var distance = Math.Clamp(camera.CenterCoordinateDistance * factor, MinimumCameraDistance, MaximumCameraDistance);
        var applied = distance / camera.CenterCoordinateDistance;
        var center = _map.CenterCoordinate;
        if (camera.CenterCoordinateDistance < 5_000_000
            && AsDouble(arguments.ElementAtOrDefault(1), out var x) && AsDouble(arguments.ElementAtOrDefault(2), out var y))
        {
            var anchor = _map.ConvertPoint(new CoreGraphics.CGPoint(
                Math.Clamp(x, 0, 1) * _map.Bounds.Width, Math.Clamp(y, 0, 1) * _map.Bounds.Height), _map);
            if (IsValid(anchor))
            {
                var anchorPoint = MKMapPoint.FromCoordinate(anchor);
                var centerPoint = MKMapPoint.FromCoordinate(center);
                var moved = MKMapPoint.ToCoordinate(new MKMapPoint(
                    anchorPoint.X + (centerPoint.X - anchorPoint.X) * applied,
                    anchorPoint.Y + (centerPoint.Y - anchorPoint.Y) * applied));
                if (IsValid(moved)) center = moved;
            }
        }
        _map.SetCamera(MKMapCamera.CameraLookingAtCenterCoordinate(center, distance, camera.Pitch, camera.Heading), false);
    }

    private const double MinimumCameraDistance = 150;
    private const double MaximumCameraDistance = 40_000_000;

    private static bool IsValid(CLLocationCoordinate2D coordinate) =>
        double.IsFinite(coordinate.Latitude) && double.IsFinite(coordinate.Longitude)
        && coordinate.Latitude is >= -90 and <= 90 && coordinate.Longitude is >= -180 and <= 180;

    private void SetThreeD(object?[] arguments)
    {
        var enabled = arguments.LastOrDefault() is true;
        _map.MapType = enabled ? MKMapType.HybridFlyover : MKMapType.Hybrid;
    }

    private void MapTap(object?[] arguments)
    {
        if (arguments.Length < 2 || !AsDouble(arguments[0], out var x) || !AsDouble(arguments[1], out var y)) return;
        var point = new CoreGraphics.CGPoint(
            Math.Clamp(x, 0, 1) * _map.Bounds.Width,
            Math.Clamp(y, 0, 1) * _map.Bounds.Height);
        var coordinate = _map.ConvertPoint(point, _map);
        if (_pickPointMode)
        {
            _pickPointMode = false;
            var json = JsonSerializer.Serialize(new
            {
                operation = "mapPoint",
                payload = new { lat = coordinate.Latitude, lon = coordinate.Longitude }
            });
            _ = SendAsync($"window.gwsNativeOverwatch && window.gwsNativeOverwatch.selected({json})");
            return;
        }

        if (HitTest(point) is { } view)
        {
            Activate(view.Annotation);
            return;
        }
        var (why, distance) = NearestSkipped(point);
        var line = $"click at {x:0.000},{y:0.000}: no pin within reach{why}";
        // A click right beside a drawn marker was almost certainly meant for it: worth the
        // server's log. A click on open map isn't.
        if (distance <= MissedPinReach) Report(line); else MapLog(line);
    }

    // The pin the user clicked: the drawn marker under the pointer (its balloon sits above the
    // coordinate, so measuring from the coordinate picked neighbours), else the nearest marker
    // within a finger's width. Only drawn views count, so a clustered pin is reached through its
    // cluster, and only layers that open something (NativeOverwatchOperations.Selectable).
    private MKAnnotationView? HitTest(CoreGraphics.CGPoint point)
    {
        const double Reach = 24;
        MKAnnotationView? inside = null;
        var insideDistance = double.PositiveInfinity;
        MKAnnotationView? near = null;
        var nearDistance = Reach * Reach;
        foreach (var view in AnnotationViews(_map))
        {
            if (view.Hidden || view.Alpha < 0.05 || !IsSelectable(view.Annotation)) continue;
            var frame = view.ConvertRectToView(view.Bounds, _map);
            var dx = frame.X + frame.Width / 2 - point.X;
            var dy = frame.Y + frame.Height / 2 - point.Y;
            var distance = dx * dx + dy * dy;
            if (frame.Contains(point))
            {
                if (distance < insideDistance) { inside = view; insideDistance = distance; }
            }
            else if (distance < nearDistance)
            {
                near = view;
                nearDistance = distance;
            }
        }
        return inside ?? near;
    }

    // For the log: the drawn marker nearest a click that opened nothing, and why it was passed
    // over, so a miss names its cause instead of needing another round trip.
    private const double MissedPinReach = 40;

    private (string Why, double Distance) NearestSkipped(CoreGraphics.CGPoint point)
    {
        MKAnnotationView? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var view in AnnotationViews(_map))
        {
            var frame = view.ConvertRectToView(view.Bounds, _map);
            var dx = frame.X + frame.Width / 2 - point.X;
            var dy = frame.Y + frame.Height / 2 - point.Y;
            if (dx * dx + dy * dy < bestDistance) { best = view; bestDistance = dx * dx + dy * dy; }
        }
        if (best is null) return (" (no markers drawn)", double.PositiveInfinity);
        var annotation = best.Annotation;
        var native = AsNative(annotation);
        return ($" (nearest marker {Math.Sqrt(bestDistance):0}pt away: {annotation?.GetType().Name ?? "none"}, " +
            $"native {native?.Class.Name ?? "?"}, cluster {IsCluster(annotation)}, selectable {IsSelectable(annotation)}, " +
            $"hidden {best.Hidden}, alpha {best.Alpha:0.00})", Math.Sqrt(bestDistance));
    }

    private static IEnumerable<MKAnnotationView> AnnotationViews(UIView root)
    {
        foreach (var child in root.Subviews)
        {
            if (child is MKAnnotationView view)
            {
                yield return view;
                continue;
            }
            foreach (var nested in AnnotationViews(child)) yield return nested;
        }
    }

    private bool IsSelectable(IMKAnnotation? annotation) =>
        IsCluster(annotation)
        || (Resolve(annotation) is { } pin && NativeOverwatchOperations.Selectable.Contains(pin.Operation));

    // Opens a pin's window directly rather than through MapKit selection, which ignores a second
    // click on the pin it already has selected. A cluster zooms to its members, unless they can't
    // be split: news stories placed at the same country or city sit on one coordinate, and a
    // cluster still there at street level won't split either. Then the page lists them all to
    // choose from (before 12-11-26 it opened only "the first", and a news pile opened nothing).
    private void Activate(IMKAnnotation? target)
    {
        if (ClusterMembers(target) is { } members)
        {
            if (members.Length == 0) return;
            var latitudes = members.Select(member => member.Coordinate.Latitude).ToList();
            var longitudes = members.Select(member => member.Coordinate.Longitude).ToList();
            var stacked = latitudes.Max() - latitudes.Min() < StackedDegrees && longitudes.Max() - longitudes.Min() < StackedDegrees;
            var closest = _map.Camera.CenterCoordinateDistance <= StreetLevelMetres;
            if (!stacked && !closest)
            {
                MapLog($"cluster of {members.Length}: zooming to its pins");
                _map.ShowAnnotations(members, true);
                return;
            }
            var pins = members.Select(Resolve).OfType<NativeAnnotation>().Where(IsSelectable).ToList();
            MapLog($"cluster of {members.Length} that won't split: {pins.Count} to choose from");
            if (pins.Count == 1)
            {
                Send(pins[0]);
            }
            else if (pins.Count > 1)
            {
                var json = JsonSerializer.Serialize(new
                {
                    items = pins.Take(MaximumChoices).Select(pin => new { operation = pin.Operation, payload = pin.Payload }),
                    total = pins.Count
                });
                // A page from before 12-11-26 has no list; it opens the first, as the app did.
                _ = SendAsync("(function (group) { const native = window.gwsNativeOverwatch; if (!native) return; " +
                    $"if (native.selectedMany) native.selectedMany(group); else native.selected(group.items[0]); }})({json})");
            }
            return;
        }
        if (Resolve(target) is not { } annotation || !IsSelectable(annotation))
        {
            Report($"click on a pin that opens nothing ({target?.GetType().Name ?? "none"})");
            return;
        }
        Send(annotation);
    }

    private void Send(NativeAnnotation annotation)
    {
        MapLog($"opening {annotation.Operation}: {annotation.Title}");
        var json = JsonSerializer.Serialize(new { operation = annotation.Operation, payload = annotation.Payload });
        _ = SendAsync($"window.gwsNativeOverwatch && window.gwsNativeOverwatch.selected({json})");
    }

    // MapKit can hand an annotation back as its own wrapper rather than our NativeAnnotation
    // (a cluster's members especially); match it to ours by its native handle.
    private NativeAnnotation? Resolve(IMKAnnotation? annotation)
    {
        if (annotation is NativeAnnotation native) return native;
        return annotation is NSObject handle ? ResolveHandle(handle.Handle) : null;
    }

    // MapKit's cluster as ours to act on. Since macOS 27 it can reach managed code as a plain
    // IMKAnnotation wrapper rather than MKClusterAnnotation (12-11-26): the type check failed, so
    // clusters drew in MapKit's default red ("7", "+6 more") and a click on one did nothing.
    // Recognise it by its native class and read its members through key-value coding.
    private static readonly ObjCRuntime.Class ClusterClass = new(typeof(MKClusterAnnotation));

    // The wrapper isn't an NSObject (it is a protocol wrapper holding only the native handle), so
    // `annotation is NSObject` failed too and the first fix changed nothing; go through the handle.
    private static NSObject? AsNative(IMKAnnotation? annotation) =>
        annotation as NSObject
        ?? (annotation is ObjCRuntime.INativeObject wrapper && wrapper.Handle != ObjCRuntime.NativeHandle.Zero
            ? ObjCRuntime.Runtime.GetNSObject(wrapper.Handle) : null);

    internal static bool IsCluster(IMKAnnotation? annotation) =>
        annotation is MKClusterAnnotation || (AsNative(annotation) is { } native && native.IsKindOfClass(ClusterClass));

    private static NSArray? MemberArray(IMKAnnotation? annotation) =>
        AsNative(annotation)?.ValueForKey(new NSString("memberAnnotations")) as NSArray;

    private IMKAnnotation[]? ClusterMembers(IMKAnnotation? annotation)
    {
        if (annotation is MKClusterAnnotation cluster) return cluster.MemberAnnotations;
        if (!IsCluster(annotation)) return null;
        if (MemberArray(annotation) is not { } array) return [];
        return (NSArray.FromArray<NSObject>(array) ?? [])
            .Select(member => member as IMKAnnotation ?? ResolveHandle(member.Handle))
            .OfType<IMKAnnotation>().ToArray();
    }

    internal static nuint ClusterCount(IMKAnnotation annotation) =>
        annotation is MKClusterAnnotation cluster ? (nuint)cluster.MemberAnnotations.Length
        : MemberArray(annotation)?.Count ?? 0;

    private NativeAnnotation? ResolveHandle(ObjCRuntime.NativeHandle handle) =>
        _annotations.Values.SelectMany(pins => pins.Values).OfType<NativeAnnotation>().FirstOrDefault(pin => pin.Handle == handle);

    private const double StackedDegrees = 0.0005;
    private const double StreetLevelMetres = 1500;
    private const int MaximumChoices = 30;

    private void Clear(string operation)
    {
        _signatures.Remove(operation);
        if (_annotations.Remove(operation, out var annotations) && annotations.Count > 0) _map.RemoveAnnotations(annotations.Values.ToArray());
        if (_overlays.Remove(operation, out var overlays) && overlays.Count > 0) _map.RemoveOverlays(overlays.ToArray());
    }

    // Radar on the native map (12-11-26; before, the Mac app drew none). The latest observed frame:
    // the whole planet (RainViewer, tiles to zoom 7) with the sharper US layer (NEXRAD) on top, both
    // stretched past their last zoom so the radar stays on screen when you zoom in.
    private bool _radarVisible;
    private string? _radarGlobal;
    private string? _radarRegional;
    private readonly List<IMKOverlay> _radarOverlays = [];
    private string? _radarShownKey;

    private void SetRadarTimeline(object?[] arguments)
    {
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is not Dictionary<string, object?> timeline || timeline.GetValueOrDefault("frames") is not object?[] frames || frames.Length == 0)
        {
            _radarGlobal = _radarRegional = null;
            RefreshRadar();
            return;
        }
        var nowIndex = AsDouble(timeline.GetValueOrDefault("nowIndex"), out var index) ? (int)Math.Clamp(index, 0, frames.Length - 1) : frames.Length - 1;
        var frame = frames[nowIndex] as Dictionary<string, object?>;
        _radarRegional = TileTemplate(frame?.GetValueOrDefault("tileUrlTemplate"));
        _radarGlobal = TileTemplate(frame?.GetValueOrDefault("globalTileUrlTemplate"));
        RefreshRadar();
    }

    // Only https XYZ templates from the server's timeline.
    private static string? TileTemplate(object? value) =>
        value is string text && text.StartsWith("https://", StringComparison.Ordinal) && text.Contains("{z}", StringComparison.Ordinal) ? text : null;

    // Night lights (13-11-26): one NASA GIBS night (or the Black Marble baseline) as a tile layer
    // under the radar; a null payload removes it.
    private MKTileOverlay? _nightLights;

    private void SetNightLights(object?[] arguments)
    {
        if (_nightLights is not null) _map.RemoveOverlay(_nightLights);
        _nightLights = null;
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is not Dictionary<string, object?> spec || spec.GetValueOrDefault("layer") is not string layer
            || spec.GetValueOrDefault("time") is not string time || spec.GetValueOrDefault("tileMatrixSet") is not string matrix
            || !AsDouble(spec.GetValueOrDefault("maxLevel"), out var maxLevel)
            || !layer.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !time.All(c => char.IsAsciiDigit(c) || c == '-')
            || !matrix.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return;
        _nightLights = new StretchedTileOverlay(
            $"https://gibs.earthdata.nasa.gov/wmts/epsg3857/best/{layer}/default/{time}/{matrix}/{{z}}/{{y}}/{{x}}.png", (int)maxLevel);
        _map.AddOverlay(_nightLights, MKOverlayLevel.AboveRoads);
    }

    // Satellite scenes (13-11-26): the chosen Sentinel-2 scene as tiles; the web globe's
    // before/after wipe isn't drawn here, only the scene chosen.
    private MKTileOverlay? _satelliteScene;

    private void SetSatelliteScene(object?[] arguments)
    {
        if (_satelliteScene is not null) _map.RemoveOverlay(_satelliteScene);
        _satelliteScene = null;
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is not Dictionary<string, object?> spec || TileTemplate(spec.GetValueOrDefault("after")) is not { } template
            || !template.StartsWith("https://planetarycomputer.microsoft.com/", StringComparison.Ordinal)) return;
        _satelliteScene = new StretchedTileOverlay(template, 18);
        _map.AddOverlay(_satelliteScene, MKOverlayLevel.AboveRoads);
    }

    // Analyst tools (13-11-26): the measured path and range rings, as polylines of [lat, lon].
    private readonly List<IMKOverlay> _measure = [];

    private void SetMeasure(object?[] arguments)
    {
        if (_measure.Count > 0) _map.RemoveOverlays(_measure.ToArray());
        _measure.Clear();
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is not Dictionary<string, object?> spec || spec.GetValueOrDefault("lines") is not object?[] lines) return;
        foreach (var line in lines.OfType<object?[]>())
        {
            var points = line.OfType<object?[]>()
                .Select(point => point.Length >= 2 && AsDouble(point[0], out var lat) && AsDouble(point[1], out var lon) ? new CLLocationCoordinate2D(lat, lon) : (CLLocationCoordinate2D?)null)
                .OfType<CLLocationCoordinate2D>().ToArray();
            if (points.Length >= 2) _measure.Add(MKPolyline.FromCoordinates(points));
        }
        if (_measure.Count > 0) _map.AddOverlays(_measure.ToArray(), MKOverlayLevel.AboveLabels);
    }

    private void RefreshRadar()
    {
        var key = _radarVisible ? $"{_radarGlobal}|{_radarRegional}" : null;
        if (key == _radarShownKey) return;
        _radarShownKey = key;
        if (_radarOverlays.Count > 0) _map.RemoveOverlays(_radarOverlays.ToArray());
        _radarOverlays.Clear();
        if (!_radarVisible) return;
        if (_radarGlobal is not null) _radarOverlays.Add(new StretchedTileOverlay(_radarGlobal, 7));
        if (_radarRegional is not null) _radarOverlays.Add(new StretchedTileOverlay(_radarRegional, 10));
        foreach (var overlay in _radarOverlays) _map.AddOverlay(overlay, MKOverlayLevel.AboveRoads);
    }

    // An XYZ tile layer that keeps drawing past the zoom its source stops at: deeper tiles are cut
    // from the deepest real one and enlarged, as the web globe does. MapKit would otherwise show
    // nothing past MaximumZ.
    private sealed class StretchedTileOverlay : MKTileOverlay
    {
        private readonly string _template;
        private readonly int _sourceMaxZoom;

        public StretchedTileOverlay(string template, int sourceMaxZoom) : base(template)
        {
            _template = template;
            _sourceMaxZoom = sourceMaxZoom;
            CanReplaceMapContent = false;
            MaximumZ = 20;
        }

        public override void LoadTileAtPath(MKTileOverlayPath path, MKTileOverlayLoadTileCompletionHandler result)
        {
            if (path.Z <= _sourceMaxZoom)
            {
                base.LoadTileAtPath(path, result);
                return;
            }
            var levels = (int)path.Z - _sourceMaxZoom;
            var scale = 1L << levels;
            var parentX = (long)path.X >> levels;
            var parentY = (long)path.Y >> levels;
            var url = _template.Replace("{z}", _sourceMaxZoom.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", parentX.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", parentY.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            NSUrlSession.SharedSession.CreateDataTask(new NSUrl(url), (data, _, error) =>
            {
                try
                {
                    using var image = data is null ? null : UIImage.LoadFromData(data);
                    if (image?.CGImage is not { } source) { result(null!, error!); return; }
                    var size = source.Width / (double)scale;
                    var crop = new CoreGraphics.CGRect(((long)path.X - (parentX << levels)) * size, ((long)path.Y - (parentY << levels)) * size, Math.Max(size, 1), Math.Max(size, 1));
                    using var piece = source.WithImageInRect(crop);
                    if (piece is null) { result(null!, null!); return; }
                    var format = UIGraphicsImageRendererFormat.DefaultFormat;
                    format.Scale = 1;
                    var renderer = new UIGraphicsImageRenderer(new CoreGraphics.CGSize(256, 256), format);
                    using var pieceImage = UIImage.FromImage(piece);
                    result(renderer.CreatePng(_ => pieceImage.Draw(new CoreGraphics.CGRect(0, 0, 256, 256))), null!);
                }
                catch (Exception ex)
                {
                    MapLog($"radar tile {path.Z}/{path.X}/{path.Y} failed: {ex.GetType().Name}: {ex.Message}");
                    result(null!, null!);
                }
            }).Resume();
        }
    }

    private void ClearAll()
    {
        foreach (var operation in _annotations.Keys.Concat(_overlays.Keys).Distinct().ToList()) Clear(operation);
    }


    private Task SendAsync(string script) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (_webView.TryGetTarget(out var webView)) await webView.EvaluateJavaScriptAsync(script);
    });

    private void RegionDidChange(MKCoordinateRegion region)
    {
        _regionChangeDebounce?.Cancel();
        _regionChangeDebounce?.Dispose();
        var debounce = _regionChangeDebounce = new CancellationTokenSource();
        var north = Math.Min(90, region.Center.Latitude + region.Span.LatitudeDelta / 2);
        var south = Math.Max(-90, region.Center.Latitude - region.Span.LatitudeDelta / 2);
        var east = NormalizeLongitude(region.Center.Longitude + region.Span.LongitudeDelta / 2);
        var west = NormalizeLongitude(region.Center.Longitude - region.Span.LongitudeDelta / 2);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, debounce.Token);
                await SendAsync($"window.gwsNativeOverwatch && window.gwsNativeOverwatch.regionChanged({north}, {south}, {east}, {west})");
            }
            catch (OperationCanceledException) { }
        });
    }

    private static bool IsTrustedSender(WKScriptMessage message)
    {
        var frame = message.FrameInfo;
        if (frame is null || !frame.MainFrame) return false;
        var origin = frame.SecurityOrigin;
        var port = origin.Port > 0 ? $":{origin.Port}" : string.Empty;
        return Uri.TryCreate($"{origin.Protocol}://{origin.Host}{port}", UriKind.Absolute, out var uri) && AppEndpoints.IsTrusted(uri);
    }

    private static object? ToPlainValue(NSObject value) => value switch
    {
        NSNull => null, NSString text => text.ToString(), NSNumber number when number.ObjCType == "c" => number.BoolValue,
        NSNumber number => number.DoubleValue,
        NSArray array => array.Select(ToPlainValue).ToArray(),
        NSDictionary dictionary => DictionaryValue(dictionary),
        _ => value.ToString()
    };

    private static Dictionary<string, object?> DictionaryValue(NSDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in dictionary.Keys.OfType<NSObject>())
        {
            var name = key.ToString();
            var value = dictionary[key];
            if (!string.IsNullOrWhiteSpace(name) && value is not null) result[name] = ToPlainValue(value);
        }
        return result;
    }

    private static string? ReadString(Dictionary<string, object?> item, params string[] keys) =>
        keys.Select(key => item.GetValueOrDefault(key)?.ToString()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    private static bool TryDouble(Dictionary<string, object?> item, out double value, params string[] keys)
    {
        foreach (var key in keys) if (item.TryGetValue(key, out var raw) && AsDouble(raw, out value)) return true;
        value = 0; return false;
    }
    private static bool AsDouble(object? value, out double number) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    private static double NormalizeLongitude(double longitude) => (longitude + 540) % 360 - 180;
    private static bool IsArea(string operation) => operation is "setPowerOutages" or "setNewsShading" or "setAurora" or "setMapNotes";

    private sealed class NativeAnnotation(string operation, Dictionary<string, object?> payload, CLLocationCoordinate2D coordinate) : MKPointAnnotation
    {
        public string Operation { get; } = operation;
        public Dictionary<string, object?> Payload { get; } = payload;
        public override CLLocationCoordinate2D Coordinate { get; set; } = coordinate;
    }

    private sealed class MapDelegate(MacNativeOverwatchBridge owner) : MKMapViewDelegate
    {
        public override MKAnnotationView GetViewForAnnotation(MKMapView mapView, IMKAnnotation annotation)
        {
            if (IsCluster(annotation))
            {
                var clusterView = mapView.DequeueReusableAnnotation("gws-cluster") as MKMarkerAnnotationView
                    ?? new MKMarkerAnnotationView(annotation, "gws-cluster");
                clusterView.Annotation = annotation;
                clusterView.CanShowCallout = false;
                clusterView.MarkerTintColor = UIColor.SystemGreen;
                clusterView.GlyphText = ClusterCount(annotation).ToString(CultureInfo.InvariantCulture);
                return clusterView;
            }
            if (annotation is not NativeAnnotation native) return new MKMarkerAnnotationView(annotation, null);
            var view = mapView.DequeueReusableAnnotation("gws-native") as MKMarkerAnnotationView ?? new MKMarkerAnnotationView(annotation, "gws-native");
            view.Annotation = native; view.CanShowCallout = false; view.ClusteringIdentifier = native.Operation;
            view.DisplayPriority = MKFeatureDisplayPriority.DefaultLow;
            view.MarkerTintColor = native.Operation switch
            {
                "setCameraPins" => UIColor.SystemGreen, "setAircraft" => UIColor.SystemCyan,
                "setShips" => UIColor.SystemOrange, "setWorldNews" => UIColor.SystemPurple,
                "setWeatherAlerts" or "setHazards" => UIColor.SystemRed, _ => UIColor.SystemYellow
            };
            view.GlyphText = native.Operation switch { "setCameraPins" => "C", "setAircraft" => "A", "setShips" => "S", "setWorldNews" => "N", _ => "•" };
            return view;
        }

        public override MKOverlayRenderer OverlayRenderer(MKMapView mapView, IMKOverlay overlay) => overlay switch
        {
            MKTileOverlay tiles => new MKTileOverlayRenderer(tiles) { Alpha = 0.75f },
            MKPolygon polygon => new MKPolygonRenderer(polygon) { FillColor = UIColor.SystemOrange.ColorWithAlpha(0.22f), StrokeColor = UIColor.SystemOrange, LineWidth = 2 },
            MKPolyline line => new MKPolylineRenderer(line) { StrokeColor = UIColor.SystemCyan, LineWidth = 3 },
            _ => new MKOverlayRenderer(overlay)
        };

        // Clicks arrive through MapTap; this covers any selection MapKit makes itself. Deselecting
        // straight away means the same pin can be opened again.
        public override void DidSelectAnnotationView(MKMapView mapView, MKAnnotationView view)
        {
            var annotation = view.Annotation;
            if (annotation is not null) mapView.DeselectAnnotation(annotation, false);
            owner.Activate(annotation);
        }

        public override void RegionChanged(MKMapView mapView, bool animated)
        {
            owner.RegionDidChange(mapView.Region);
        }
    }
}
