using System.Collections.Concurrent;
using GwsBusinessSuite.Application.LiveShow;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.AspNetCore.SignalR;

namespace GwsBusinessSuite.Web.Hubs;

// Pure WebRTC signaling relay for a direct broadcaster<->viewer mesh (no media ever
// flows through the server/hub itself - only SDP offers/answers and ICE candidates).
// Sized for the "a handful of invited viewers" scale this feature was scoped for: the
// broadcaster's browser opens one RTCPeerConnection per viewer directly, so this only
// needs to route messages by SignalR connectionId, not run any actual media server.
public sealed class LiveShowHub(ILiveShowService liveShowService) : Hub
{
    // sessionId -> the broadcaster's current connectionId, so a joining viewer can be
    // introduced to the right peer. connectionId -> sessionId lets OnDisconnectedAsync
    // clean up either side without the client having to tell us who it was.
    private static readonly ConcurrentDictionary<Guid, string> BroadcasterConnections = new();
    private static readonly ConcurrentDictionary<string, Guid> ConnectionSessions = new();

    public async Task<bool> JoinAsBroadcaster(Guid sessionId)
    {
        if (Context.User?.IsInRole(AppRoles.Admin) != true)
        {
            return false;
        }

        var session = await liveShowService.GetActiveSessionAsync();
        if (session is null || session.Id != sessionId)
        {
            return false;
        }

        BroadcasterConnections[sessionId] = Context.ConnectionId;
        ConnectionSessions[Context.ConnectionId] = sessionId;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(sessionId));
        return true;
    }

    // Returns the sessionId (as a string) on success so the viewer page can display/track
    // it, or null if the invite token is missing, expired, or the show has already ended.
    public async Task<string?> JoinAsViewer(string inviteToken)
    {
        var session = await liveShowService.ValidateInviteTokenAsync(inviteToken);
        if (session is null)
        {
            return null;
        }

        ConnectionSessions[Context.ConnectionId] = session.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(session.Id));

        if (BroadcasterConnections.TryGetValue(session.Id, out var broadcasterConnectionId))
        {
            await Clients.Client(broadcasterConnectionId).SendAsync("ViewerJoined", Context.ConnectionId);
        }

        return session.Id.ToString();
    }

    public Task SendOffer(string targetConnectionId, string sdp) =>
        RelaySignalAsync(targetConnectionId, "ReceiveOffer", sdp);

    public Task SendAnswer(string targetConnectionId, string sdp) =>
        RelaySignalAsync(targetConnectionId, "ReceiveAnswer", sdp);

    public Task SendIceCandidate(string targetConnectionId, string candidateJson) =>
        RelaySignalAsync(targetConnectionId, "ReceiveIceCandidate", candidateJson);

    // The hub is AllowAnonymous (viewers join via an unauthenticated invite link), so
    // without this check any connected client could relay a fabricated offer/answer/ICE
    // candidate to an arbitrary connectionId. Requiring both ends to have already joined
    // the *same* session (via JoinAsBroadcaster/JoinAsViewer, which populate
    // ConnectionSessions) confines relaying to peers that are actually part of that show.
    private Task RelaySignalAsync(string targetConnectionId, string method, string payload)
    {
        if (!ConnectionSessions.TryGetValue(Context.ConnectionId, out var callerSessionId) ||
            !ConnectionSessions.TryGetValue(targetConnectionId, out var targetSessionId) ||
            callerSessionId != targetSessionId)
        {
            return Task.CompletedTask;
        }

        return Clients.Client(targetConnectionId).SendAsync(method, Context.ConnectionId, payload);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectionSessions.TryRemove(Context.ConnectionId, out var sessionId))
        {
            if (BroadcasterConnections.TryGetValue(sessionId, out var broadcasterConnectionId)
                && broadcasterConnectionId == Context.ConnectionId)
            {
                BroadcasterConnections.TryRemove(sessionId, out _);
                await Clients.Group(GroupName(sessionId)).SendAsync("BroadcasterLeft");

                // Safety net for a crash/force-quit that never called the normal StopAsync
                // shutdown path - idempotent, so this is also a harmless no-op on a clean
                // shutdown where StopAsync already finalized everything itself.
                await liveShowService.HandleBroadcasterDisconnectedAsync(sessionId);
            }
            else if (BroadcasterConnections.TryGetValue(sessionId, out var currentBroadcasterConnectionId))
            {
                await Clients.Client(currentBroadcasterConnectionId).SendAsync("ViewerLeft", Context.ConnectionId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private static string GroupName(Guid sessionId) => $"live-show-{sessionId:N}";
}
