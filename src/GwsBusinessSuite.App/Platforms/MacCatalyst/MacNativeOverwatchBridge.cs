using System.Globalization;
using System.Text.Json;
using CoreLocation;
using Foundation;
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
    private readonly Dictionary<string, List<IMKAnnotation>> _annotations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<IMKOverlay>> _overlays = new(StringComparer.Ordinal);
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
        MainThread.BeginInvokeOnMainThread(() => Apply(operation, arguments));
    }

    private void Apply(string operation, object?[] arguments)
    {
        switch (operation)
        {
            case "init": Show(arguments); return;
            case "dispose": _pickPointMode = false; ClearAll(); _map.Hidden = true; return;
            case "flyTo":
            case "flyToStreetLevel": FlyTo(arguments); return;
            case "flyToBounds": FlyToBounds(arguments); return;
            case "zoom": Zoom(arguments); return;
            case "setThreeD": SetThreeD(arguments); return;
            case "startPickPoint": _pickPointMode = true; return;
            case "cancelPickPoint": _pickPointMode = false; return;
            case "mapTap": MapTap(arguments); return;
            case "home":
                _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D(20, 0), new MKCoordinateSpan(140, 340)), true);
                return;
        }
        if (operation.StartsWith("clear", StringComparison.Ordinal)) { Clear("set" + operation[5..]); return; }
        if (operation.StartsWith("set", StringComparison.Ordinal)) Render(operation, arguments);
    }

    private void Show(object?[] arguments)
    {
        if (!_webView.TryGetTarget(out var webView)) return;
        if (_map.Superview is null) webView.Superview?.InsertSubviewBelow(_map, webView);
        _map.Frame = webView.Frame;
        if (arguments.Length >= 6
            && AsDouble(arguments[0], out var x) && AsDouble(arguments[1], out var y)
            && AsDouble(arguments[2], out var width) && AsDouble(arguments[3], out var height)
            && AsDouble(arguments[4], out var viewportWidth) && viewportWidth > 0
            && AsDouble(arguments[5], out var viewportHeight) && viewportHeight > 0)
        {
            var scaleX = webView.Bounds.Width / viewportWidth;
            var scaleY = webView.Bounds.Height / viewportHeight;
            _map.Frame = new CoreGraphics.CGRect(
                webView.Frame.X + x * scaleX,
                webView.Frame.Y + y * scaleY,
                width * scaleX,
                height * scaleY);
        }
        _map.Hidden = false;
        webView.Opaque = false;
        webView.BackgroundColor = UIColor.Clear;
        webView.ScrollView.BackgroundColor = UIColor.Clear;
        if (!_hasInitialRegion)
        {
            _hasInitialRegion = true;
            _map.SetRegion(new MKCoordinateRegion(new CLLocationCoordinate2D(20, 0), new MKCoordinateSpan(140, 340)), false);
        }
    }

    private void Render(string operation, object?[] arguments)
    {
        Clear(operation);
        var payload = arguments.FirstOrDefault() is string ? arguments.ElementAtOrDefault(1) : arguments.FirstOrDefault();
        if (payload is null) return;
        foreach (var item in Items(payload))
        {
            if (TryCoordinate(item, out var coordinate))
            {
                var annotation = new NativeAnnotation(operation, item, coordinate)
                {
                    Title = ReadString(item, "title", "name", "label", "callsign", "code", "id") ?? operation[3..],
                    Subtitle = ReadString(item, "summary", "description", "status", "source", "kind")
                };
                _map.AddAnnotation(annotation);
                Annotations(operation).Add(annotation);
            }
            foreach (var coordinates in Paths(item))
            {
                if (coordinates.Length < 2) continue;
                IMKOverlay overlay = coordinates.Length >= 3 && IsArea(operation)
                    ? MKPolygon.FromCoordinates(coordinates)
                    : MKPolyline.FromCoordinates(coordinates);
                _map.AddOverlay(overlay);
                Overlays(operation).Add(overlay);
            }
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

        var nearest = _annotations.Values.SelectMany(values => values).OfType<NativeAnnotation>()
            .Select(annotation =>
            {
                var location = _map.ConvertCoordinate(annotation.Coordinate, _map);
                var deltaX = location.X - point.X;
                var deltaY = location.Y - point.Y;
                return (Annotation: annotation, DistanceSquared: deltaX * deltaX + deltaY * deltaY);
            })
            .Where(item => item.DistanceSquared <= 36 * 36)
            .OrderBy(item => item.DistanceSquared)
            .FirstOrDefault();
        if (nearest.Annotation is not null) _map.SelectAnnotation(nearest.Annotation, true);
    }

    private void Clear(string operation)
    {
        if (_annotations.Remove(operation, out var annotations) && annotations.Count > 0) _map.RemoveAnnotations(annotations.ToArray());
        if (_overlays.Remove(operation, out var overlays) && overlays.Count > 0) _map.RemoveOverlays(overlays.ToArray());
    }

    private void ClearAll()
    {
        foreach (var operation in _annotations.Keys.Concat(_overlays.Keys).Distinct().ToList()) Clear(operation);
    }

    private List<IMKAnnotation> Annotations(string operation) =>
        _annotations.TryGetValue(operation, out var values) ? values : _annotations[operation] = [];
    private List<IMKOverlay> Overlays(string operation) =>
        _overlays.TryGetValue(operation, out var values) ? values : _overlays[operation] = [];

    private Task SendAsync(string script) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (_webView.TryGetTarget(out var webView)) await webView.EvaluateJavaScriptAsync(script);
    });

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
            if (annotation is not NativeAnnotation native) return new MKMarkerAnnotationView(annotation, null);
            var view = mapView.DequeueReusableAnnotation("gws-native") as MKMarkerAnnotationView ?? new MKMarkerAnnotationView(annotation, "gws-native");
            view.Annotation = native; view.CanShowCallout = true; view.ClusteringIdentifier = native.Operation;
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
            MKPolygon polygon => new MKPolygonRenderer(polygon) { FillColor = UIColor.SystemOrange.ColorWithAlpha(0.22f), StrokeColor = UIColor.SystemOrange, LineWidth = 2 },
            MKPolyline line => new MKPolylineRenderer(line) { StrokeColor = UIColor.SystemCyan, LineWidth = 3 },
            _ => new MKOverlayRenderer(overlay)
        };

        public override void DidSelectAnnotationView(MKMapView mapView, MKAnnotationView view)
        {
            if (view.Annotation is not NativeAnnotation annotation) return;
            var json = JsonSerializer.Serialize(new { operation = annotation.Operation, payload = annotation.Payload });
            _ = owner.SendAsync($"window.gwsNativeOverwatch && window.gwsNativeOverwatch.selected({json})");
        }

        public override void RegionChanged(MKMapView mapView, bool animated)
        {
            var region = mapView.Region;
            var north = Math.Min(90, region.Center.Latitude + region.Span.LatitudeDelta / 2);
            var south = Math.Max(-90, region.Center.Latitude - region.Span.LatitudeDelta / 2);
            var east = region.Center.Longitude + region.Span.LongitudeDelta / 2;
            var west = region.Center.Longitude - region.Span.LongitudeDelta / 2;
            if (east > 180) east -= 360; if (west < -180) west += 360;
            _ = owner.SendAsync($"window.gwsNativeOverwatch && window.gwsNativeOverwatch.regionChanged({north}, {south}, {east}, {west})");
        }
    }
}
