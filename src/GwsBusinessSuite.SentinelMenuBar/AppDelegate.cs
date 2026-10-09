using AppKit;
using Foundation;

namespace GwsBusinessSuite.SentinelMenuBar;

// A menu-bar-only companion (LSUIElement, see Info.plist - no Dock icon, no window) for the
// hosted Sentinel workspace. Every action opens the already-authenticated system browser session
// at the hosted app (see docs/CROSS_PLATFORM_CLIENTS.md). For the at-a-glance status it reads
// GET /api/v1/sentinel/status with a Developer API key (sentinel:read, read-only) that the user
// pastes in once via "Set API key..." and that's kept in the Keychain - it holds no other data.
[Register("AppDelegate")]
public class AppDelegate : NSApplicationDelegate
{
    private const string HostUrl = "https://admin.gwsapp.net/";
    private const string BaseUrl = HostUrl + "admin";
    private const string SentinelUrl = BaseUrl + "/sentinel";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private readonly StatusClient _client = new(HostUrl);
    private NSStatusItem? _statusItem;
    private NSTimer? _timer;
    private StatusSummary? _summary;
    private StatusProblem _problem = StatusProblem.NoKey;
    private DateTimeOffset? _lastChecked;
    private bool _refreshing;

    public override void DidFinishLaunching(NSNotification notification)
    {
        _statusItem = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Variable);
        ConfigureIcon();
        Render();
        _timer = NSTimer.CreateRepeatingScheduledTimer(RefreshInterval, timer => { _ = RefreshAsync(); });
        _ = RefreshAsync();
    }

    public override void WillTerminate(NSNotification notification)
    {
        _timer?.Invalidate();
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
            button.ImagePosition = NSCellImagePosition.ImageLeading;
        }
        else
        {
            button.Title = "S";
        }

        button.ToolTip = "Sentinel";
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var (summary, problem) = await _client.FetchAsync(KeychainKeyStore.Read());
            InvokeOnMainThread(() =>
            {
                _problem = problem;
                // Keep showing the last good numbers through a brief network blip.
                if (summary is not null) _summary = summary;
                if (problem is StatusProblem.NoKey or StatusProblem.KeyRejected) _summary = null;
                _lastChecked = DateTimeOffset.Now;
                Render();
            });
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Render()
    {
        if (_statusItem?.Button is { } button)
        {
            // The number of things wanting attention sits next to the icon; "!" when the server
            // reports an error-level container alert.
            var count = _summary?.AttentionCount ?? 0;
            button.Title = _summary?.Health == "critical" ? " !" : count > 0 ? $" {count}" : string.Empty;
            button.ToolTip = _summary is null ? "Sentinel" : count == 0 ? "Sentinel - nothing needs attention" : $"Sentinel - {count} need attention";
        }

        if (_statusItem is not null)
        {
            _statusItem.Menu = BuildMenu();
        }
    }

    private NSMenu BuildMenu()
    {
        var menu = new NSMenu();
        foreach (var line in StatusMenuLines.Build(_summary, _problem, DateTimeOffset.Now))
        {
            menu.AddItem(line.Path is null
                ? new NSMenuItem(line.Text) { Enabled = false }
                : CreateItem(line.Text, () => OpenUrl(BaseUrl + line.Path)));
        }
        if (_lastChecked is { } checkedAt)
        {
            menu.AddItem(new NSMenuItem($"Updated {checkedAt:h:mm tt}") { Enabled = false });
        }
        menu.AddItem(CreateItem("Refresh Now", () => _ = RefreshAsync(), "r"));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(CreateItem("Open Sentinel", OpenSentinel));
        menu.AddItem(CreateItem("Open Dashboard", OpenDashboard));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(CreateItem("Set API Key...", PromptForKey));
        if (KeychainKeyStore.Read() is not null)
        {
            menu.AddItem(CreateItem("Remove API Key", RemoveKey));
        }
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(CreateItem("Quit Sentinel", Quit, "q"));
        return menu;
    }

    private void PromptForKey()
    {
        // A menu-bar app has no window of its own, so bring it forward for the dialog.
        if (OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            NSApplication.SharedApplication.Activate();
        }
        else
        {
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        }
        var field = new NSSecureTextField(new CoreGraphics.CGRect(0, 0, 320, 24)) { PlaceholderString = "gws_..." };
        var alert = new NSAlert
        {
            MessageText = "Developer API key",
            InformativeText = "Create a key with the sentinel:read scope in Settings > Developer API, then paste it here. It's stored in your Keychain and only used to read status counts.",
            AccessoryView = field
        };
        alert.AddButton("Save");
        alert.AddButton("Cancel");
        alert.Window.InitialFirstResponder = field;
        if (alert.RunModal() == (nint)(long)NSAlertButtonReturn.First && !string.IsNullOrWhiteSpace(field.StringValue))
        {
            KeychainKeyStore.Save(field.StringValue.Trim());
            _ = RefreshAsync();
        }
    }

    private void RemoveKey()
    {
        KeychainKeyStore.Remove();
        _summary = null;
        _problem = StatusProblem.NoKey;
        Render();
    }

    private static NSMenuItem CreateItem(string title, Action action, string keyEquivalent = "")
    {
        var item = new NSMenuItem(title) { KeyEquivalent = keyEquivalent };
        item.Activated += (_, _) => action();
        return item;
    }

    private static void OpenSentinel() => OpenUrl(SentinelUrl);
    private static void OpenDashboard() => OpenUrl(BaseUrl);
    private static void Quit() => NSApplication.SharedApplication.Terminate(null);

    private static void OpenUrl(string url)
    {
        if (NSUrl.FromString(url) is { } nsUrl)
        {
            NSWorkspace.SharedWorkspace.OpenUrl(nsUrl);
        }
    }
}
