using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.App;

public partial class App : Application
{
	private readonly OfflineSyncService _offlineSync;

	public App(OfflineSyncService offlineSync)
	{
		InitializeComponent();
		_offlineSync = offlineSync;
	}

	protected override void OnStart()
	{
		base.OnStart();
		// Sends waiting Quick Notes and refreshes offline page copies (Sentinel item 12).
		_offlineSync.Start();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}