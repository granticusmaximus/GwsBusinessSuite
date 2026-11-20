using System.Globalization;
using System.Diagnostics;
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
    private const int MaximumNativeCameraPins = 750;
    private readonly WeakReference<WKWebView> _webView;
    private readonly MKMapView _map;
    private readonly MapDelegate _delegate;
    // Each layer's pins keyed by their payload JSON, so a refresh with mostly the same pins only
    // removes and adds what changed (MapKit relays out every pin on each add/remove).
    private readonly Dictionary<string, Dictionary<string, IMKAnnotation>> _annotations = new(StringComparer.Ordinal);
    private readonly Dictionary<ObjCRuntime.NativeHandle, NativeAnnotation> _annotationsByHandle = [];
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IMKOverlay>> _overlays = new(StringComparer.Ordinal);
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, (string Operation, object?[] Arguments)> _pendingLayers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _scheduledLayers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _regionChangeDebounce;
    private bool _pickPointMode;
    private bool _hasInitialRegion;
    private readonly HoloStreetOverlay _streets = new();
    private const double GroupCellsAcross = 14;
    private readonly Dictionary<string, object?[]> _ungrouped = new(StringComparer.Ordinal);
    private double _groupedAtSpan;
    private IReadOnlySet<string>? _focus;

    // A zoom of more than half again either way regroups the crowded layers.
    internal void RegroupIfZoomed()
    {
        var span = _map.Region.Span.LongitudeDelta;
        if (_groupedAtSpan <= 0 || (span < _groupedAtSpan * 1.5 && span > _groupedAtSpan / 1.5)) return;
        foreach (var (operation, arguments) in _ungrouped.ToList())
        {
            _signatures.Remove(operation);
            Render(operation, arguments);
        }
    }

    // Focus one layer (OverwatchFocus): the rest dim to a quarter, pins and areas alike.
    private void SetFocus(object?[] arguments)
    {
        _focus = NativeShapes.FocusOperations(arguments);
        foreach (var view in AnnotationViews(_map))
            if (Resolve(view.Annotation) is NativeAnnotation native) view.Alpha = FocusAlpha(native.Operation);
        foreach (var (operation, overlays) in _overlays)
            foreach (var overlay in overlays)
                if (_map.RendererForOverlay(overlay) is { } renderer) { renderer.Alpha = FocusAlpha(operation); renderer.SetNeedsDisplay(); }
        MapLog(_focus is null ? "focus ended" : $"focus: {string.Join(", ", _focus)}");
    }

    internal nfloat FocusAlpha(string operation) => _focus is null || _focus.Contains(operation) ? 1 : (nfloat)OverwatchFocus.DimAlpha;

    private MKOverlayRenderer? _streetRenderer;
    private MKOverlayRenderer? _landRenderer;

    // The land the web globe draws, from the same file on the same server.
    private async Task LoadHoloLandAsync()
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(AppEndpoints.BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
            var json = await http.GetStringAsync(OverwatchMapLook.LandPath.TrimStart('/'));
            var overlays = HoloMap.Land(json);
            UIApplication.SharedApplication.InvokeOnMainThread(() =>
            {
                foreach (var overlay in overlays) _map.InsertOverlayBelow(overlay, _streets);
                UpdateHolo();
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            MapLog($"holo land not loaded: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Streets fade in as the camera comes down, and the land fill fades out over them, at the same
    // heights as the web globe (OverwatchMapLook.StreetShare).
    internal void UpdateHolo()
    {
        var share = OverwatchMapLook.StreetShare(_map.Camera.CenterCoordinateDistance);
        if (_streetRenderer is not null) { _streetRenderer.Alpha = (nfloat)(share * OverwatchMapLook.StreetAlpha); _streetRenderer.SetNeedsDisplay(); }
        if (_landRenderer is not null) { _landRenderer.Alpha = share >= 0.98 ? 0 : 1; _landRenderer.SetNeedsDisplay(); }
    }

    internal MKOverlayRenderer? HoloRenderer(IMKOverlay overlay)
    {
        if (HoloMap.Renderer(overlay) is not { } renderer) return null;
        if (overlay is HoloStreetOverlay) _streetRenderer = renderer;
        if (overlay is MKMultiPolygon { Title: HoloMap.LandTitle }) _landRenderer = renderer;
        UpdateHolo();
        return renderer;
    }

    // The web globe's colours (NativeShapes.ToneHex).
    internal static UIColor Tone(NativeShapes.MarkerTone tone) => Hex(NativeShapes.ToneHex(tone));

    internal static UIColor Hex(string hex) => UIColor.FromRGB(
        Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));

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
        // Holo Wireframe, identical to the web globe (19-11-26; HoloMap, OverwatchMapLook). Apple's
        // globe stays underneath (round when zoomed out) behind an opaque veil above its labels;
        // the grid, then the land and coastlines once loaded, and the street map go on top.
        _map.OverrideUserInterfaceStyle = UIUserInterfaceStyle.Dark;
        _map.PointOfInterestFilter = MKPointOfInterestFilter.FilterExcludingAllCategories;
        _map.AddOverlay(HoloMap.Veil(), MKOverlayLevel.AboveLabels);
        _map.AddOverlay(_streets, MKOverlayLevel.AboveLabels);
        _map.AddOverlay(HoloMap.Grid(), MKOverlayLevel.AboveLabels);
        _ = LoadHoloLandAsync();
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
        QueueApply(operation, arguments);
    }

    // Layer refreshes can arrive in bursts when several server calls finish together or a moving
    // source ticks while the previous revision is waiting for MapKit. Keep only the newest command
    // for that layer. Navigation and pointer commands are never coalesced.
    private void QueueApply(string operation, object?[] arguments)
    {
        var layer = operation.StartsWith("clear", StringComparison.Ordinal) ? "set" + operation[5..]
            : operation.StartsWith("set", StringComparison.Ordinal) ? operation : null;
        if (layer is null)
        {
            MainThread.BeginInvokeOnMainThread(() => ApplySafely(operation, arguments));
            return;
        }
        lock (_pendingLock)
        {
            _pendingLayers[layer] = (operation, arguments);
            if (!_scheduledLayers.Add(layer)) return;
        }
        MainThread.BeginInvokeOnMainThread(() =>
        {
            (string Operation, object?[] Arguments) pending;
            lock (_pendingLock)
            {
                if (!_pendingLayers.Remove(layer, out pending)) { _scheduledLayers.Remove(layer); return; }
                _scheduledLayers.Remove(layer);
            }
            ApplySafely(pending.Operation, pending.Arguments);
        });
    }

    private void ApplySafely(string operation, object?[] arguments)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            Apply(operation, arguments);
        }
        catch (Exception ex)
        {
            // A bad payload must not take the app down or fail without a trace.
            Report($"{operation} failed: {ex.GetType().Name}: {ex.Message}", ex.StackTrace);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= TimeSpan.FromMilliseconds(50))
            {
                var count = NativeShapes.Items(NativeShapes.Payload(operation, arguments)).Count();
                MapLog($"slow apply {operation}: {elapsed.TotalMilliseconds:0} ms, {count} item(s)");
            }
        }
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
            case NativeOverwatchOperations.FocusLayer: SetFocus(arguments); return;
            case "clearSatelliteScene": SetSatelliteScene([]); return;
            case "setMeasure": SetMeasure(arguments); return;
            case "clearMeasure": SetMeasure([]); return;
            case "startPickPoint": _pickPointMode = true; return;
            case "cancelPickPoint": _pickPointMode = false; return;
            case "mapTap": MapTap(arguments); return;
            case "setRadarTimeline": SetRadarTimeline(arguments); return;
            case "setTimeMachine": SetTimeMachine(arguments); return;
            case "setAurora": SetAurora(arguments); return;
            case "setTrafficFlow": SetTrafficFlow(arguments); return;
            case "clearTimeMachine": SetTimeMachine([]); return;
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
        // NativeShapes (shared, tested) reads every payload: points, lines, rings and rectangles.
        var payload = NativeShapes.Payload(operation, arguments);
        if (payload is null) { Clear(operation); return; }
        var shapedItems = NativeShapes.Items(payload);
        if (operation == "setCameraPins") shapedItems = shapedItems.Take(MaximumNativeCameraPins);
        // Crowds become counts, regrouped when the zoom changes (NativeShapes.Group, Holo Wireframe).
        if (NativeShapes.Groupable.Contains(operation))
        {
            _ungrouped[operation] = arguments;
            var span = _map.Region.Span;
            _groupedAtSpan = span.LongitudeDelta;
            shapedItems = NativeShapes.Group(shapedItems.ToList(), span.LongitudeDelta / GroupCellsAcross, span.LatitudeDelta / GroupCellsAcross, NativeShapes.GroupNoun(operation));
        }
        var items = shapedItems.Select(item =>
            (Key: NativeShapes.Identity(operation, item), Fingerprint: JsonSerializer.Serialize(item), Item: item)).ToList();
        var signature = string.Join('\n', items.Select(entry => entry.Fingerprint));
        // The page re-sends every layer after each move; an unchanged layer costs nothing.
        if (_signatures.TryGetValue(operation, out var last) && last == signature) return;
        _signatures[operation] = signature;

        var previous = _annotations.Remove(operation, out var existing) ? existing : new Dictionary<string, IMKAnnotation>(StringComparer.Ordinal);
        var next = new Dictionary<string, IMKAnnotation>(StringComparer.Ordinal);
        var added = new List<IMKAnnotation>();
        var overlays = new List<IMKOverlay>();
        var polygons = new List<MKPolygon>();
        var lines = new List<MKPolyline>();
        foreach (var (itemKey, _, item) in items)
        {
            if (TryCoordinate(item, out var coordinate))
            {
                var key = itemKey;
                while (next.ContainsKey(key)) key += "#";
                // A pin whose symbol changes (a camera found not working, say) is replaced: MapKit
                // doesn't restyle a view it has already drawn.
                if (previous.TryGetValue(key, out var kept) && kept is NativeAnnotation native
                    && NativeShapes.Marker(operation, native.Payload) == NativeShapes.Marker(operation, item)
                    && Face(native.Payload) == Face(item))
                {
                    previous.Remove(key);
                    native.Update(item, coordinate);
                    next[key] = native;
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
                    _annotationsByHandle[annotation.Handle] = annotation;
                }
            }
            foreach (var coordinates in NativeShapes.Paths(item).Select(path => path.Select(point => new CLLocationCoordinate2D(point.Lat, point.Lon)).ToArray()))
            {
                if (coordinates.Length < 2) continue;
                if (coordinates.Length >= 3 && NativeShapes.IsArea(operation)) polygons.Add(MKPolygon.FromCoordinates(coordinates));
                else lines.Add(MKPolyline.FromCoordinates(coordinates));
            }
        }
        // One overlay per layer for its areas and one for its lines: city traffic alone sends 4,000
        // road pieces, and MapKit draws a multi-shape far faster than thousands of overlays.
        if (polygons.Count > 0) overlays.Add(new MKMultiPolygon(polygons.ToArray()) { Title = operation });
        if (lines.Count > 0) overlays.Add(new MKMultiPolyline(lines.ToArray()) { Title = operation });
        if (previous.Count > 0)
        {
            _map.RemoveAnnotations(previous.Values.ToArray());
            foreach (var removed in previous.Values.OfType<NativeAnnotation>()) _annotationsByHandle.Remove(removed.Handle);
        }
        if (added.Count > 0) _map.AddAnnotations(added.ToArray());
        if (next.Count > 0) _annotations[operation] = next;

        if (_overlays.Remove(operation, out var oldOverlays) && oldOverlays.Count > 0) _map.RemoveOverlays(oldOverlays.ToArray());
        if (overlays.Count > 0)
        {
            _map.AddOverlays(overlays.ToArray());
            _overlays[operation] = overlays;
        }
    }

    private static bool TryCoordinate(Dictionary<string, object?> item, out CLLocationCoordinate2D coordinate)
    {
        var ok = NativeShapes.TryPoint(item, out var latitude, out var longitude);
        coordinate = ok ? new CLLocationCoordinate2D(latitude, longitude) : default;
        return ok;
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
        // A count disc zooms to the pins it stands for.
        if (Resolve(target) is NativeAnnotation { } group && group.Payload.TryGetValue("groupBounds", out var bounds) && bounds is object?[] { Length: 4 } box
            && AsDouble(box[0], out var south) && AsDouble(box[1], out var west) && AsDouble(box[2], out var north) && AsDouble(box[3], out var east))
        {
            var pad = Math.Max(0.002, Math.Max(north - south, east - west) * 0.25);
            _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D((north + south) / 2, (east + west) / 2),
                new MKCoordinateSpan(Math.Min(170, north - south + pad * 2), Math.Min(350, east - west + pad * 2))), true);
            MapLog("count disc: zooming to its pins");
            return;
        }
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
        _annotationsByHandle.GetValueOrDefault(handle);

    private const double StackedDegrees = 0.0005;
    private const double StreetLevelMetres = 1500;
    private const int MaximumChoices = 30;

    private void Clear(string operation)
    {
        _signatures.Remove(operation);
        if (_annotations.Remove(operation, out var annotations) && annotations.Count > 0)
        {
            _map.RemoveAnnotations(annotations.Values.ToArray());
            foreach (var annotation in annotations.Values.OfType<NativeAnnotation>()) _annotationsByHandle.Remove(annotation.Handle);
        }
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

    // The time machine (13-11-26): its archived radar as a tile layer, and its incidents, hazards,
    // aircraft, ships and outages as pins. The payload is one object holding several lists, which
    // the generic Render couldn't draw, so the Mac's map showed nothing for a past moment.
    private MKTileOverlay? _timeMachineRadar;

    private void SetTimeMachine(object?[] arguments)
    {
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (_timeMachineRadar is not null) { _map.RemoveOverlay(_timeMachineRadar); _timeMachineRadar = null; }
        if (payload is not Dictionary<string, object?> snapshot)
        {
            Clear("setTimeMachine");
            return;
        }
        if (TileTemplate(snapshot.GetValueOrDefault("radarTileUrl")) is { } radar)
        {
            _timeMachineRadar = new StretchedTileOverlay(radar, 10);
            _map.AddOverlay(_timeMachineRadar, MKOverlayLevel.AboveRoads);
        }
        Render("setTimeMachine", [NativeShapes.TimeMachinePins(snapshot)]);
    }

    // Aurora (13-11-26; before, the Mac drew nothing): the chance grid as areas (NativeShapes.AuroraCells).
    private void SetAurora(object?[] arguments)
    {
        Clear("setAurora");
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is not Dictionary<string, object?> aurora) return;
        // Thousands of grid runs: two multi-polygons (likely, then strong), not one overlay each.
        var overlays = new List<IMKOverlay>();
        foreach (var strength in NativeShapes.AuroraCells(aurora).GroupBy(cell => cell.Strong).OrderBy(group => group.Key))
        {
            var polygons = strength.Select(cell => MKPolygon.FromCoordinates(
            [
                new CLLocationCoordinate2D(cell.South, cell.West), new CLLocationCoordinate2D(cell.North, cell.West),
                new CLLocationCoordinate2D(cell.North, cell.East), new CLLocationCoordinate2D(cell.South, cell.East),
            ])).ToArray();
            overlays.Add(new MKMultiPolygon(polygons) { Title = strength.Key ? "setAurora:strong" : "setAurora" });
        }
        if (overlays.Count == 0) return;
        _map.AddOverlays(overlays.ToArray(), MKOverlayLevel.AboveRoads);
        _overlays["setAurora"] = overlays;
    }

    // Live flow (13-11-26; before, the Mac drew nothing): TomTom's congestion tiles come through the
    // server (it holds the key), which needs the person's sign-in, so tiles are fetched with the web
    // view's cookies.
    private MKTileOverlay? _trafficFlow;

    private void SetTrafficFlow(object?[] arguments)
    {
        if (_trafficFlow is not null) { _map.RemoveOverlay(_trafficFlow); _trafficFlow = null; }
        if (arguments.LastOrDefault() is not true || !_webView.TryGetTarget(out var webView) || webView.Url is not { } page) return;
        var origin = $"{page.Scheme}://{page.Host}{(page.Port > 0 ? ":" + page.Port.ToString(CultureInfo.InvariantCulture) : string.Empty)}";
        _trafficFlow = new SignedInTileOverlay(origin + "/admin/api/overwatch/traffic-flow/{z}/{x}/{y}?t=" + (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 120).ToString(CultureInfo.InvariantCulture));
        _map.AddOverlay(_trafficFlow, MKOverlayLevel.AboveRoads);
    }

    private sealed class SignedInTileOverlay : MKTileOverlay
    {
        private readonly string _template;

        public SignedInTileOverlay(string template) : base(template) => _template = template;

        public override void LoadTileAtPath(MKTileOverlayPath path, MKTileOverlayLoadTileCompletionHandler result)
        {
            var url = new NSUrl(_template.Replace("{z}", path.Z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{x}", path.X.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{y}", path.Y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            WKWebsiteDataStore.DefaultDataStore.HttpCookieStore.GetAllCookies(cookies =>
            {
                var request = new NSMutableUrlRequest(url);
                var headers = NSHttpCookie.RequestHeaderFieldsWithCookies(cookies.Where(c => url.Host?.EndsWith(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase) == true).ToArray());
                if (headers.TryGetValue(new NSString("Cookie"), out var cookieHeader)) request["Cookie"] = cookieHeader.ToString() ?? string.Empty;
                NSUrlSession.SharedSession.CreateDataTask(request, (data, response, error) =>
                {
                    if (response is NSHttpUrlResponse { StatusCode: 200 } && data is not null) result(data, null!);
                    else result(null!, error!);
                }).Resume();
            });
        }
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
        if (_timeMachineRadar is not null) { _map.RemoveOverlay(_timeMachineRadar); _timeMachineRadar = null; }
        if (_trafficFlow is not null) { _map.RemoveOverlay(_trafficFlow); _trafficFlow = null; }
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

    // What a pin's picture shows besides its symbol: a count disc's number, a callout's text.
    private static string Face(Dictionary<string, object?> item) =>
        $"{(item.TryGetValue("count", out var count) ? Convert.ToString(count, System.Globalization.CultureInfo.InvariantCulture) : "")}|{ReadString(item, "callout")}";

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

    private sealed class NativeAnnotation(string operation, Dictionary<string, object?> payload, CLLocationCoordinate2D coordinate) : MKPointAnnotation
    {
        public string Operation { get; } = operation;
        public Dictionary<string, object?> Payload { get; private set; } = payload;
        public override CLLocationCoordinate2D Coordinate { get; set; } = coordinate;

        public void Update(Dictionary<string, object?> next, CLLocationCoordinate2D coordinate)
        {
            Payload = next;
            Coordinate = coordinate;
            Title = ReadString(next, "title", "name", "label", "callsign", "code", "id") ?? Operation[3..];
            Subtitle = ReadString(next, "summary", "description", "status", "source", "kind");
        }
    }

    private sealed class MapDelegate(MacNativeOverwatchBridge owner) : MKMapViewDelegate
    {
        private static readonly Dictionary<string, UIImage> Symbols = new(StringComparer.Ordinal);

        // The group's label says what it holds ("7 conflict reports") and names the first few items,
        // instead of MapKit's first title and "+6 more" (19-11-26). MapKit makes the group; only its
        // label is set here. Making the group ourselves (mapView:clusterAnnotationForMemberAnnotations:)
        // crashed the app on 19-11-26: since macOS 27 the members arrive as protocol wrappers, and the
        // cluster built from them gave MapKit's quad trie a nil annotation (EXC_BAD_ACCESS at 0x20 while
        // zooming). Nothing here may throw: a group without a label is fine, a crash isn't.
        private void LabelCluster(IMKAnnotation annotation)
        {
            try
            {
                var members = (owner.ClusterMembers(annotation) ?? []).Select(owner.Resolve).OfType<NativeAnnotation>().ToList();
                if (members.Count == 0) return;
                var (title, subtitle) = NativeShapes.ClusterLabel(members[0].Operation, members.Select(m => m.Title).ToList());
                if (annotation is MKClusterAnnotation cluster)
                {
                    cluster.Title = title;
                    cluster.Subtitle = subtitle;
                    return;
                }
                if (AsNative(annotation) is not { } native) return;
                if (native.RespondsToSelector(new ObjCRuntime.Selector("setTitle:")))
                    native.SetValueForKey(new NSString(title), new NSString("title"));
                if (native.RespondsToSelector(new ObjCRuntime.Selector("setSubtitle:")))
                    native.SetValueForKey(new NSString(subtitle), new NSString("subtitle"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Overwatch: couldn't label a pin group: {ex.Message}");
            }
        }

        // Flat discs like the web globe's pins (19-11-26), in place of MapKit's balloons: a disc in
        // the layer's colour with its symbol, centred on the point (a balloon sat above it, which is
        // why HitTest measures drawn frames). Groups are a green disc with the count.
        public override MKAnnotationView GetViewForAnnotation(MKMapView mapView, IMKAnnotation annotation)
        {
            if (IsCluster(annotation))
            {
                LabelCluster(annotation);
                var clusterView = mapView.DequeueReusableAnnotation("gws-cluster-disc") ?? new MKAnnotationView(annotation, "gws-cluster-disc");
                clusterView.Annotation = annotation;
                clusterView.CanShowCallout = false;
                clusterView.Image = CountDisc((int)ClusterCount(annotation));
                clusterView.CollisionMode = MKAnnotationViewCollisionMode.Circle;
                return clusterView;
            }
            if (annotation is not NativeAnnotation native) return new MKAnnotationView(annotation, null) { Image = Disc(new NativeShapes.MarkerStyle("mappin", NativeShapes.MarkerTone.Gray)) };
            var view = mapView.DequeueReusableAnnotation("gws-disc") ?? new MKAnnotationView(annotation, "gws-disc");
            // No MapKit grouping (19-11-26): MapKit's clustering crashed the app twice while zooming
            // (EXC_BAD_ACCESS at 0x20 in MKQuadTrie insert, from requestAddingClusterForAnnotationViews),
            // first with our own cluster annotations and again, at 18:45 build, with MapKit's own. Overlap
            // is left to collision: a low-priority pin hides behind another, prominent ones always show.
            view.Annotation = native; view.CanShowCallout = false; view.ClusteringIdentifier = null;
            var style = NativeShapes.Marker(native.Operation, native.Payload);
            view.DisplayPriority = style.Prominent ? MKFeatureDisplayPriority.DefaultHigh : MKFeatureDisplayPriority.DefaultLow;
            view.CollisionMode = MKAnnotationViewCollisionMode.Circle;
            view.CenterOffset = CoreGraphics.CGPoint.Empty;
            view.Alpha = owner.FocusAlpha(native.Operation);
            if (ReadString(native.Payload, "id") is { } groupId && groupId.StartsWith("group:", StringComparison.Ordinal)
                && native.Payload.TryGetValue("count", out var count) && AsDouble(count, out var n))
            {
                // A crowd as one count disc (NativeShapes.Group); always shown.
                view.Image = CountDisc((int)n);
                view.DisplayPriority = MKFeatureDisplayPriority.Required;
            }
            else if (ReadString(native.Payload, "callout") is { Length: > 0 } callout)
            {
                // One of the three callouts (OverwatchCallouts): the disc with its box beside it,
                // offset so the disc stays on the place.
                var (image, discWidth) = CalloutImage(style, callout);
                view.Image = image;
                view.CenterOffset = new CoreGraphics.CGPoint((image.Size.Width - discWidth) / 2, 0);
                view.DisplayPriority = MKFeatureDisplayPriority.Required;
            }
            else
            {
                view.Image = Disc(style);
            }
            return view;
        }

        private static readonly Dictionary<string, UIImage> Discs = new(StringComparer.Ordinal);

        // The disc for a style, drawn once and kept: the colour fill, a near-black rim like the
        // web's pin outline, and the symbol in the glyph colour.
        private static UIImage Disc(NativeShapes.MarkerStyle style)
        {
            var key = $"{style.Symbol}|{style.Tone}|{style.Prominent}";
            if (Discs.TryGetValue(key, out var cached)) return cached;
            var size = style.Prominent ? 30.0 : 24.0;
            var glyph = NativeShapes.DarkGlyph(style.Tone) ? Hex(NativeShapes.GlyphHex) : UIColor.White;
            var image = new UIGraphicsImageRenderer(new CoreGraphics.CGSize(size, size)).CreateImage(_ =>
            {
                var ring = UIBezierPath.FromOval(new CoreGraphics.CGRect(1, 1, size - 2, size - 2));
                Tone(style.Tone).SetFill();
                ring.Fill();
                ring.LineWidth = 1.5f;
                Hex(NativeShapes.GlyphHex).SetStroke();
                ring.Stroke();
                var configured = Symbol(style.Symbol).ApplyConfiguration(UIImageSymbolConfiguration.Create((nfloat)(size * 0.42), UIImageSymbolWeight.Semibold));
                var symbol = (configured ?? Symbol(style.Symbol)).ApplyTintColor(glyph, UIImageRenderingMode.AlwaysOriginal);
                var w = symbol.Size.Width;
                var h = symbol.Size.Height;
                symbol.Draw(new CoreGraphics.CGRect((size - w) / 2, (size - h) / 2, w, h));
            });
            Discs[key] = image;
            return image;
        }

        // A pin with its callout box to the right: place/kind text in a dark box with a green (or
        // severity) rim, as the web globe draws it. Returns the image and the disc's width.
        private static (UIImage Image, double DiscWidth) CalloutImage(NativeShapes.MarkerStyle style, string text)
        {
            var disc = Disc(style);
            var colour = style.Tone is NativeShapes.MarkerTone.Red or NativeShapes.MarkerTone.Orange or NativeShapes.MarkerTone.Yellow
                ? Tone(style.Tone) : HoloMap.Hex(OverwatchMapLook.Bright);
            var attributes = new UIStringAttributes { Font = UIFont.FromName("Menlo-Bold", 10) ?? UIFont.BoldSystemFontOfSize(10), ForegroundColor = colour };
            var label = new NSString(text);
            var measured = label.GetSizeUsingAttributes(attributes);
            var boxWidth = measured.Width + 14;
            var boxHeight = measured.Height + 8;
            var gap = 8.0;
            var width = disc.Size.Width + gap + boxWidth;
            var height = Math.Max(disc.Size.Height, boxHeight);
            var image = new UIGraphicsImageRenderer(new CoreGraphics.CGSize(width, height)).CreateImage(_ =>
            {
                disc.Draw(new CoreGraphics.CGPoint(0, (height - disc.Size.Height) / 2));
                var box = new CoreGraphics.CGRect(disc.Size.Width + gap, (height - boxHeight) / 2, boxWidth, boxHeight);
                HoloMap.Hex("#020c07", 0.88).SetFill();
                UIBezierPath.FromRect(box).Fill();
                colour.ColorWithAlpha(0.7f).SetStroke();
                var rim = UIBezierPath.FromRect(box);
                rim.LineWidth = 0.8f;
                rim.Stroke();
                label.DrawString(new CoreGraphics.CGRect(box.X + 7, box.Y + 4, measured.Width, measured.Height), attributes);
            });
            return (image, disc.Size.Width);
        }

        // A group: a green disc, wider for a bigger count, with the number in the glyph colour.
        private static UIImage CountDisc(int count)
        {
            var text = count.ToString(CultureInfo.InvariantCulture);
            var key = "count|" + text;
            if (Discs.TryGetValue(key, out var cached)) return cached;
            var size = text.Length switch { <= 1 => 28.0, 2 => 32.0, 3 => 36.0, _ => 42.0 };
            var attributes = new UIStringAttributes
            {
                Font = UIFont.BoldSystemFontOfSize((nfloat)(size * 0.4)),
                ForegroundColor = Hex(NativeShapes.GlyphHex),
            };
            var label = new NSString(text);
            var image = new UIGraphicsImageRenderer(new CoreGraphics.CGSize(size, size)).CreateImage(_ =>
            {
                var ring = UIBezierPath.FromOval(new CoreGraphics.CGRect(1, 1, size - 2, size - 2));
                Tone(NativeShapes.MarkerTone.Green).SetFill();
                ring.Fill();
                ring.LineWidth = 2f;
                Hex(NativeShapes.GlyphHex).SetStroke();
                ring.Stroke();
                var measured = label.GetSizeUsingAttributes(attributes);
                label.DrawString(new CoreGraphics.CGRect((size - measured.Width) / 2, (size - measured.Height) / 2, measured.Width, measured.Height), attributes);
            });
            Discs[key] = image;
            return image;
        }

        private static UIImage Symbol(string name)
        {
            if (Symbols.TryGetValue(name, out var cached)) return cached;
            var image = UIImage.GetSystemImage(name) ?? UIImage.GetSystemImage("mappin")
                ?? throw new InvalidOperationException("The system map-pin symbol is unavailable.");
            Symbols[name] = image;
            return image;
        }

        public override MKOverlayRenderer OverlayRenderer(MKMapView mapView, IMKOverlay overlay) => owner.HoloRenderer(overlay) ?? overlay switch
        {
            MKTileOverlay tiles => new MKTileOverlayRenderer(tiles) { Alpha = 0.75f },
            MKMultiPolygon areas => AreaStyle(new MKMultiPolygonRenderer(areas), areas.Title),
            MKMultiPolyline paths => LineStyle(new MKMultiPolylineRenderer(paths), paths.Title),
            MKPolygon polygon => polygon.Title switch
            {
                "setWeatherAlerts" => new MKPolygonRenderer(polygon) { FillColor = Tone(NativeShapes.MarkerTone.Red).ColorWithAlpha(0.16f), StrokeColor = Tone(NativeShapes.MarkerTone.Red), LineWidth = 1.5f },
                "setPowerOutages" => new MKPolygonRenderer(polygon) { FillColor = Tone(NativeShapes.MarkerTone.Pink).ColorWithAlpha(0.22f), StrokeColor = Tone(NativeShapes.MarkerTone.Pink), LineWidth = 1 },
                "setAurora" => new MKPolygonRenderer(polygon) { FillColor = Tone(NativeShapes.MarkerTone.Green).ColorWithAlpha(0.22f), StrokeColor = UIColor.Clear, LineWidth = 0 },
                "setAurora:strong" => new MKPolygonRenderer(polygon) { FillColor = Tone(NativeShapes.MarkerTone.Yellow).ColorWithAlpha(0.3f), StrokeColor = UIColor.Clear, LineWidth = 0 },
                _ => new MKPolygonRenderer(polygon) { FillColor = Tone(NativeShapes.MarkerTone.Orange).ColorWithAlpha(0.22f), StrokeColor = Tone(NativeShapes.MarkerTone.Orange), LineWidth = 2 },
            },
            MKPolyline line => line.Title switch
            {
                "setRoadTraffic" => new MKPolylineRenderer(line) { StrokeColor = Tone(NativeShapes.MarkerTone.Yellow).ColorWithAlpha(0.85f), LineWidth = 3 },
                "setSatellites" => new MKPolylineRenderer(line) { StrokeColor = UIColor.White.ColorWithAlpha(0.45f), LineWidth = 1 },
                _ => new MKPolylineRenderer(line) { StrokeColor = Tone(NativeShapes.MarkerTone.Cyan), LineWidth = 3 },
            },
            _ => new MKOverlayRenderer(overlay)
        };

        // The same colours for a layer's grouped areas and lines.
        private static MKOverlayRenderer AreaStyle(MKOverlayPathRenderer renderer, string? layer)
        {
            var (fill, stroke, width) = layer switch
            {
                "setWeatherAlerts" => (Tone(NativeShapes.MarkerTone.Red).ColorWithAlpha(0.16f), Tone(NativeShapes.MarkerTone.Red), 1.5f),
                "setPowerOutages" => (Tone(NativeShapes.MarkerTone.Pink).ColorWithAlpha(0.22f), Tone(NativeShapes.MarkerTone.Pink), 1f),
                "setAurora" => (Tone(NativeShapes.MarkerTone.Green).ColorWithAlpha(0.22f), UIColor.Clear, 0f),
                "setAurora:strong" => (Tone(NativeShapes.MarkerTone.Yellow).ColorWithAlpha(0.3f), UIColor.Clear, 0f),
                "setConvergence" => (Tone(NativeShapes.MarkerTone.Orange).ColorWithAlpha(0.18f), Tone(NativeShapes.MarkerTone.Orange), 1.5f),
                _ => (Tone(NativeShapes.MarkerTone.Orange).ColorWithAlpha(0.22f), Tone(NativeShapes.MarkerTone.Orange), 2f),
            };
            renderer.FillColor = fill;
            renderer.StrokeColor = stroke;
            renderer.LineWidth = width;
            return renderer;
        }

        private static MKOverlayRenderer LineStyle(MKOverlayPathRenderer renderer, string? layer)
        {
            (renderer.StrokeColor, renderer.LineWidth) = layer switch
            {
                "setRoadTraffic" => (Tone(NativeShapes.MarkerTone.Yellow).ColorWithAlpha(0.85f), 3f),
                "setSatellites" => (UIColor.White.ColorWithAlpha(0.45f), 1f),
                _ => (Tone(NativeShapes.MarkerTone.Cyan), 3f),
            };
            return renderer;
        }

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
            owner.UpdateHolo();
            owner.RegroupIfZoomed();
            owner.RegionDidChange(mapView.Region);
        }
    }
}
