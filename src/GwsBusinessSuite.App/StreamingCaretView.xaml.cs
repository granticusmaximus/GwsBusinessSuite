namespace GwsBusinessSuite.App;

// The caret that trails streaming text. Same self-managing lifecycle as TypingIndicatorView:
// it animates only while visible, so a transcript holding dozens of finished messages isn't
// running dozens of idle animation loops.
public partial class StreamingCaretView : ContentView
{
    private const uint FadeMilliseconds = 480;

    private CancellationTokenSource? _loop;

    public StreamingCaretView()
    {
        InitializeComponent();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IsVisible))
            {
                if (IsVisible) Start(); else Stop();
            }
        };
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = new CancellationTokenSource();
        _ = PulseAsync(_loop.Token);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        Caret.CancelAnimations();
        Caret.Opacity = 0.9;
    }

    private async Task PulseAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Caret.FadeToAsync(0.15, FadeMilliseconds, Easing.SinInOut);
                if (cancellationToken.IsCancellationRequested) break;
                await Caret.FadeToAsync(0.9, FadeMilliseconds, Easing.SinInOut);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop().
        }
    }
}
