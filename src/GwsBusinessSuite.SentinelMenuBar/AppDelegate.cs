using AppKit;
using Foundation;

namespace GwsBusinessSuite.SentinelMenuBar;

// A menu-bar-only companion (LSUIElement, see Info.plist - no Dock icon, no window) for the
// hosted Sentinel workspace. It owns no data of its own and does not authenticate on its own
// behalf: every action just opens the already-authenticated system browser session at the
// hosted app, the same source of truth every other client (browser, MAUI) uses - see
// docs/CROSS_PLATFORM_CLIENTS.md. "Refresh Workspace Data" reuses the existing manual-sync
// button's own logic (Wiki.razor's SyncNotionNowAsync) via a `?syncNow=1` query flag rather
// than inventing a second, unauthenticated remote-trigger API surface.
[Register("AppDelegate")]
public class AppDelegate : NSApplicationDelegate
{
    private const string BaseUrl = "https://admin.gwsapp.net/admin";
    private const string SentinelUrl = BaseUrl + "/sentinel";
    private const string RefreshUrl = SentinelUrl + "?syncNow=1";

    private NSStatusItem? _statusItem;

    public override void DidFinishLaunching(NSNotification notification)
    {
        _statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);
        ConfigureIcon();
        _statusItem.Menu = BuildMenu();
    }

    public override void WillTerminate(NSNotification notification)
    {
    }

    private void ConfigureIcon()
    {
        if (_statusItem?.Button is not { } button)
        {
            return;
        }

        // Placeholder SF Symbol glyph until a dedicated Sentinel logo exists (tracked in
        // docs/CROSS_PLATFORM_CLIENTS.md) - marked as a template image so AppKit recolors it
        // automatically for the menu bar's light/dark appearance, matching every other menu
        // extra rather than a fixed-color icon.
        var image = NSImage.GetSystemSymbol("shield.lefthalf.filled", null);
        if (image is not null)
        {
            image.Template = true;
            button.Image = image;
        }
        else
        {
            button.Title = "S";
        }

        button.ToolTip = "Sentinel";
    }

    private NSMenu BuildMenu()
    {
        var menu = new NSMenu();
        menu.AddItem(CreateItem("Open Sentinel", OpenSentinel));
        menu.AddItem(CreateItem("Open Dashboard", OpenDashboard));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(CreateItem("Refresh Workspace Data", RefreshWorkspace));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(CreateItem("Quit Sentinel", Quit, "q"));
        return menu;
    }

    private static NSMenuItem CreateItem(string title, Action action, string keyEquivalent = "")
    {
        var item = new NSMenuItem(title) { KeyEquivalent = keyEquivalent };
        item.Activated += (_, _) => action();
        return item;
    }

    private static void OpenSentinel() => OpenUrl(SentinelUrl);
    private static void OpenDashboard() => OpenUrl(BaseUrl);
    private static void RefreshWorkspace() => OpenUrl(RefreshUrl);
    private static void Quit() => NSApplication.SharedApplication.Terminate(null);

    private static void OpenUrl(string url)
    {
        if (NSUrl.FromString(url) is { } nsUrl)
        {
            NSWorkspace.SharedWorkspace.OpenUrl(nsUrl);
        }
    }
}
