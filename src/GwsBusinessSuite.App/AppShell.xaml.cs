namespace GwsBusinessSuite.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
#if MACCATALYST
		// Local-model SentinelGPT only makes sense where a local Ollama install is realistic -
		// the Mac app. iOS/Android/Windows keep the single hosted-workspace page unchanged.
		//
		// Appending a second bare ShellContent to Items (the first approach here) built and ran
		// without error but never actually rendered a visible tab bar - two screenshots in a row
		// showed no tab control anywhere in the window. A ShellContent added directly to a Shell
		// only reliably becomes a visible tab when it's inside an explicit TabBar ShellItem, so
		// this replaces the XAML-declared item with a properly constructed one instead of just
		// adding alongside it.
		Items.Clear();
		var tabBar = new TabBar();
		tabBar.Items.Add(new ShellContent
		{
			Title = "Workspace",
			ContentTemplate = new DataTemplate(typeof(MainPage)),
			Route = "MainPage"
		});
		tabBar.Items.Add(new ShellContent
		{
			Title = "SentinelGPT",
			ContentTemplate = new DataTemplate(typeof(SentinelGptPage)),
			Route = "SentinelGptPage"
		});
		Items.Add(tabBar);
		Shell.SetTabBarIsVisible(this, true);
#endif
	}
}
