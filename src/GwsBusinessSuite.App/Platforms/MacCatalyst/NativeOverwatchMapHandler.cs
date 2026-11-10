#if MACCATALYST
using CoreLocation;
using MapKit;
using Microsoft.Maui.Handlers;

namespace GwsBusinessSuite.App;

public sealed class NativeOverwatchMapHandler : ViewHandler<NativeOverwatchMap, MKMapView>
{
    public NativeOverwatchMapHandler() : base(ViewMapper)
    {
    }

    protected override MKMapView CreatePlatformView()
    {
        var map = new MKMapView
        {
            MapType = MKMapType.HybridFlyover,
            ShowsCompass = true,
            ShowsScale = true,
            PitchEnabled = true,
            RotateEnabled = true
        };
        map.SetRegion(
            new MKCoordinateRegion(
                new CLLocationCoordinate2D(20, 0),
                new MKCoordinateSpan(100, 160)),
            animated: false);
        return map;
    }
}
#endif
