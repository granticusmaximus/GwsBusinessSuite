namespace GwsBusinessSuite.Application.Growth;

/// <summary>
/// App-wide in-memory pub/sub, registered as a singleton, so the scheduled publishing
/// background service can push a new permanent-failure alert to every connected Blazor
/// Server circuit's <c>NotificationBell</c> component without any extra websocket/JS
/// plumbing - each circuit's own SignalR connection delivers the resulting re-render
/// automatically. Mirrors GwsBusinessSuite.Application.DockerHealth.DockerHealthNotifier.
/// </summary>
public sealed class SocialPublishingNotifier
{
    public event Action<SocialPostAlertView>? OnAlert;

    public void Publish(SocialPostAlertView alert) => OnAlert?.Invoke(alert);
}
