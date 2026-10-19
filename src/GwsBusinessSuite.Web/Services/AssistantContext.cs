using GwsBusinessSuite.Application.Assistant;
using Microsoft.AspNetCore.Components;

namespace GwsBusinessSuite.Web.Services;

// The bridge between an admin page and the SentinelGPT side panel, one per circuit (the page
// and the panel are separate interactive roots on the same circuit). A page describes what's
// open and can offer to take a draft ("Use as reply"). Each description is stamped with the
// page's path and only counts while the browser is still on that path, so a page that sets
// nothing never inherits the previous page's record.
public sealed class AssistantContext(NavigationManager navigation)
{
    private Registration? _current;

    public event Action? Changed;

    // apply: called with a draft the user accepted from the panel; applyLabel names the button.
    public void Set(AssistantPageContext page, Func<string, Task>? apply = null, string? applyLabel = null)
    {
        _current = new Registration(CurrentPath(), page, apply, apply is null ? null : applyLabel ?? "Use this draft");
        Changed?.Invoke();
    }

    // A page clears its description when its record closes (e.g. no ticket selected any more).
    public void Clear()
    {
        if (_current is null) return;
        _current = null;
        Changed?.Invoke();
    }

    public Registration? Current => _current is { } registration && registration.Path == CurrentPath() ? registration : null;

    private string CurrentPath()
    {
        var relative = navigation.ToBaseRelativePath(navigation.Uri);
        var end = relative.IndexOfAny(['?', '#']);
        return (end < 0 ? relative : relative[..end]).TrimEnd('/').ToLowerInvariant();
    }

    public sealed record Registration(string Path, AssistantPageContext Page, Func<string, Task>? Apply, string? ApplyLabel);
}
