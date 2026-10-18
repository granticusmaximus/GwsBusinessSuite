#if MACCATALYST
using Foundation;
using WebKit;
#endif

namespace GwsBusinessSuite.App;

public partial class MainPage : ContentPage
{
    // TEMPORARY diagnostic - remove once the page-editor loading investigation is done.
    // App Sandbox blocks writes to a literal /tmp path, so this uses the app's own sandboxed
    // container instead (visible from Terminal via DebugLogPath).
    private static readonly string DebugLogPath =
        Path.Combine(FileSystem.Current.AppDataDirectory, "gws-mainpage-debug.log");

    private static void DebugLog(string message)
    {
        try
        {
            File.AppendAllText(DebugLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(DebugLogPath, $"DebugLog FAILED: {ex}{Environment.NewLine}"); } catch { }
        }
    }

    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);

    private readonly DeviceSecretStore _deviceSecretStore;
    private readonly NativeAppAuthService _nativeAppAuthService;
    private bool _connectivitySubscribed;
    private bool _wasOffline;
    private bool _startedLoading;
    private CancellationTokenSource? _navigationTimeoutCts;

    private readonly CameraWatcher _cameraWatcher;

    private readonly TouchIdKeyStore _touchIdKeys;
    private readonly TouchIdLockService _touchIdLock;
    private readonly NativeSessionClient _session;

    public MainPage(DeviceSecretStore deviceSecretStore, NativeAppAuthService nativeAppAuthService, CameraWatcher cameraWatcher,
        TouchIdKeyStore touchIdKeys, TouchIdLockService touchIdLock, NativeSessionClient session)
    {
        _session = session;
        InitializeComponent();
        _deviceSecretStore = deviceSecretStore;
        _nativeAppAuthService = nativeAppAuthService;
        _cameraWatcher = cameraWatcher;
        _touchIdKeys = touchIdKeys;
        _touchIdLock = touchIdLock;
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

#if MACCATALYST
        // Route the Mac window's native titlebar buttons to this page's own handlers while it is
        // the visible tab. Cleared in OnDisappearing so Reload is a no-op while the SentinelGPT
        // tab is showing instead of silently reloading a WebView the user can no longer see.
        MacToolbarActions.Reload = () => OnReloadClicked(this, EventArgs.Empty);
        MacToolbarActions.ConfigureDeviceLogin = () => OnConfigureDeviceLoginClicked(this, EventArgs.Empty);
        MacToolbarActions.SetUpTouchId = () => _ = SetUpTouchIdAsync();
        MacToolbarActions.SignInWithTouchId = () => _ = SignInWithTouchIdAsync(reportProblems: true);
        MacToolbarActions.SyncSelectedTab?.Invoke("MainPage");
#endif

        // Another tab (e.g. Notes after saving) asked to open a page here.
        if (_startedLoading && WorkspaceNavigation.TakePending() is { } pendingUrl)
        {
            WorkspaceView.Source = pendingUrl;
        }

        // Only attempt device login once per launch - re-appearing (e.g. returning from another
        // Shell tab) must never re-show the sign-in prompt over an already-loading/loaded WebView.
        if (!_startedLoading)
        {
            _startedLoading = true;
            await StartWorkspaceAsync();
#if MACCATALYST
            // Overwatch camera watch runs on this Mac's own Ollama, using the Workspace session.
            _cameraWatcher.Start();
#endif
        }
    }

    private async Task StartWorkspaceAsync()
    {
#if MACCATALYST
        await ClearStaleWebViewCacheAsync();

        // Sign in with Touch ID when this Mac has a key; any failure or cancel falls through to
        // the device-secret / browser sign-in below.
        if (_touchIdLock.IsSetUp && await SignInWithTouchIdAsync(reportProblems: false)) return;

        var deviceSecret = await _deviceSecretStore.GetAsync();
        if (!string.IsNullOrWhiteSpace(deviceSecret) && await TryDeviceLoginAsync(deviceSecret) is { } signInUrl)
        {
            WorkspaceView.Source = signInUrl;
            return;
        }
#endif
        // No device secret configured, device login failed/was cancelled, or a non-MacCatalyst
        // platform - fall back to today's behavior exactly: load the WebView and let the user
        // sign in (with MFA) inside it.
        WorkspaceView.Source = AppEndpoints.StartUrl;
    }

