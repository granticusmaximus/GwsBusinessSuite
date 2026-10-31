namespace GwsBusinessSuite.App;

// The rings around the fingerprint on the Touch ID screens (31-10-26): each grows and fades from the
// badge, over and over, until stopped. Two rings half a beat apart read as a slow pulse.
public static class TouchIdPulse
{
    public static async Task RunAsync(VisualElement ring, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                ring.Scale = 0.85;
                ring.Opacity = 0.7;
                await Task.WhenAll(ring.ScaleToAsync(1.45, 1600, Easing.CubicOut), ring.FadeToAsync(0, 1600, Easing.CubicOut));
            }
        }
        catch (TaskCanceledException)
        {
        }
        ring.Opacity = 0;
    }
}
