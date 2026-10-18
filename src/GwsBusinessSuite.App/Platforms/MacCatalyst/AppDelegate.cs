using Foundation;
using ObjCRuntime;
using UIKit;

namespace GwsBusinessSuite.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	// "GWS Business Suite > Check for Updates..." in the Mac menu bar (AppUpdater).
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

	private static async Task CheckForUpdatesAsync()
	{
		var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
		if (page is null) return;
		var (available, problem) = await AppUpdater.CheckAsync();
		if (available is null)
		{
			await page.DisplayAlertAsync("Check for Updates", $"Couldn't check for updates: {problem}", "OK");
			return;
		}
		if (available == false)
		{
			await page.DisplayAlertAsync("Check for Updates", "GWS Business Suite is up to date.", "OK");
			return;
		}
		var install = await page.DisplayAlertAsync("Update Available",
			"A newer version is in your source folder. Install it now? The app closes, rebuilds (usually a minute or two) and opens again by itself.",
			"Install and Reopen", "Later");
		if (!install) return;
		if (AppUpdater.StartUpdate() is { } error)
			await page.DisplayAlertAsync("Check for Updates", $"The update couldn't start: {error}", "OK");
	}
}
