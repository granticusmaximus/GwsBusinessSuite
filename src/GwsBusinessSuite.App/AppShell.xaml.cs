namespace GwsBusinessSuite.App;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
#if MACCATALYST
		// Local-model SentinelGPT only makes sense where a local Ollama install is realistic -
		// the Mac app. iOS/Android/Windows keep the single hosted-workspace page unchanged.
		Shell.SetTabBarIsVisible(this, true);
		Items.Add(new ShellContent
		{
			Title = "SentinelGPT",
			ContentTemplate = new DataTemplate(typeof(SentinelGptPage)),
			Route = "SentinelGptPage"
		});
#endif
	}
}
