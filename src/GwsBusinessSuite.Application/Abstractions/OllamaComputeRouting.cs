namespace GwsBusinessSuite.Application.Abstractions;

public enum OllamaComputeRoute
{
    Auto,
    PreferMac,
    PreferDroplet,
}

// An ambient hint for a whole use case, including streamed async iterators. Most callers stay Auto;
// Content Studio marks full drafts and revisions as Mac work, while small/background operations can
// explicitly prefer the always-on droplet. Failover still applies whichever route is chosen.
public static class OllamaComputeRouting
{
    private static readonly AsyncLocal<OllamaComputeRoute?> CurrentValue = new();
    private static readonly AsyncLocal<TimeSpan?> PreferredAttemptTimeoutValue = new();

    public static OllamaComputeRoute Current => CurrentValue.Value ?? OllamaComputeRoute.Auto;
    public static TimeSpan? PreferredAttemptTimeout => PreferredAttemptTimeoutValue.Value;

    public static IDisposable Use(OllamaComputeRoute route, TimeSpan? preferredAttemptTimeout = null)
    {
        var previous = CurrentValue.Value;
        var previousTimeout = PreferredAttemptTimeoutValue.Value;
        CurrentValue.Value = route;
        PreferredAttemptTimeoutValue.Value = preferredAttemptTimeout;
        return new Scope(previous, previousTimeout);
    }

    private sealed class Scope(OllamaComputeRoute? previous, TimeSpan? previousTimeout) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentValue.Value = previous;
            PreferredAttemptTimeoutValue.Value = previousTimeout;
        }
    }
}
