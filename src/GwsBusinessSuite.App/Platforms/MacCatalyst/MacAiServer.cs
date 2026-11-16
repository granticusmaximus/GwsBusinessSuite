using Foundation;
using UIKit;

namespace GwsBusinessSuite.App;

// This Mac as the AI server for your other devices (PWA phase 4, 07-11-26). The server has no
// model, so SentinelGPT on a phone or tablet runs on this Mac through the relay
// (SentinelAgentMacRelay). This keeps the relay answering while the app is in the background or its
// window is closed (no App Nap; the Mac can still go to sleep, which pauses it), and helps add the
// app to Login Items so it's there after a restart. LocalAiPowerPolicyService separately owns the
// idle-system-sleep assertion selected under SentinelGPT settings.
//
// Not SMAppService: its Register/Unregister are macOS-only, not Mac Catalyst (CA1416, and calling
// them from a Catalyst app isn't supported), so the person adds the app in System Settings.
public static class MacAiServer
{
    private static NSObject? _relayActivity;
    private static NSObject? _computeActivity;

    // Called once the relays start. Like Apple's NSActivityUserInitiatedAllowingIdleSystemSleep:
    // stops macOS slowing the app's timers and polls while nobody is looking at it, but still lets
    // the Mac sleep when idle.
    public static void KeepRelayAnswering()
    {
        if (_relayActivity is not null) return;
        _relayActivity = NSProcessInfo.ProcessInfo.BeginActivity(NSActivityOptions.UserInitiated & ~NSActivityOptions.IdleSystemSleepDisabled,
            "Answering SentinelGPT for your other devices");
    }

    // The web server reaches Ollama directly over Tailscale, so the app must be awake before the
    // request arrives. This assertion prevents idle system sleep but deliberately does not prevent
    // display sleep. macOS still owns lid-close and critical-power behavior.
    public static void SetLocalAiAvailable(bool available)
    {
        if (available && _computeActivity is null)
        {
            _computeActivity = NSProcessInfo.ProcessInfo.BeginActivity(
                NSActivityOptions.UserInitiated | NSActivityOptions.IdleSystemSleepDisabled,
                "Keeping this Mac available for GWS local AI");
        }
        else if (!available && _computeActivity is not null)
        {
            NSProcessInfo.ProcessInfo.EndActivity(_computeActivity);
            _computeActivity.Dispose();
            _computeActivity = null;
        }
    }

    public const string LoginItemsHelp =
        "SentinelGPT on your phone and tablet runs on this Mac, so it works while this app is open. " +
        "It keeps answering with its window closed. Its Local AI on battery setting controls idle sleep; quitting it or closing a MacBook lid pauses it.\n\n" +
        "To have it open when you log in: in Login Items, click + under \"Open at Login\" and choose GWS Business Suite.";

    // System Settings → General → Login Items (macOS 13 and later).
    public static Task<bool> OpenLoginItemsSettingsAsync() =>
        UIApplication.SharedApplication.OpenUrlAsync(new NSUrl("x-apple.systempreferences:com.apple.LoginItems-Settings.extension"),
            new UIApplicationOpenUrlOptions());
}
