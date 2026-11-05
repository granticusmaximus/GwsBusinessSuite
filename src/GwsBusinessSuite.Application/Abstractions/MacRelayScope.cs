namespace GwsBusinessSuite.Application.Abstractions;

// With server AI off (ServerAi None, since 31-10-26) the droplet has no model. Work on shared, public
// data - bill and vote overviews, news hot takes, county meeting summaries - may run on an Admin's
// signed-in Mac instead, through the relay SentinelGPT's agent uses (MacRelayOllamaService). Only a
// call made inside ForSharedWork() goes there. Never wrap a person's own content (their chats,
// drafts, notes): that only ever goes to a Mac signed in as them (RelayedOllamaService).
public static class MacRelayScope
{
    private static readonly AsyncLocal<bool> Allowed = new();

    public static bool SharedWorkAllowed => Allowed.Value;

    public static IDisposable ForSharedWork()
    {
        var previous = Allowed.Value;
        Allowed.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => Allowed.Value = previous;
    }
}
