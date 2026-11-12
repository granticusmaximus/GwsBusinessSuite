using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace GwsBusinessSuite.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// SIGTERM (what install-gws-mac.sh --relaunch sends before reinstalling) quits the app the normal
	// way instead of killing it mid-write. A signal, not an Apple event, so the updater helper needs
	// no permission to control this app.
	// (PosixSignalRegistration isn't supported on Mac Catalyst, so: ignore the default action and
	// watch for the signal with a GCD source on the main queue.)
	private const int SigTerm = 15;
	private static DispatchSource.SignalMonitor? _quitOnTerm;

	[DllImport("libc", EntryPoint = "signal")]
	private static extern IntPtr SetSignalHandler(int signal, IntPtr handler);

	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		if (_quitOnTerm is null)
		{
			SetSignalHandler(SigTerm, new IntPtr(1)); // SIG_IGN
			_quitOnTerm = new DispatchSource.SignalMonitor(SigTerm, DispatchQueue.MainQueue);
			_quitOnTerm.SetEventHandler(() => Microsoft.Maui.Controls.Application.Current?.Quit());
			_quitOnTerm.Resume();
		}
		return base.FinishedLaunching(application, launchOptions);
	}

	// "GWS Business Suite > Check for Updates..." in the Mac menu bar.
	public override void BuildMenu(IUIMenuBuilder builder)
	{
		base.BuildMenu(builder);
		if (builder.System != UIMenuSystem.MainSystem) return;
		var checkForUpdates = UICommand.Create("Check for Updates…", null, new Selector("gwsCheckForUpdates:"), null);
		var setUpTouchId = UICommand.Create("Set Up Touch ID…", null, new Selector("gwsSetUpTouchId:"), null);
		var signInWithTouchId = UICommand.Create("Sign In with Touch ID", null, new Selector("gwsSignInWithTouchId:"), null);
		// PWA phase 4 (07-11-26): this Mac is SentinelGPT's model for your phone and tablet.
		var openAtLogin = UICommand.Create("Open at Login…", null, new Selector("gwsOpenAtLogin:"), null);
		builder.InsertSiblingMenuAfter(
			UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [checkForUpdates, setUpTouchId, signInWithTouchId, openAtLogin]),
			UIMenuIdentifier.About.GetConstant()!);

		// The standard Mac commands for the web workspace (12-11-26): Settings..., Back, Forward and
		// Find, with their usual keys. Find replaces the system Find group so Cmd+F has one owner.
		// Reload stays off the menu: pages such as World News use Cmd+R themselves.
		var settings = UIKeyCommand.Create(new NSString("Settings…"), null, new Selector("gwsSettings:"), ",", UIKeyModifierFlags.Command, null);
		builder.InsertSiblingMenuAfter(
			UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [settings]),
			UIMenuIdentifier.About.GetConstant()!);
		var back = UIKeyCommand.Create(new NSString("Back"), null, new Selector("gwsBack:"), "[", UIKeyModifierFlags.Command, null);
		var forward = UIKeyCommand.Create(new NSString("Forward"), null, new Selector("gwsForward:"), "]", UIKeyModifierFlags.Command, null);
		builder.InsertChildMenuAtStart(
			UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [back, forward]),
			UIMenuIdentifier.View.GetConstant()!);
		var find = UIKeyCommand.Create(new NSString("Find…"), null, new Selector("gwsFind:"), "f", UIKeyModifierFlags.Command, null);
		builder.ReplaceMenu(UIMenuIdentifier.Find.GetConstant()!,
			UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [find]));
	}

	[Export("gwsSettings:")]
	public void OpenSettings(NSObject sender) =>
		RunInWorkspace("(window.Blazor && Blazor.navigateTo) ? Blazor.navigateTo('/admin/settings') : location.assign('/admin/settings')");

	[Export("gwsBack:")]
	public void GoBack(NSObject sender) => RunInWorkspace("history.back()");

	[Export("gwsForward:")]
	public void GoForward(NSObject sender) => RunInWorkspace("history.forward()");

	// The page's own search where it has one (World News, lists), else the app's command palette
	// (app.js listens for gws-find).
	[Export("gwsFind:")]
	public void FindInWorkspace(NSObject sender) => RunInWorkspace("window.dispatchEvent(new CustomEvent('gws-find'))");

	// Switches to the Workspace tab if another is showing; its page sets EvaluateScript as it appears.
	private static void RunInWorkspace(string script) => MainThread.BeginInvokeOnMainThread(async () =>
	{
		if (MacToolbarActions.EvaluateScript is null)
		{
			MacToolbarActions.SelectTab?.Invoke("MainPage");
			for (var attempt = 0; attempt < 10 && MacToolbarActions.EvaluateScript is null; attempt++) await Task.Delay(50);
		}
		MacToolbarActions.EvaluateScript?.Invoke(script);
	});

	// Says why (your phone's SentinelGPT runs here), then opens Login Items to add the app.
	[Export("gwsOpenAtLogin:")]
	public void OpenAtLogin(NSObject sender) => MainThread.BeginInvokeOnMainThread(async () =>
	{
		var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
		if (page is not null && !await page.DisplayAlertAsync("Open at Login", MacAiServer.LoginItemsHelp, "Open Login Items", "Not Now")) return;
		if (!await MacAiServer.OpenLoginItemsSettingsAsync() && page is not null)
			await page.DisplayAlertAsync("Open at Login", "Open System Settings → General → Login Items yourself, then add GWS Business Suite under \"Open at Login\".", "OK");
	});

	[Export("gwsSignInWithTouchId:")]
	public void SignInWithTouchId(NSObject sender) => MainThread.BeginInvokeOnMainThread(() =>
	{
		MacToolbarActions.SelectTab?.Invoke("MainPage");
		MacToolbarActions.SignInWithTouchId?.Invoke();
	});

	[Export("gwsSetUpTouchId:")]
	public void SetUpTouchId(NSObject sender) => MainThread.BeginInvokeOnMainThread(() =>
	{
		MacToolbarActions.SelectTab?.Invoke("MainPage");
		MacToolbarActions.SetUpTouchId?.Invoke();
	});

	[Export("gwsCheckForUpdates:")]
	public void CheckForUpdates(NSObject sender) => MainThread.BeginInvokeOnMainThread(async () => await CheckForUpdatesAsync());

	// This app runs in the App Sandbox, so it can't read the repo, build, or replace itself in
	// /Applications. It hands the job to GWS Updater, a small helper outside the sandbox that
	// install-gws-mac.sh installs: the helper checks the source folder, asks, and runs the update.
	private static async Task CheckForUpdatesAsync()
	{
		var opened = await UIApplication.SharedApplication.OpenUrlAsync(new NSUrl("gws-update://check"), new UIApplicationOpenUrlOptions());
		if (opened) return;
		var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
		if (page is null) return;
		await page.DisplayAlertAsync("Check for Updates",
			"The updater isn't installed on this Mac yet. Run ./scripts/install-gws-mac.sh once from the source folder (or `open gws`) and it's set up from then on.",
			"OK");
	}
}
