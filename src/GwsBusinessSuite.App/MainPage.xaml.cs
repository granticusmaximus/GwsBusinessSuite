namespace GwsBusinessSuite.App;

public partial class MainPage : ContentPage
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);

    private readonly DeviceSecretStore _deviceSecretStore;
    private readonly NativeAppAuthService _nativeAppAuthService;
    private bool _connectivitySubscribed;
    private bool _wasOffline;
    private bool _startedLoading;
    private CancellationTokenSource? _navigationTimeoutCts;

    public MainPage(DeviceSecretStore deviceSecretStore, NativeAppAuthService nativeAppAuthService)
    {
        InitializeComponent();
        _deviceSecretStore = deviceSecretStore;
        _nativeAppAuthService = nativeAppAuthService;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_connectivitySubscribed)
        {
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
            _connectivitySubscribed = true;
        }

        UpdateConnectivity(Connectivity.Current.NetworkAccess);

        // Only attempt device login once per launch - re-appearing (e.g. returning from another
        // Shell tab) must never re-show the sign-in prompt over an already-loading/loaded WebView.
        if (!_startedLoading)
        {
            _startedLoading = true;
            await StartWorkspaceAsync();
        }
    }

    private async Task StartWorkspaceAsync()
    {
#if MACCATALYST
        var deviceSecret = await _deviceSecretStore.GetAsync();
        if (!string.IsNullOrWhiteSpace(deviceSecret) && await TryDeviceLoginAsync(deviceSecret))
        {
            WorkspaceView.Source = AppEndpoints.StartUrl;
            return;
        }
#endif
        // No device secret configured, device login failed/was cancelled, or a non-MacCatalyst
        // platform - fall back to today's behavior exactly: load the WebView and let the user
        // sign in (with MFA) inside it.
        WorkspaceView.Source = AppEndpoints.StartUrl;
    }

#if MACCATALYST
    // Native prompts, not custom XAML - kept intentionally minimal since this is opt-in,
    // single-device tooling, not a polished multi-user login screen. Note DisplayPromptAsync has
    // no masked/secure-entry mode, so the password is briefly visible in the OS alert - an
    // accepted tradeoff for a local, physically-trusted machine, not something to build a custom
    // masked-entry page to avoid.
    private async Task<bool> TryDeviceLoginAsync(string deviceSecret)
    {
        var username = await DisplayPromptAsync("Sign In", "Username", accept: "Next", cancel: "Use browser login instead");
        if (string.IsNullOrWhiteSpace(username)) return false;

        var password = await DisplayPromptAsync("Sign In", $"Password for {username}", accept: "Sign In", cancel: "Cancel");
        if (string.IsNullOrWhiteSpace(password)) return false;

        var result = await _nativeAppAuthService.LoginAsync(deviceSecret, username, password);
        if (!result.Succeeded)
        {
            await DisplayAlertAsync("Sign In Failed", result.ErrorMessage ?? "Could not sign in.", "OK");
            return false;
        }

        await NativeCookieInjector.InjectCookiesAsync(result.SetCookieHeaders, AppEndpoints.BaseUrl);
        return true;
    }
#endif

    private async void OnConfigureDeviceLoginClicked(object? sender, EventArgs e)
    {
        var secret = await DisplayPromptAsync(
            "Device Login",
            "Enter the device secret configured on the server (NATIVE_APP_DEVICE_SECRET). Leave blank and confirm to remove a previously saved secret.");
        if (secret is null) return;

        if (string.IsNullOrWhiteSpace(secret))
        {
            _deviceSecretStore.Remove();
            await DisplayAlertAsync("Device Login", "Device login disabled for this app. It will use the normal browser login next launch.", "OK");
            return;
        }

        await _deviceSecretStore.SetAsync(secret.Trim());
        await DisplayAlertAsync("Device Login", "Saved. Relaunch the app to sign in without the browser MFA challenge.", "OK");
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
