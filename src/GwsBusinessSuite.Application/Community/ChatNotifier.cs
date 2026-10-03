namespace GwsBusinessSuite.Application.Community;

/// <summary>
/// App-wide in-memory pub/sub for new chat messages, registered as a singleton, so a message sent
/// from one Blazor Server circuit reaches every recipient's open circuit (the NotificationBell and
/// any open Messages page) immediately, without polling. Mirrors SocialPublishingNotifier.
/// </summary>
public sealed class ChatNotifier : IChatNotifier
{
    public event Action<ChatMessageNotice>? OnMessage;

    public void Publish(ChatMessageNotice notice) => OnMessage?.Invoke(notice);
}
