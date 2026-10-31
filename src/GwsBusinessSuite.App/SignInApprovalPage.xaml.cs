namespace GwsBusinessSuite.App;

public partial class SignInApprovalPage : ContentPage
{
    private static readonly Color Amber = Color.FromArgb("#F59E0B");
    private static readonly Color Green = Color.FromArgb("#22C55E");
    private static readonly Color Red = Color.FromArgb("#EF4444");
    private readonly SignInApprovalRelay _relay;
    private readonly SignInApprovalRequest _request;
    private readonly CancellationTokenSource _pulse = new();
    private readonly List<Button> _numbers = [];
    private bool _finished;

    public SignInApprovalPage(SignInApprovalRelay relay, SignInApprovalRequest request)
    {
        InitializeComponent();
        _relay = relay;
        _request = request;
        TitleLabel.Text = $"Sign in on {request.Browser}?";
        foreach (var number in request.Numbers)
        {
            var button = new Button
            {
                Text = number.ToString(),
                WidthRequest = 84,
                HeightRequest = 64,
                FontSize = 26,
                FontAttributes = FontAttributes.Bold,
                BackgroundColor = Color.FromArgb("#292524"),
                TextColor = Color.FromArgb("#FAFAF9"),
                BorderColor = Color.FromArgb("#44403C"),
                BorderWidth = 1,
                CornerRadius = 12
            };
            button.Clicked += async (_, _) => await ApproveAsync(number);
            _numbers.Add(button);
            NumberRow.Children.Add(button);
        }
        UpdateDetail();
    }

    // Closes itself when another request replaces it or this one runs out.
    public Guid RequestId => _request.Id;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = TouchIdPulse.RunAsync(PulseOuter, TimeSpan.Zero, _pulse.Token);
        _ = TouchIdPulse.RunAsync(PulseInner, TimeSpan.FromMilliseconds(800), _pulse.Token);
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (_finished) return false;
            UpdateDetail();
            if (DateTimeOffset.UtcNow < _request.ExpiresAt) return true;
            _ = FinishAsync(Red, "This request ran out. Start again in the browser if it was you.", TimeSpan.FromSeconds(3));
            return false;
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _finished = true;
        _pulse.Cancel();
    }

    private void UpdateDetail()
    {
        var left = _request.ExpiresAt - DateTimeOffset.UtcNow;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_request.Address)) parts.Add(_request.Address!);
        parts.Add($"asked at {_request.StartedAt.ToLocalTime():HH:mm}");
        if (left > TimeSpan.Zero) parts.Add($"{(int)left.TotalMinutes}:{left.Seconds:00} left");
        DetailLabel.Text = string.Join(" · ", parts);
    }

    private async Task ApproveAsync(int number)
    {
        if (_finished) return;
        SetNumbersEnabled(false);
        DeclineButton.IsEnabled = false;
        ShowStatus(Amber, "Touch the sensor to approve…");
        var error = await _relay.ApproveAsync(_request, number);
        if (error is null)
        {
            await FinishAsync(Green, "Approved - that browser is signing in now.", TimeSpan.FromSeconds(1.6));
            return;
        }
        // Touch ID cancelled: the request is still open, so they can try again. Anything else ends it.
        if (error.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(Red, error);
            SetNumbersEnabled(true);
            DeclineButton.IsEnabled = true;
            return;
        }
        await FinishAsync(Red, error, TimeSpan.FromSeconds(3.5));
    }

    private async void OnDeclineClicked(object? sender, EventArgs e)
    {
        if (_finished) return;
        DeclineButton.IsEnabled = false;
        SetNumbersEnabled(false);
        await _relay.DeclineAsync(_request);
        await FinishAsync(Color.FromArgb("#A8A29E"), "Declined - nothing was signed in.", TimeSpan.FromSeconds(1.2));
    }

    private async Task FinishAsync(Color tone, string message, TimeSpan linger)
    {
        _finished = true;
        _pulse.Cancel();
        SetNumbersEnabled(false);
        Badge.Stroke = tone;
        ShowStatus(tone, message);
        await Task.Delay(linger);
        await CloseAsync();
    }

    public async Task CloseAsync()
    {
        _finished = true;
        if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync();
    }

    private void ShowStatus(Color tone, string message)
    {
        StatusLabel.TextColor = tone == Amber ? Color.FromArgb("#FBBF24") : tone;
        StatusLabel.Text = message;
        StatusLabel.IsVisible = true;
    }

    private void SetNumbersEnabled(bool enabled)
    {
        foreach (var button in _numbers) button.IsEnabled = enabled;
    }
}
