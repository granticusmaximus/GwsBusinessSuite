using CoreGraphics;
using GwsBusinessSuite.Application.Overwatch;
using UIKit;

namespace GwsBusinessSuite.App;

// This view is intentionally not connected to /admin/osint yet. WKWebView remains the active
// Overwatch UI until this host renders every command at feature parity. The host is nevertheless
// a real native UIKit surface and its adapter retains every server payload without lossy DTO
// conversion, ready for the staged Metal/map implementation.
internal sealed class NativeOverwatchRendererHost : UIView
{
    private readonly NativeOverwatchPayloadAdapter _adapter = new();

    public NativeOverwatchRendererHost(CGRect frame) : base(frame)
    {
        BackgroundColor = UIColor.FromRGB(20, 18, 16);
        Opaque = true;
    }

    public void Apply(NativeOverwatchViewport viewport, string contractVersion)
    {
        _adapter.Apply(viewport, contractVersion);
        SetNeedsDisplay();
    }

    public override void Draw(CGRect rect)
    {
        base.Draw(rect);
        using var context = UIGraphics.GetCurrentContext();
        if (context is null) return;

        context.SetFillColor(UIColor.FromRGB(20, 18, 16).CGColor);
        context.FillRect(rect);
        context.SetStrokeColor(UIColor.FromRGB(102, 80, 31).CGColor);
        context.SetLineWidth(1);
        context.StrokeRect(rect.Inset(1, 1));

        var message = _adapter.Request is null
            ? "NATIVE OVERWATCH RENDERER · awaiting authenticated viewport"
            : $"NATIVE OVERWATCH RENDERER · {_adapter.Payloads.Count} contract payloads";
        var attributes = new UIStringAttributes
        {
            ForegroundColor = UIColor.FromRGB(251, 191, 36),
            Font = UIFont.SystemFontOfSize(12, UIFontWeight.Semibold)
        };
        new Foundation.NSString(message).DrawString(new CGPoint(14, 24), attributes);
    }
}

internal static class NativeOverwatchRendererFeature
{
    // This must be explicit per launch. It remains false in production until the native surface
    // can draw every authenticated command, and no existing route consults it yet.
    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("GWS_NATIVE_OVERWATCH_RENDERER"), "1", StringComparison.Ordinal);
}