#if MACCATALYST
    // A real incident: WKWebsiteDataStore.DefaultDataStore's on-disk cache kept serving a
    // pre-fix response for Overwatch (wrong CSP header) indefinitely - the cache lives in
    // this app's sandbox container, not process memory, so a full quit/relaunch (which the user
    // tried first) does nothing to it, unlike a normal browser tab's cache. Only cache-type data
    // is cleared here - cookies (the login session) and local storage (e.g. the coverage
    // banner's own dismiss preference) are deliberately left alone.
    private static Task ClearStaleWebViewCacheAsync()
    {
        var cacheTypes = new NSSet<NSString>(new NSString[]
        {
            WKWebsiteDataType.DiskCache,
            WKWebsiteDataType.MemoryCache,
            WKWebsiteDataType.OfflineWebApplicationCache,
            WKWebsiteDataType.FetchCache
        });
        return WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypesAsync(cacheTypes, NSDate.DistantPast);
    }

    // Native prompts, not custom XAML - kept intentionally minimal since this is opt-in,
    // single-device tooling, not a polished multi-user login screen. Note DisplayPromptAsync has
    // no masked/secure-entry mode, so the password is briefly visible in the OS alert - an
    // accepted tradeoff for a local, physically-trusted machine, not something to build a custom
    // masked-entry page to avoid.
    // Returns the address to open in the web view (it signs the web view in), or null to fall back
    // to the normal sign-in page.
    private async Task<string?> TryDeviceLoginAsync(string deviceSecret)
    {
        var username = await DisplayPromptAsync("Sign In", "Username", accept: "Next", cancel: "Use browser login instead");
        if (string.IsNullOrWhiteSpace(username)) return null;

        var password = await DisplayPromptAsync("Sign In", $"Password for {username}", accept: "Sign In", cancel: "Cancel");
        if (string.IsNullOrWhiteSpace(password)) return null;

        var result = await _nativeAppAuthService.LoginAsync(deviceSecret, username, password);
        if (!result.Succeeded)
        {
            await DisplayAlertAsync("Sign In Failed", result.ErrorMessage ?? "Could not sign in.", "OK");
            return null;
        }
        DebugLog($"Device sign-in: handoff={result.WebSignInUrl is not null}");
        return result.WebSignInUrl ?? AppEndpoints.StartUrl;
    }
