namespace GwsBusinessSuite.App;

// A three-dot "composing" animation. Runs only while it is actually on screen: an animation
// looping behind a hidden view still costs a frame callback every 90ms and, on a page that can
// stream for minutes, that is a real waste rather than a theoretical one.
public partial class TypingIndicatorView : ContentView
{
    // Slow enough to read as deliberate rather than frantic. Each dot runs the same cycle a
    // third of a period apart, which is what produces the travelling wave.
    private static readonly uint RiseMilliseconds = 320;
    private static readonly int PhaseOffsetMilliseconds = 180;

    private CancellationTokenSource? _loop;

    public TypingIndicatorView()
    {
        InitializeComponent();
        // Starts and stops with visibility, so callers only bind IsVisible and never have to
        // remember to stop it - a forgotten stop is exactly how these leak.
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
        _ = RunAsync(_loop.Token);
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        foreach (var dot in Dots())
        {
            dot.CancelAnimations();
            dot.Opacity = 0.35;
            dot.TranslationY = 0;
        }
    }

    private View[] Dots() => [Dot1, Dot2, Dot3];

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var dots = Dots();
        // Stagger the start rather than the cycle, so all three stay in lockstep afterwards
        // instead of drifting apart as timing jitter accumulates over a long stream.
        for (var index = 0; index < dots.Length; index++)
        {
            var dot = dots[index];
            var delay = index * PhaseOffsetMilliseconds;
            _ = PulseAsync(dot, delay, cancellationToken);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stop() was called - the per-dot loops observe the same token and unwind too.
        }
    }

    private static async Task PulseAsync(View dot, int startDelay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(startDelay, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.WhenAll(
                    dot.FadeToAsync(1, RiseMilliseconds, Easing.CubicOut),
                    dot.TranslateToAsync(0, -3, RiseMilliseconds, Easing.CubicOut));
                if (cancellationToken.IsCancellationRequested) break;
                await Task.WhenAll(
                    dot.FadeToAsync(0.35, RiseMilliseconds, Easing.CubicIn),
                    dot.TranslateToAsync(0, 0, RiseMilliseconds, Easing.CubicIn));
                // Completes the period so every dot's cycle is the same length regardless of
                // how long the two legs above actually took.
                await Task.Delay(PhaseOffsetMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop().
        }
    }
}
