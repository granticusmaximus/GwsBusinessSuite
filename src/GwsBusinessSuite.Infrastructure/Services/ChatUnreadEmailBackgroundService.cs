using System.Net;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Emails a member about chat messages they still haven't read 15 minutes after they arrived - the
// "nothing pings you if the tab isn't open" gap. One email per thread per batch of new messages
// (ChatThreadParticipant.UnreadEmailSentAt), only to members who put an email on their Community
// profile and left "email me about unread messages" on. Sent over the shared notification route.
public sealed class ChatUnreadEmailBackgroundService(
    IServiceScopeFactory scopeFactory,
    IMailTransport mailTransport,
    IOptions<GrowthReportEmailOptions> mailOptions,
    IOptions<SupportNotificationOptions> supportOptions,
    TimeProvider timeProvider,
    ILogger<ChatUnreadEmailBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Unread chat email sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        if (!mailTransport.Describe(mailOptions.Value).CanSend) return 0;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = timeProvider.GetUtcNow();
        var since = now - Lookback;

        // Small internal-staff tables: materialize, then filter/sort client-side (SQLite can't
        // compare DateTimeOffset in SQL).
        var recent = (await db.ChatMessages.AsNoTracking().ToListAsync(cancellationToken))
            .Where(m => m.CreatedAt >= since)
            .ToList();
        if (recent.Count == 0) return 0;
        var threadIds = recent.Select(m => m.ThreadId).Distinct().ToList();
        var participants = await db.ChatThreadParticipants.Where(p => threadIds.Contains(p.ThreadId)).ToListAsync(cancellationToken);
        var threads = await db.ChatThreads.AsNoTracking().Where(t => threadIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        var users = await db.AppUsers.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.Username }).ToListAsync(cancellationToken);
        var profiles = await db.MemberProfiles.AsNoTracking().ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var participant in participants)
        {
            var user = users.FirstOrDefault(u => string.Equals(u.Username, participant.Username, StringComparison.OrdinalIgnoreCase));
            var profile = user is null ? null : profiles.FirstOrDefault(p => p.AppUserId == user.Id);
            if (profile is null || !profile.NotifyUnreadMessagesByEmail || !MailboxAddress.TryParse(profile.Email, out var to)) continue;

            var cutoff = new[] { participant.LastReadAt, participant.UnreadEmailSentAt }.Max() ?? DateTimeOffset.MinValue;
            var unread = recent
                .Where(m => m.ThreadId == participant.ThreadId && m.CreatedAt > cutoff
                    && !string.Equals(m.CreatedBy, participant.Username, StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => m.CreatedAt)
                .ToList();
            if (unread.Count == 0 || now - unread[0].CreatedAt < Grace) continue;

            var thread = threads.GetValueOrDefault(participant.ThreadId);
            var where = thread?.IsGroup == true ? $"in {thread.Title ?? "a group chat"}" : $"from {unread[^1].CreatedBy}";
            var link = $"{supportOptions.Value.AdminBaseUrl.TrimEnd('/')}/admin/community/messages/{participant.ThreadId}";
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GWS Business Suite", mailOptions.Value.FromAddress));
            message.To.Add(to);
            message.Subject = $"{unread.Count} unread message{(unread.Count == 1 ? "" : "s")} {where}";
            var last = unread[^1].Body.Length > 200 ? unread[^1].Body[..200] + "…" : unread[^1].Body;
            message.Body = new BodyBuilder
            {
                TextBody = $"You have {unread.Count} unread message{(unread.Count == 1 ? "" : "s")} {where}.\n\nLatest: {last}\n\nOpen the conversation: {link}\n\nTurn these emails off on your Community profile.",
                HtmlBody = $"<p>You have <strong>{unread.Count}</strong> unread message{(unread.Count == 1 ? "" : "s")} {WebUtility.HtmlEncode(where)}.</p><blockquote style=\"color:#555\">{WebUtility.HtmlEncode(last)}</blockquote><p><a href=\"{WebUtility.HtmlEncode(link)}\">Open the conversation</a></p><p style=\"color:#888;font-size:12px\">Turn these emails off on your Community profile.</p>"
            }.ToMessageBody();

            try
            {
                await mailTransport.SendAsync(message, mailOptions.Value, "chat-unread", cancellationToken);
                participant.UnreadEmailSentAt = now;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't email {Username} about unread chat messages.", participant.Username);
            }
        }

        if (sent > 0) await db.SaveChangesAsync(cancellationToken);
        return sent;
    }
}
