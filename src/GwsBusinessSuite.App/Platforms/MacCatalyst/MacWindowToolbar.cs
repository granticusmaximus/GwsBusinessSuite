using AppKit;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace GwsBusinessSuite.App;

// App-level controls for the Mac window's native titlebar.
//
// Why not in the page: the hosted admin site fills the WebView and already uses all four of its
// corners - search and the account menu top-right, the sidebar and Settings bottom-left. Any
// floating control drawn over it lands on top of the site's own chrome, which is what the
// corner-anchored Reload button and Shell's bottom tab pill were both doing. The titlebar is
// empty, it is native chrome rather than page chrome, and it is where a macOS user already
// looks for app-level actions - so the two concerns stop competing for the same pixels.
//
// Text labels only, deliberately: this binding's NSToolbarItem.Image wants an AppKit NSImage,
// and there is no SF Symbol constructor exposed on it in this SDK - converting a UIImage across
// that boundary is not worth the fragility it would add to a two-button toolbar.
internal static class MacToolbarActions
{
    // Set by MainPage while it is on screen. Null when the SentinelGPT tab is showing, which is
    // also when the reload item should be unavailable - there is no WebView to reload.
    public static Action? Reload { get; set; }
    public static Action? ConfigureDeviceLogin { get; set; }

    // Raised by a tab button; AppShell owns the actual navigation.
    public static Action<string>? SelectTab { get; set; }

    // Lets the current page tell the toolbar which tab is showing, so the highlighted button
    // still reflects reality when navigation happens from somewhere other than the toolbar.
    public static Action<string>? SyncSelectedTab { get; set; }
}

// NSToolbar via UIWindowScene.Titlebar is Apple's documented, supported way to add a native
// toolbar to a Mac Catalyst app running the Mac idiom (UIDeviceFamily 6) - not a private or
// undocumented bridge; every "Optimize Interface for Mac" Catalyst app uses exactly this API.
//
// The CA1416 warnings below are a real limitation of the platform-compat analyzer rather than
// a real incompatibility (contrast WorkspaceBookmarkStore's WithSecurityScope, which genuinely
// is unsupported on Catalyst - checked the same way before concluding this, per that fix).
// The binding tags these specific members [SupportedOSPlatform("macos")], and the analyzer
// treats "macos" and "maccatalyst" as unrelated platform identifiers - no [SupportedOSPlatform]
// on this class's own maccatalyst-only code can satisfy a requirement written against a
// different identifier, so there is no annotation that fixes this (this is a known dotnet/macios
// gap: AppKit-tagged members bridged to Catalyst's Mac idiom have no attribute that expresses
// "supported here too"). This file exists only under Platforms/MacCatalyst and is therefore
// compiled solely into the net10.0-maccatalyst target, where UIDeviceFamily 6 makes every
// suppressed call genuinely safe to make.
#pragma warning disable CA1416
internal sealed class MacWindowToolbar : NSToolbarDelegate
{
    private const string WorkspaceId = "gws.tab.workspace";
    private const string SentinelGptId = "gws.tab.sentinelgpt";
    private const string ReloadId = "gws.reload";
    private const string DeviceLoginId = "gws.deviceLogin";

    private NSToolbar? _toolbar;

    public static void Attach(UIWindowScene scene)
    {
        var titlebar = scene.Titlebar;
        if (titlebar is null)
        {
            // Titlebar is null when the same build runs on iPad, where there is no window
            // chrome to attach to. Nothing to do, and nothing has gone wrong.
            return;
        }

        var owner = new MacWindowToolbar();
        var toolbar = new NSToolbar("gws.mainToolbar") { Delegate = owner };
        owner._toolbar = toolbar;

        titlebar.Toolbar = toolbar;
        // Unified puts the toolbar on the same row as the window title, which keeps the whole
        // titlebar one compact strip instead of adding a second band above the content.
        titlebar.ToolbarStyle = UITitlebarToolbarStyle.Unified;
        titlebar.TitleVisibility = UITitlebarTitleVisibility.Visible;

        toolbar.SelectedItemIdentifier = WorkspaceId;
        MacToolbarActions.SyncSelectedTab = route =>
            toolbar.SelectedItemIdentifier = RouteToIdentifier(route);
    }

    private static string RouteToIdentifier(string route) =>
        route.Contains("Sentinel", StringComparison.OrdinalIgnoreCase) ? SentinelGptId : WorkspaceId;

    public override string[] AllowedItemIdentifiers(NSToolbar toolbar) =>
        [WorkspaceId, SentinelGptId, NSToolbar.NSToolbarFlexibleSpaceItemIdentifier, DeviceLoginId, ReloadId];

    public override string[] DefaultItemIdentifiers(NSToolbar toolbar) =>
        [WorkspaceId, SentinelGptId, NSToolbar.NSToolbarFlexibleSpaceItemIdentifier, DeviceLoginId, ReloadId];

    // Selectable items (a segmented radio group of one) let NSToolbar draw the current tab as
    // pressed-in without any custom highlight logic - toolbar.SelectedItemIdentifier is all
    // that is needed to move the highlight.
    public override string[] SelectableItemIdentifiers(NSToolbar toolbar) => [WorkspaceId, SentinelGptId];

    public override NSToolbarItem? WillInsertItem(NSToolbar toolbar, string itemIdentifier, bool willBeInserted) =>
        itemIdentifier switch
        {
            WorkspaceId => BuildTabButton(WorkspaceId, "Workspace", "MainPage"),
            SentinelGptId => BuildTabButton(SentinelGptId, "SentinelGPT", "SentinelGptPage"),
            ReloadId => BuildActionButton(ReloadId, "Reload", "Reload the workspace", () => MacToolbarActions.Reload?.Invoke()),
            DeviceLoginId => BuildDeviceLoginButton(),
            _ => null
        };

    private static NSToolbarItem BuildTabButton(string identifier, string label, string route)
    {
        var item = new NSToolbarItem(identifier)
        {
            Label = label,
            PaletteLabel = label,
            Bordered = true,
            Target = new ActionTarget(() => MacToolbarActions.SelectTab?.Invoke(route)),
            Action = new Selector("gwsToolbarActionActivated:")
        };
        return item;
    }

    private static NSToolbarItem BuildActionButton(string identifier, string label, string tooltip, Action onActivated)
    {
        var item = new NSToolbarItem(identifier)
        {
            Label = label,
            PaletteLabel = label,
            ToolTip = tooltip,
            Bordered = true,
            Target = new ActionTarget(onActivated),
            Action = new Selector("gwsToolbarActionActivated:")
        };
        return item;
    }

    // Device login is a MacCatalyst-only feature already (see DeviceSecretStore/
    // NativeAppAuthService), and this whole toolbar only exists on the Mac idiom, so no
    // per-item availability check is needed here.
    private static NSToolbarItem BuildDeviceLoginButton() =>
        BuildActionButton(DeviceLoginId, "Device Login", "Configure device login",
            () => MacToolbarActions.ConfigureDeviceLogin?.Invoke());

    // NSToolbarItem dispatches through target/action rather than a managed delegate, so each
    // item keeps a small NSObject whose exported selector calls back into managed code. The
    // item owns a strong reference to it, which is what keeps it alive.
    private sealed class ActionTarget(Action onActivated) : NSObject
    {
        [Export("gwsToolbarActionActivated:")]
        public void Activated(NSObject sender) => onActivated();
    }
}
#pragma warning restore CA1416
