namespace GwsBusinessSuite.Application.Support;

/// <summary>
/// App-wide in-memory updates for open Support lists. Blazor Server circuits already have a
/// SignalR connection, so publishing the complete row lets them redraw without polling or a
/// second, potentially concurrent DbContext read.
/// </summary>
public sealed class SupportTicketNotifier
{
    public event Action<SupportTicketView>? Changed;

    public void Publish(SupportTicketView ticket) => Changed?.Invoke(ticket);
}
