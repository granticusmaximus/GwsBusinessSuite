namespace GwsBusinessSuite.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();

#if MACCATALYST
		// The Mac window's native toolbar (see Platforms/MacCatalyst/MacWindowToolbar.cs) is the
		// tab switcher on that platform, replacing Shell's own bottom tab bar - so route its
		// button taps into normal Shell navigation, and keep its highlighted button in sync with
		// whatever actually caused the page to change (a toolbar click, but also GoToAsync
		// called from anywhere else, or the app's initial route).
		MacToolbarActions.SelectTab = route => GoToAsync($"//{route}");
		Navigated += (_, e) => MacToolbarActions.SyncSelectedTab?.Invoke(e.Current?.Location?.OriginalString ?? string.Empty);
#endif
	}
}
