using System.Net;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.BusinessIntelligence;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Weekly BI Dashboards report: one email per subscribed admin summarising their own pinned
// charts. Sent by BiGoalBackgroundService's hourly tick (SendDueReportsAsync) or "Send now".
public sealed class BiReportEmailService(
    IAppDbContext db,
    IBusinessIntelligenceService bi,
    IMailTransport mailTransport,
    IOptions<GrowthReportEmailOptions> smtpOptions,
    TimeProvider timeProvider) : IBiReportEmailService
{
    public async Task<BiReportSubscriptionView> GetSubscriptionAsync(string ownerUsername, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var row = await db.BiReportSubscriptions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.OwnerUsername == owner, cancellationToken)
            ?? new BiReportSubscription { OwnerUsername = owner };
        return new BiReportSubscriptionView(row.Enabled, row.Recipient, row.DayOfWeek, row.HourLocal, row.LastSentAt,
            mailTransport.Describe(smtpOptions.Value).CanSend);
    }

    public async Task SaveSubscriptionAsync(string ownerUsername, BiReportSubscriptionView subscription, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var recipient = string.IsNullOrWhiteSpace(subscription.Recipient) ? null : subscription.Recipient.Trim();
        if (subscription.Enabled && !NewsWatchService.IsEmailAddress(recipient))
        {
            throw new InvalidOperationException("Enter a valid email address for the weekly report.");
        }

        var row = await db.BiReportSubscriptions.FirstOrDefaultAsync(item => item.OwnerUsername == owner, cancellationToken);
        if (row is null)
        {
            row = new BiReportSubscription { OwnerUsername = owner, CreatedAt = timeProvider.GetUtcNow(), CreatedBy = owner };
            db.BiReportSubscriptions.Add(row);
        }
        else
        {
            row.UpdatedAt = timeProvider.GetUtcNow();
            row.UpdatedBy = owner;
        }
        row.Enabled = subscription.Enabled;
        row.Recipient = recipient;
        row.DayOfWeek = Math.Clamp(subscription.DayOfWeek, 0, 6);
        row.HourLocal = Math.Clamp(subscription.HourLocal, 0, 23);
        await db.SaveChangesAsync(cancellationToken);
    }

    // Due once per week: on the chosen weekday at/after the chosen hour, if not already sent in
    // the last 6 days (so a late tick still sends, but never twice in one week).
    public static bool IsDue(BiReportSubscription subscription, DateTimeOffset nowLocal) =>
        subscription.Enabled
        && (int)nowLocal.DayOfWeek == subscription.DayOfWeek
        && nowLocal.Hour >= subscription.HourLocal
        && (subscription.LastSentAt is not { } last || nowLocal - last > TimeSpan.FromDays(6));

    public async Task<int> SendDueReportsAsync(CancellationToken cancellationToken = default)
    {
        var nowLocal = timeProvider.GetLocalNow();
        var due = (await db.BiReportSubscriptions.AsNoTracking().Where(item => item.Enabled).ToListAsync(cancellationToken))
            .Where(item => IsDue(item, nowLocal))
            .Select(item => item.OwnerUsername)
            .ToList();
        var sent = 0;
        foreach (var owner in due)
        {
            if ((await SendAsync(owner, force: false, cancellationToken)).Sent) sent++;
        }
        return sent;
    }

    public async Task<BiReportSendResult> SendAsync(string ownerUsername, bool force, CancellationToken cancellationToken = default)
    {
        var owner = NormalizeOwner(ownerUsername);
        var row = await db.BiReportSubscriptions.FirstOrDefaultAsync(item => item.OwnerUsername == owner, cancellationToken);
        if (row is null || !MailboxAddress.TryParse(row.Recipient, out var to))
        {
            return new(false, "Save a report email address first.");
        }
        if (!force && !IsDue(row, timeProvider.GetLocalNow()))
        {
            return new(false, "Not due yet.");
        }
        var smtp = smtpOptions.Value;
        if (!mailTransport.Describe(smtp).CanSend || !MailboxAddress.TryParse(smtp.FromAddress, out var from))
        {
            return new(false, "Email delivery isn't set up on this server (Settings > Email).");
        }

        var widgets = await bi.GetDashboardAsync(owner, refresh: true, cancellationToken: cancellationToken);
        if (widgets.Count == 0)
        {
            return new(false, "Pin at least one chart to your dashboard first.");
        }

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = $"[BI Dashboards] Weekly report - {widgets.Count} chart{(widgets.Count == 1 ? "" : "s")}";
        message.Body = new BodyBuilder { TextBody = ReportText(widgets), HtmlBody = ReportHtml(widgets) }.ToMessageBody();
        await mailTransport.SendAsync(message, smtp, "bi-weekly-report", cancellationToken);
        row.LastSentAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return new(true, $"Report sent to {to}.");
    }

    public static string FormatValue(decimal value, string format) => format switch
    {
        "Currency" => value.ToString("C2"),
        "Rating" => value.ToString("0.0#") + " / 5",
        _ => value.ToString("N0")
    };

    public static string ChangeText(BiChartResult chart)
    {
        if (chart.PreviousTotal == 0) return chart.Total == 0 ? "no change" : "new activity";
        var percent = (chart.Total - chart.PreviousTotal) / chart.PreviousTotal * 100m;
        return percent == 0 ? "no change" : $"{(percent > 0 ? "up" : "down")} {Math.Abs(percent):0.#}%";
    }

    private static string GoalText(BiDashboardWidget widget) => widget.GoalValue is not { } goal
        ? string.Empty
        : $"{(widget.GoalMet == true ? "goal met" : widget.GoalIsCeiling ? "over the limit" : "below target")} ({(widget.GoalIsCeiling ? "limit" : "target")} {FormatValue(goal, widget.Chart.ValueFormat)})";

    public static string ReportText(IReadOnlyList<BiDashboardWidget> widgets)
    {
        var text = new StringBuilder();
        foreach (var widget in widgets)
        {
            text.AppendLine($"{widget.Title} - {widget.Chart.MetricLabel}, last {widget.RangeDays} days");
            text.Append($"  {FormatValue(widget.Chart.Total, widget.Chart.ValueFormat)} ({ChangeText(widget.Chart)} vs previous period)");
            var goal = GoalText(widget);
            text.AppendLine(goal.Length > 0 ? $"; {goal}" : string.Empty);
        }
        text.AppendLine();
        text.AppendLine("Open BI Dashboards for the charts and drill-down.");
        return text.ToString();
    }

    public static string ReportHtml(IReadOnlyList<BiDashboardWidget> widgets)
    {
        var html = new StringBuilder("<table cellpadding=\"6\" style=\"border-collapse:collapse;font-family:sans-serif;font-size:14px\">");
        html.Append("<tr><th align=\"left\">Chart</th><th align=\"right\">Total</th><th align=\"left\">Change</th><th align=\"left\">Goal</th></tr>");
        foreach (var widget in widgets)
        {
            html.Append("<tr style=\"border-top:1px solid #ddd\"><td><strong>").Append(WebUtility.HtmlEncode(widget.Title)).Append("</strong><br><small>")
                .Append(WebUtility.HtmlEncode($"{widget.Chart.MetricLabel}, last {widget.RangeDays} days")).Append("</small></td>")
                .Append("<td align=\"right\">").Append(WebUtility.HtmlEncode(FormatValue(widget.Chart.Total, widget.Chart.ValueFormat))).Append("</td>")
                .Append("<td>").Append(WebUtility.HtmlEncode(ChangeText(widget.Chart))).Append("</td>")
                .Append("<td>").Append(WebUtility.HtmlEncode(GoalText(widget))).Append("</td></tr>");
        }
        html.Append("</table><p style=\"font-family:sans-serif;font-size:13px\">Open BI Dashboards for the charts and drill-down.</p>");
        return html.ToString();
    }

    private static string NormalizeOwner(string ownerUsername)
    {
        var owner = ownerUsername.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(owner)
            ? throw new InvalidOperationException("An authenticated user is required.")
            : owner;
    }
}
