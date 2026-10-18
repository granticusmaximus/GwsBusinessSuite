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
		builder.InsertSiblingMenuAfter(
			UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [checkForUpdates, setUpTouchId, signInWithTouchId]),
			UIMenuIdentifier.About.GetConstant()!);
	}

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
