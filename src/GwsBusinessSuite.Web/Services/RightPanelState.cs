namespace GwsBusinessSuite.Web.Services;

// The right edge is shared by the SentinelGPT panel and a page's record inspector (shell
// redesign R3). They sit side by side only where there's room for both (1920px and wider,
// gwsAssistant.wide in app.js); otherwise opening one closes the other. Same circuit, like
// AssistantContext.
public sealed class RightPanelState
{
    public event Action? InspectorOpened;
    public event Action? AssistantOpened;

    public bool IsAssistantOpen { get; private set; }

    public void NotifyInspectorOpened() => InspectorOpened?.Invoke();

    public void NotifyAssistantOpened()
    {
        IsAssistantOpen = true;
        AssistantOpened?.Invoke();
    }

    public void NotifyAssistantClosed() => IsAssistantOpen = false;
}
