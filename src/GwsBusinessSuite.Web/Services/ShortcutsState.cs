namespace GwsBusinessSuite.Web.Services;

// Tells the rail's shortcuts (RailShortcuts, an interactive island in the static layout) that a
// page pinned, unpinned or opened a record, on the same circuit - the way AssistantContext links
// a page and the SentinelGPT panel.
public sealed class ShortcutsState
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
