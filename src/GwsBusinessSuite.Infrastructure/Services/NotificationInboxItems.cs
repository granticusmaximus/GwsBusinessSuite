using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Application.DockerHealth;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.WorldNewsKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Each notification source as an inbox entry, shared by the bell (NotificationBell) and Web Push
// (WebPushDispatcher), so a push says what the bell says and opens the same page (07-11-26).
public static class NotificationInboxItems
{
    public static InboxItem From(DockerHealthAlertView alert) => new(
        "docker", alert.Id, alert.ContainerName, alert.Message, alert.IsRead, alert.CreatedAt,
        $"/admin/docker-health/{Uri.EscapeDataString(alert.ContainerName)}");

    public static InboxItem From(SocialPostAlertView alert) => new(
        "social", alert.Id, alert.PostTitle, alert.Message, alert.IsRead, alert.CreatedAt, "/admin/growth");

    public static InboxItem From(OverwatchAreaAlertView alert) => new(
        "overwatch", alert.Id, alert.Title, alert.Message, alert.IsRead, alert.CreatedAt, alert.LinkUrl ?? $"/admin/osint?area={alert.AreaId}");

    public static InboxItem From(WorldNewsAlert alert) => new(
        "world-news", alert.Id, alert.Title, alert.Message, alert.IsRead, alert.CreatedAt,
        $"/admin/world-news?situation={Uri.EscapeDataString(alert.SituationId)}");

    public static InboxItem From(BoardNotificationView note) => new(
        "board", note.Id, note.Title, note.Message, note.IsRead, note.CreatedAt, note.Url);

    // A SentinelGPT job that finished after its tab closed (05-11-26, background jobs).
    public static InboxItem From(SentinelAgentTurns.FinishedJob job) => new(
        "sentinelgpt", job.TurnId, "SentinelGPT finished: " + (job.Request.Length <= 60 ? job.Request : job.Request[..60] + "…"),
        job.Outcome, false, job.FinishedAt, "/admin/sentinelgpt/activity");

    // A new chat message: in a group it says who sent it.
    public static InboxItem From(ChatMessageNotice notice) => new(
        "chat", notice.ThreadId, notice.ThreadTitle, notice.IsGroup ? $"{notice.SenderDisplayName}: {notice.Preview}" : notice.Preview,
        false, notice.SentAt, $"/admin/community/messages/{notice.ThreadId}");

    public static string KindLabel(string kind) => kind switch
    {
        "docker" => "Containers",
        "social" => "Social posts",
        "chat" => "Messages",
        "overwatch" => "Overwatch",
        "board" => "Boards",
        "world-news" => "World News",
        "sentinelgpt" => "SentinelGPT",
        _ => kind
    };
}
