namespace GwsBusinessSuite.Application.Diagnostics;

// Logging Phase 2, part 1 (13-11-26): warnings whose cause is already known and expected, closed by
// rule with no AI. Each reason says why it isn't a bug (the traps listed in AGENTS.md).
public static class KnownCauses
{
    public static string? Match(string? exceptionType, string? source, string? title, string? stack)
    {
        var text = string.Join('\n', exceptionType, source, title, stack);
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("ServerAiUnavailableException"))
            return "No AI model was reachable at that moment. Expected: background AI waits and retries (AGENTS.md: ServerAiUnavailableException means wait).";
        if (Has("RefusedHostPause") || Has("refused this server"))
            return "A site refused the server (403/405), so it's left alone for a day. Expected: refusals are respected, never worked around (AGENTS.md).";
        if (Has("smtp") && (Has("timed out") || Has("timeout") || Has("Unable to connect") || Has("SocketException") || Has("ports 25")))
            return "Outgoing SMTP is blocked on the droplet by DigitalOcean (ports 25, 465, 587). Expected: mail from the server goes through the Gmail API (AGENTS.md).";
        if (Has("congress.gov") && (Has("429") || Has("DEMO_KEY") || Has("rate limit") || Has("rate-limit")))
            return "congress.gov's shared DEMO_KEY allows about 50 requests a day. Expected: the Federal Civic feed slows to fit it; CongressApi__ApiKey lifts the limit.";
        return null;
    }
}
