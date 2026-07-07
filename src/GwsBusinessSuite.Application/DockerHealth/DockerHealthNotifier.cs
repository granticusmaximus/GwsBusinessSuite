namespace GwsBusinessSuite.Application.DockerHealth;

/// <summary>
/// App-wide in-memory pub/sub, registered as a singleton, so the background health
/// monitor can push a new alert to every connected Blazor Server circuit's
/// <c>NotificationBell</c> component without any extra websocket/JS plumbing - each
/// circuit's own SignalR connection delivers the resulting re-render automatically.
/// </summary>
public sealed class DockerHealthNotifier
{
    public event Action<DockerHealthAlertView>? OnAlert;

    public void Publish(DockerHealthAlertView alert) => OnAlert?.Invoke(alert);
}
