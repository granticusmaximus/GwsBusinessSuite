namespace GwsBusinessSuite.App;

public partial class MainPage : ContentPage
{
	private bool _connectivitySubscribed;

	public MainPage()
	{
		InitializeComponent();
		WorkspaceView.Source = AppEndpoints.StartUrl;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		if (!_connectivitySubscribed)
		{
			Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
			_connectivitySubscribed = true;
		}
		UpdateConnectivity(Connectivity.Current.NetworkAccess);
	}

	protected override void OnDisappearing()
	{
		if (_connectivitySubscribed)
		{
			Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
			_connectivitySubscribed = false;
		}
		base.OnDisappearing();
	}

	private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs args) =>
		MainThread.BeginInvokeOnMainThread(() => UpdateConnectivity(args.NetworkAccess));

	private void UpdateConnectivity(NetworkAccess access)
	{
		var offline = access != NetworkAccess.Internet;
		StatusBanner.IsVisible = offline;
		StatusMessage.Text = offline ? "You are offline. Sentinel will reconnect when internet access returns." : string.Empty;
	}

	private void OnWorkspaceNavigating(object? sender, WebNavigatingEventArgs args)
	{
		LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = true;
		if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || AppEndpoints.IsTrusted(uri)) return;

		args.Cancel = true;
		LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = false;
		_ = Launcher.Default.OpenAsync(uri);
	}

	private void OnWorkspaceNavigated(object? sender, WebNavigatedEventArgs args)
	{
		LoadingIndicator.IsVisible = LoadingIndicator.IsRunning = false;
		BackButton.IsEnabled = WorkspaceView.CanGoBack;
		if (args.Result == WebNavigationResult.Success) return;

		StatusMessage.Text = "The workspace could not be loaded. Check the server address and connection.";
		StatusBanner.IsVisible = true;
	}

	private void OnBackClicked(object? sender, EventArgs e)
	{
		if (WorkspaceView.CanGoBack) WorkspaceView.GoBack();
	}

	private void OnHomeClicked(object? sender, EventArgs e) => WorkspaceView.Source = AppEndpoints.StartUrl;

	private void OnRefreshClicked(object? sender, EventArgs e)
	{
		StatusBanner.IsVisible = false;
		WorkspaceView.Reload();
	}
}
