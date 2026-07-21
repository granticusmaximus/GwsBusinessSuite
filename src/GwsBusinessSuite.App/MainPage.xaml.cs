namespace GwsBusinessSuite.App;

public partial class MainPage : ContentPage
{
    private bool _connectivitySubscribed;
    private bool _wasOffline;

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
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || AppEndpoints.IsTrusted(uri)) return;

        args.Cancel = true;
        LoadingOverlay.IsVisible = false;
        _ = Launcher.Default.OpenAsync(uri);
    }

    private void OnWorkspaceNavigated(object? sender, WebNavigatedEventArgs args)
    {
        LoadingOverlay.IsVisible = false;
        if (args.Result == WebNavigationResult.Success)
        {
            StatusOverlay.IsVisible = false;
            return;
        }

        ShowUnavailable("The workspace could not be loaded. Check the server address and connection.");
    }

    private void ShowUnavailable(string message)
    {
        LoadingOverlay.IsVisible = false;
        StatusMessage.Text = message;
        StatusOverlay.IsVisible = true;
    }

    private void OnRefreshClicked(object? sender, EventArgs e) => ReloadWorkspace();

    private void ReloadWorkspace()
    {
        StatusOverlay.IsVisible = false;
        LoadingOverlay.IsVisible = true;
        WorkspaceView.Source = AppEndpoints.StartUrl;
    }
}
