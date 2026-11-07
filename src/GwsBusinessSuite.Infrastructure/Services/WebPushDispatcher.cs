using System.Collections.Concurrent;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.SentinelAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Sends the bell's notifications to people's devices as Web Push (PWA phase 2, 07-11-26): the same
// sources the bell listens to, the same words (NotificationInboxItems), to whoever the bell shows
// them to. Container and social-post alerts go to admins; the rest to the person they're for.
public sealed class WebPushDispatcher(
    IWebPushSender sender,
    DockerHealthNotifier docker,
    SocialPublishingNotifier social,
    ChatNotifier chat,
    OverwatchAreaNotifier overwatch,
    WorldNewsService worldNews,
    BoardNotificationNotifier boards,
    SentinelAgentTurns agentTurns,
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<WebPushDispatcher> logger) : IHostedService
{
    // What's been pushed (kind:id -> when), so an event that brings older items again (boards,
    // SentinelGPT jobs) doesn't push them twice.
    private readonly ConcurrentDictionary<string, long> _sent = new(StringComparer.Ordinal);
    private static readonly TimeSpan Remember = TimeSpan.FromDays(1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        docker.OnAlert += OnDocker;
        social.OnAlert += OnSocial;
        chat.OnMessage += OnChat;
        overwatch.OnAlert += OnOverwatch;
        worldNews.AlertCreated += OnWorldNews;
        boards.OnNotified += OnBoards;
        agentTurns.JobFinishedUnwatched += OnJobFinished;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        docker.OnAlert -= OnDocker;
        social.OnAlert -= OnSocial;
        chat.OnMessage -= OnChat;
        overwatch.OnAlert -= OnOverwatch;
        worldNews.AlertCreated -= OnWorldNews;
        boards.OnNotified -= OnBoards;
        agentTurns.JobFinishedUnwatched -= OnJobFinished;
        return Task.CompletedTask;
    }

    // The push for one inbox entry: the source in the title, as the bell's filter names it.
    public static PushNotice Notice(InboxItem item) =>
        new($"{NotificationInboxItems.KindLabel(item.Kind)}: {item.Title}", item.Message, item.NavigateTo, $"{item.Kind}:{item.Id}");

    private void OnDocker(DockerHealthAlertView alert) => Send(async ct => await sender.AdminsWithDevicesAsync(ct), NotificationInboxItems.From(alert));

    private void OnSocial(SocialPostAlertView alert) => Send(async ct => await sender.AdminsWithDevicesAsync(ct), NotificationInboxItems.From(alert));

    private void OnChat(ChatMessageNotice notice) => Send(
        _ => Task.FromResult<IReadOnlyCollection<string>>(notice.RecipientUsernames
            .Where(username => !string.Equals(username, notice.SenderUsername, StringComparison.OrdinalIgnoreCase)).ToList()),
        NotificationInboxItems.From(notice),
        // Each message, not once per conversation: the tag still replaces the last one on the phone.
        once: false);

    private void OnOverwatch(string username, OverwatchAreaAlertView alert) => Send(username, NotificationInboxItems.From(alert));

    private void OnWorldNews(string username, GwsBusinessSuite.WorldNewsKit.WorldNewsAlert alert) => Send(username, NotificationInboxItems.From(alert));

    // A board told this person something: the newest unread note they haven't had pushed.
    private void OnBoards(string username) => _ = Task.Run(async () =>
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var notes = await scope.ServiceProvider.GetRequiredService<IBoardNotificationService>().ListAsync(username);
            var newest = notes.Where(note => !note.IsRead).OrderByDescending(note => note.CreatedAt.ToUnixTimeSeconds()).FirstOrDefault();
            if (newest is not null) await SendNowAsync([username], NotificationInboxItems.From(newest), once: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web Push: board notifications for {Username} weren't sent.", username);
        }
    });

    private void OnJobFinished(string username)
    {
        var newest = agentTurns.FinishedUnwatched(username).OrderByDescending(job => job.FinishedAt.ToUnixTimeSeconds()).FirstOrDefault();
        if (newest is not null) Send(username, NotificationInboxItems.From(newest));
    }

    private void Send(string username, InboxItem item) =>
        Send(_ => Task.FromResult<IReadOnlyCollection<string>>([username]), item);

    // Off the event's own thread: the source (a background job, a chat send) never waits for push services.
    private void Send(Func<CancellationToken, Task<IReadOnlyCollection<string>>> recipients, InboxItem item, bool once = true) => _ = Task.Run(async () =>
    {
        try
        {
            await SendNowAsync(await recipients(CancellationToken.None), item, once, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web Push: {Kind} notification {Id} wasn't sent.", item.Kind, item.Id);
        }
    });

    public async Task SendNowAsync(IReadOnlyCollection<string> recipients, InboxItem item, bool once, CancellationToken cancellationToken)
    {
        if (recipients.Count == 0) return;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (once)
        {
            if (!_sent.TryAdd($"{item.Kind}:{item.Id}", now)) return;
            if (_sent.Count > 2000)
                foreach (var old in _sent.Where(entry => entry.Value < now - (long)Remember.TotalSeconds).Select(entry => entry.Key).ToList())
                    _sent.TryRemove(old, out _);
        }
        await sender.SendAsync(recipients, Notice(item), cancellationToken);
    }
}
