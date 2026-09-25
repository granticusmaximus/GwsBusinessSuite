namespace GwsBusinessSuite.Application.ThreatIntel;

public interface IOtxThreatIntelService
{
    // GetSubscribedPulsesAsync(null) lists the configured account's own subscribed pulses;
    // a non-null, non-whitespace searchTerm instead searches OTX's public pulse index by
    // keyword. Returns an empty list (never throws) when no API key is configured, or on any
    // HTTP/parsing failure - callers show a plain "not configured"/"unavailable" message rather
    // than a stack trace.
    Task<IReadOnlyList<OtxPulse>> GetPulsesAsync(string? searchTerm, CancellationToken cancellationToken = default);
}

public sealed record OtxPulse(
    string Id,
    string Name,
    string Description,
    string AuthorName,
    DateTimeOffset? Created,
    IReadOnlyList<string> Tags,
    int IndicatorCount)
{
    public string PulseUrl => $"https://otx.alienvault.com/pulse/{Id}";
}
