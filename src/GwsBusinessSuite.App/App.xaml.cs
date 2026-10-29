using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.App;

public partial class App : Application
{
	private readonly OfflineSyncService _offlineSync;
	private readonly NativeSessionClient _session;
	private readonly WorldNewsSummaryRelay _worldNewsSummaries;

	public App(OfflineSyncService offlineSync, NativeSessionClient session, WorldNewsSummaryRelay worldNewsSummaries)
	{
		InitializeComponent();
		_offlineSync = offlineSync;
		_session = session;
		_worldNewsSummaries = worldNewsSummaries;
	}

	// Page lock: hiding the app (or the Mac going to sleep, which hides it) locks this session's
	// locked Sentinel pages again.
	protected override void OnSleep()
	{
		base.OnSleep();
		_ = _session.LockSessionAsync();
	}

	protected override void OnStart()
	{
		base.OnStart();
		// Sends waiting Quick Notes and refreshes offline page copies (Sentinel item 12).
		_offlineSync.Start();
		_worldNewsSummaries.Start();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}