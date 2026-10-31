using Microsoft.Extensions.DependencyInjection;

namespace GwsBusinessSuite.App;

public partial class App : Application
{
	private readonly OfflineSyncService _offlineSync;
	private readonly NativeSessionClient _session;
	private readonly WorldNewsSummaryRelay _worldNewsSummaries;
	private readonly WorldNewsEmbedRelay _worldNewsEmbeddings;
	private readonly SentinelAgentMacRelay _agentRelay;
	private readonly SignInApprovalRelay _signInApprovals;
	private SignInApprovalPage? _approvalShown;

	public App(OfflineSyncService offlineSync, NativeSessionClient session, WorldNewsSummaryRelay worldNewsSummaries,
		WorldNewsEmbedRelay worldNewsEmbeddings, SentinelAgentMacRelay agentRelay, SignInApprovalRelay signInApprovals)
	{
		InitializeComponent();
		_offlineSync = offlineSync;
		_session = session;
		_worldNewsSummaries = worldNewsSummaries;
		_worldNewsEmbeddings = worldNewsEmbeddings;
		_agentRelay = agentRelay;
		_signInApprovals = signInApprovals;
		_signInApprovals.RequestArrived += request => MainThread.BeginInvokeOnMainThread(() => _ = ShowSignInRequestAsync(request));
	}

	// "Approve on my Mac": the request over whatever tab is open (a newer one replaces it), and a
	// notification in case the app is in the background.
	private async Task ShowSignInRequestAsync(SignInApprovalRequest request)
	{
		if (Windows.FirstOrDefault()?.Page is not { } page) return;
		if (_approvalShown is { } shown) await shown.CloseAsync();
		_approvalShown = new SignInApprovalPage(_signInApprovals, request);
		await page.Navigation.PushModalAsync(_approvalShown);
		await NotifyAsync(request);
	}

	private static async Task NotifyAsync(SignInApprovalRequest request)
	{
#if MACCATALYST
		try
		{
			var center = UserNotifications.UNUserNotificationCenter.Current;
			var (granted, _) = await center.RequestAuthorizationAsync(UserNotifications.UNAuthorizationOptions.Alert | UserNotifications.UNAuthorizationOptions.Sound);
			if (!granted) return;
			var content = new UserNotifications.UNMutableNotificationContent
			{
				Title = "Sign-in request",
				Body = $"{request.Browser} wants to sign in to GWS Business Suite. Open the app to approve with Touch ID.",
				Sound = UserNotifications.UNNotificationSound.Default
			};
			await center.AddNotificationRequestAsync(UserNotifications.UNNotificationRequest.FromIdentifier(request.Id.ToString(), content, null));
		}
		catch (Exception)
		{
			// Notifications are a nicety: the request is already on screen in the app.
		}
#else
		await Task.CompletedTask;
#endif
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
		_worldNewsEmbeddings.Start();
		_agentRelay.Start();
		_signInApprovals.Start();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell());
	}
}