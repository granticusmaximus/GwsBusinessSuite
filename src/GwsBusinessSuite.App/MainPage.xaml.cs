namespace GwsBusinessSuite.App;

public partial class MainPage : ContentPage
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);

    private bool _connectivitySubscribed;
    private bool _wasOffline;
    private CancellationTokenSource? _navigationTimeoutCts;

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

    protected override bool OnBackButtonPressed()
    {
        if (!WorkspaceView.CanGoBack) return base.OnBackButtonPressed();

        WorkspaceView.GoBack();
        return true;
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs args) =>
        MainThread.BeginInvokeOnMainThread(() => UpdateConnectivity(args.NetworkAccess));

    private void UpdateConnectivity(NetworkAccess access)
    {
        var offline = access != NetworkAccess.Internet;
        if (offline)
        {
            _wasOffline = true;
            ShowUnavailable("You are offline. Sentinel will reconnect when internet access returns.");
            return;
        }

        if (!_wasOffline) return;

        _wasOffline = false;
        ReloadWorkspace();
    }

    private void OnWorkspaceNavigating(object? sender, WebNavigatingEventArgs args)
    {
        LoadingOverlay.IsVisible = true;
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || AppEndpoints.IsTrusted(uri))
        {
            StartNavigationTimeout();
            return;
        }

        args.Cancel = true;
        LoadingOverlay.IsVisible = false;
        _ = Launcher.Default.OpenAsync(uri);
    }

    private void OnWorkspaceNavigated(object? sender, WebNavigatedEventArgs args)
    {
        StopNavigationTimeout();
        LoadingOverlay.IsVisible = false;
        if (args.Result == WebNavigationResult.Success)
        {
            StatusOverlay.IsVisible = false;
            return;
        }

        ShowUnavailable("The workspace could not be loaded. Check the server address and connection.");
    }

    // A page that never fires Navigated (e.g. an admin page whose embedded iframe hangs waiting
    // on a slow or unreachable backend) used to leave LoadingOverlay showing forever - it has no
    // controls of its own, and InputTransparent="False" blocks every tap on the WebView beneath
    // it, so there was no way back into the app short of force-quitting. Both this timeout and
    // the overlay's own Cancel button exist so that can never happen again.
    private void StartNavigationTimeout()
    {
        StopNavigationTimeout();
        var cts = new CancellationTokenSource();
        _navigationTimeoutCts = cts;
        _ = Task.Delay(NavigationTimeout, cts.Token).ContinueWith(
            task =>
            {
                if (task.IsCanceled) return;
                MainThread.BeginInvokeOnMainThread(() =>
                    ShowUnavailable("This page is taking too long to load. Check the connection and try again."));
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void StopNavigationTimeout()
    {
        _navigationTimeoutCts?.Cancel();
        _navigationTimeoutCts?.Dispose();
        _navigationTimeoutCts = null;
    }

    private void OnCancelLoadingClicked(object? sender, EventArgs e)
    {
        StopNavigationTimeout();
        if (WorkspaceView.CanGoBack)
        {
            LoadingOverlay.IsVisible = false;
            WorkspaceView.GoBack();
        }
        else
        {
            ReloadWorkspace();
        }
    }

    private void ShowUnavailable(string message)
    {
        StopNavigationTimeout();
        LoadingOverlay.IsVisible = false;
        StatusMessage.Text = message;
        StatusOverlay.IsVisible = true;
    }

    private void OnRefreshClicked(object? sender, EventArgs e) => ReloadWorkspace();

    // Reloads whatever page is currently showing (like a browser refresh) rather than
    // ReloadWorkspace()'s "go back to the start URL" - for a stuck SignalR circuit or a stale
    // render on a page the user has already navigated into, jumping back to the start page would
    // lose their place for no reason.
    private void OnReloadClicked(object? sender, EventArgs e)
    {
        StopNavigationTimeout();
        StatusOverlay.IsVisible = false;
        LoadingOverlay.IsVisible = true;
        WorkspaceView.Reload();
    }

    private void ReloadWorkspace()
    {
        StatusOverlay.IsVisible = false;
        LoadingOverlay.IsVisible = true;
        WorkspaceView.Source = AppEndpoints.StartUrl;
    }
}