#endif

    // The toolbar's sign-in button: Touch ID first (the recommended way), the device secret as before.
    private async void OnConfigureDeviceLoginClicked(object? sender, EventArgs e)
    {
        var choice = await DisplayActionSheetAsync("Sign-in for this Mac", "Cancel", null,
            _touchIdLock.IsSetUp ? "Set Up Touch ID Again" : "Set Up Touch ID (recommended)", "Device Secret…");
        if (choice is null || choice == "Cancel") return;
        if (choice.StartsWith("Set Up Touch ID", StringComparison.Ordinal))
        {
            await SetUpTouchIdAsync();
            return;
        }

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

#if MACCATALYST
        MacToolbarActions.Reload = null;
        MacToolbarActions.ConfigureDeviceLogin = null;
#endif

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
        DebugLog($"OnWorkspaceNavigating url={args.Url}");
        // Page lock: the web page asks the app for Touch ID with gws-native://unlock or ://enroll.
        if (Uri.TryCreate(args.Url, UriKind.Absolute, out var nativeUri) && nativeUri.Scheme == "gws-native")
        {
            args.Cancel = true;
            _ = HandleNativeRequestAsync(nativeUri.Host);
            return;
        }
        LoadingOverlay.IsVisible = true;
        if (!Uri.TryCreate(args.Url, UriKind.Absolute, out var uri) || AppEndpoints.IsTrusted(uri))
        {
            StartNavigationTimeout();
            return;
        }

        DebugLog($"OnWorkspaceNavigating: untrusted, cancelling and opening externally url={args.Url}");
        args.Cancel = true;
        LoadingOverlay.IsVisible = false;
        _ = Launcher.Default.OpenAsync(uri);
    }

    // Does the Touch ID step, then tells the page (a "gws-native" DOM event). The page doesn't
    // trust the event: it asks the server whether the unlock or the new key is really there.
    private async Task HandleNativeRequestAsync(string action)
    {
        var (ok, message) = action switch
        {
            "unlock" => await _touchIdLock.UnlockAsync(),
            "enroll" => await _touchIdLock.EnrollAsync(),
            _ => (false, "Unknown request.")
        };
        var detail = System.Text.Json.JsonSerializer.Serialize(new { action, ok, message });
        try
        {
            await WorkspaceView.EvaluateJavaScriptAsync($"window.dispatchEvent(new CustomEvent('gws-native', {{ detail: {detail} }}))");
        }
        catch (Exception ex)
        {
            DebugLog($"HandleNativeRequestAsync: couldn't notify the page: {ex.Message}");
        }
    }

    // Set Up Touch ID: creates this Mac's key (Touch ID asked), registers it with Sentinel - after
    // the page-lock PIN when one exists - and then offers to sign in with it straight away, which
    // gives the session the Mac-app sign-in that Content Studio needs.
    private async Task SetUpTouchIdAsync()
    {
#if MACCATALYST
        var (ok, message) = await _touchIdLock.EnrollAsync();
        if (!ok && message.StartsWith("Unlock first", StringComparison.Ordinal))
        {
            var pin = await DisplayPromptAsync("Set Up Touch ID",
                "You already have a page-lock PIN. Enter it to confirm it's you.", accept: "Continue", cancel: "Cancel",
                maxLength: 8, keyboard: Keyboard.Numeric);
            if (string.IsNullOrWhiteSpace(pin)) return;
            if (await _session.UnlockWithPinAsync(pin.Trim()) is { } pinError)
            {
                await DisplayAlertAsync("Set Up Touch ID", pinError, "OK");
                return;
            }
            (ok, message) = await _touchIdLock.EnrollAsync();
        }
        if (!ok)
        {
            await DisplayAlertAsync("Set Up Touch ID", message.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || message.Contains("session", StringComparison.OrdinalIgnoreCase)
                ? $"{message}\n\nSign in on the Workspace tab first, then choose Set Up Touch ID again."
                : message, "OK");
            return;
        }
        Preferences.Default.Set(TouchIdOfferedKey, true);
        var signInNow = await DisplayAlertAsync("Touch ID Is Set Up",
            $"{message} From now on the app signs you in with Touch ID when it opens. Sign in with Touch ID now? That also turns on Mac-only tools like Content Studio.",
            "Sign In Now", "Later");
        if (!signInNow) return;
        await SignInWithTouchIdAsync(reportProblems: true);
#else
        await DisplayAlertAsync("Set Up Touch ID", "Touch ID is available in the Mac app.", "OK");
#endif
    }

    // Signs the web view in with Touch ID, then asks the server what session the web view really
    // has - Content Studio and the other Mac-only tools need the Touch ID (or device) sign-in.
    // Every step goes to the debug log (never cookie values).
    private async Task<bool> SignInWithTouchIdAsync(bool reportProblems)
    {
#if MACCATALYST
        if (!_touchIdLock.IsSetUp)
        {
            if (reportProblems) await DisplayAlertAsync("Sign In with Touch ID", "Touch ID isn't set up on this Mac yet - choose GWS Business Suite > Set Up Touch ID first.", "OK");
            return false;
        }
        // Touch ID + signature here; the web view itself opens the sign-in address, so the session
        // cookie is set in its own store (an HTTP-client cookie can't be handed over on macOS).
        var (url, error) = await _nativeAppAuthService.PrepareTouchIdWebSignInAsync(_touchIdKeys);
        DebugLog($"TouchId sign-in: prepared={url is not null} error={error}");
        if (url is null)
        {
            if (reportProblems) await DisplayAlertAsync("Sign In with Touch ID", error ?? "Touch ID sign-in didn't work.", "OK");
            return false;
        }
        _verifyTouchIdSessionAfterLoad = reportProblems;
        WorkspaceView.Source = url;
        return true;
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    // After a Touch ID sign-in from the menu: once the workspace has loaded, confirm the window's
    // session really is the Mac-app one (what Content Studio needs).
    private bool _verifyTouchIdSessionAfterLoad;

    private async Task VerifyTouchIdSessionAsync(string url)
    {
        if (!_verifyTouchIdSessionAfterLoad || !url.Contains("/admin", StringComparison.OrdinalIgnoreCase)) return;
        _verifyTouchIdSessionAfterLoad = false;
        if (url.Contains("/login", StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlertAsync("Sign In with Touch ID", "Sentinel didn't accept the Touch ID sign-in. Sign in with your password, then choose GWS Business Suite > Set Up Touch ID again.", "OK");
            return;
        }
        var (signedIn, macApp, method) = await _session.GetSessionInfoAsync();
        DebugLog($"TouchId sign-in: web view session signedIn={signedIn} macApp={macApp} amr={method}");
        if (signedIn && !macApp)
            await DisplayAlertAsync("Sign In with Touch ID", $"This window is still using a '{method}' sign-in, so Mac-only tools like Content Studio stay hidden.", "OK");
    }

    // Offered once, after the first ordinary sign-in on a Mac without Touch ID set up.
    private const string TouchIdOfferedKey = "touchid-setup-offered";

    private async Task OfferTouchIdOnceAsync(string url)
    {
#if MACCATALYST
        if (_touchIdLock.IsSetUp || Preferences.Default.Get(TouchIdOfferedKey, false)) return;
        if (!url.Contains("/admin", StringComparison.OrdinalIgnoreCase) || url.Contains("/login", StringComparison.OrdinalIgnoreCase)) return;
        Preferences.Default.Set(TouchIdOfferedKey, true);
        var setUp = await DisplayAlertAsync("Sign In with Touch ID?",
            "Set up Touch ID and the app signs you in with your fingerprint next time - no password or code. It also turns on Mac-only tools like Content Studio. You can do it later from the app menu: GWS Business Suite > Set Up Touch ID.",
            "Set Up Touch ID", "Not Now");
        if (setUp) await SetUpTouchIdAsync();
#else
        await Task.CompletedTask;
#endif
    }

    private void OnWorkspaceNavigated(object? sender, WebNavigatedEventArgs args)
    {
        DebugLog($"OnWorkspaceNavigated url={args.Url} result={args.Result}");
        StopNavigationTimeout();
        LoadingOverlay.IsVisible = false;
        if (args.Result == WebNavigationResult.Success)
        {
            StatusOverlay.IsVisible = false;
            _ = OfferTouchIdOnceAsync(args.Url);
            _ = VerifyTouchIdSessionAsync(args.Url);
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
        DebugLog("StartNavigationTimeout");
        StopNavigationTimeout();
        var cts = new CancellationTokenSource();
        _navigationTimeoutCts = cts;
        _ = Task.Delay(NavigationTimeout, cts.Token).ContinueWith(
            task =>
            {
                if (task.IsCanceled) return;
                DebugLog("Navigation timeout FIRED");
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
