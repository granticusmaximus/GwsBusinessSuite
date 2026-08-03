using System.Net;
using System.Net.Mail;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class GrowthReportService(
    IAppDbContext db,
    IGrowthAnalyticsService analytics,
    IGrowthReportEmailSender sender,
    IOptions<GrowthReportEmailOptions> emailOptions,
    TimeProvider timeProvider,
    ILogger<GrowthReportService> logger) : IGrowthReportService
{
    private static readonly int[] AllowedRanges = [7, 30, 90];
    private readonly GrowthReportEmailOptions options = emailOptions.Value;

    public GrowthReportDeliveryConfiguration DeliveryConfiguration => sender.Configuration;

    public async Task<IReadOnlyList<AnalyticsReportScheduleView>> GetSchedulesAsync(
        CancellationToken cancellationToken = default)
    {
        var schedules = await db.AnalyticsReportSchedules.AsNoTracking()
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return schedules.Select(ToView).ToList();
    }

    public async Task<Guid> SaveScheduleAsync(
        AnalyticsReportScheduleInput input,
        CancellationToken cancellationToken = default)
    {
        var name = Clean(input.Name, 100);
        var recipient = Clean(input.RecipientEmail, 320);
        if (name.Length == 0) throw new ArgumentException("Schedule name is required.", nameof(input));
        if (!MailAddress.TryCreate(recipient, out _))
            throw new ArgumentException("Enter a valid recipient email address.", nameof(input));
        if (!AnalyticsReportFrequencies.All.Contains(input.Frequency, StringComparer.Ordinal))
            throw new ArgumentException("Report frequency must be weekly or monthly.", nameof(input));
        if (!AllowedRanges.Contains(input.RangeDays))
            throw new ArgumentException("Report range must be 7, 30, or 90 days.", nameof(input));
        if (input.DeliveryHourUtc is < 0 or > 23)
            throw new ArgumentException("Delivery hour must be between 0 and 23 UTC.", nameof(input));
        if (input.Frequency == AnalyticsReportFrequencies.Weekly && input.DeliveryDay is < 0 or > 6)
            throw new ArgumentException("Weekly delivery day is invalid.", nameof(input));
        if (input.Frequency == AnalyticsReportFrequencies.Monthly && input.DeliveryDay is < 1 or > 28)
            throw new ArgumentException("Monthly delivery day must be between 1 and 28.", nameof(input));

        var schedule = input.Id is { } id
            ? await db.AnalyticsReportSchedules.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("Report schedule no longer exists.")
            : new AnalyticsReportSchedule { Name = name, RecipientEmail = recipient };
        schedule.Name = name;
        schedule.RecipientEmail = recipient;
        schedule.Frequency = input.Frequency;
        schedule.RangeDays = input.RangeDays;
        schedule.DeliveryDay = input.DeliveryDay;
        schedule.DeliveryHourUtc = input.DeliveryHourUtc;
        schedule.IsActive = input.IsActive;
        schedule.NextRunAtUnixSeconds = input.IsActive
            ? NextOccurrence(input.Frequency, input.DeliveryDay, input.DeliveryHourUtc, timeProvider.GetUtcNow()).ToUnixTimeSeconds()
            : null;
        schedule.UpdatedAt = timeProvider.GetUtcNow();
        if (input.Id is null) await db.AnalyticsReportSchedules.AddAsync(schedule, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return schedule.Id;
    }

    public async Task DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await db.AnalyticsReportSchedules
            .FirstOrDefaultAsync(item => item.Id == scheduleId, cancellationToken);
        if (schedule is null) return;
        db.AnalyticsReportSchedules.Remove(schedule);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SendNowAsync(Guid scheduleId, CancellationToken cancellationToken = default)
    {
        if (!sender.Configuration.IsConfigured)
            throw new InvalidOperationException(sender.Configuration.Message);
        await DeliverAsync(scheduleId, advanceSchedule: false, cancellationToken);
    }

    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        if (!sender.Configuration.IsConfigured) return 0;
        var now = timeProvider.GetUtcNow();
        var nowUnix = now.ToUnixTimeSeconds();
        var ids = await db.AnalyticsReportSchedules.AsNoTracking()
            .Where(item => item.IsActive
                && item.NextRunAtUnixSeconds != null
                && item.NextRunAtUnixSeconds <= nowUnix)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var delivered = 0;
        foreach (var id in ids)
        {
            try
            {
                await DeliverAsync(id, advanceSchedule: true, cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Scheduled Growth Studio report {ScheduleId} failed.", id);
            }
        }
        return delivered;
    }

    private async Task DeliverAsync(
        Guid scheduleId,
        bool advanceSchedule,
        CancellationToken cancellationToken)
    {
        var schedule = await db.AnalyticsReportSchedules
            .FirstOrDefaultAsync(item => item.Id == scheduleId, cancellationToken)
            ?? throw new InvalidOperationException("Report schedule no longer exists.");
        var now = timeProvider.GetUtcNow();
        schedule.LastAttemptAt = now;
        try
        {
            var from = now.AddDays(-schedule.RangeDays);
            var dashboard = await analytics.GetDashboardAsync(from, now, breakdownLimit: 10, cancellationToken: cancellationToken);
            var email = BuildEmail(schedule, dashboard, from, now, options.DashboardUrl);
            await sender.SendAsync(email, cancellationToken);
            schedule.LastDeliveredAt = now;
            schedule.LastStatus = AnalyticsReportDeliveryStatuses.Delivered;
            schedule.LastError = string.Empty;
            if (advanceSchedule)
            {
                schedule.NextRunAtUnixSeconds = NextOccurrence(
                    schedule.Frequency,
                    schedule.DeliveryDay,
                    schedule.DeliveryHourUtc,
                    now).ToUnixTimeSeconds();
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            schedule.LastStatus = AnalyticsReportDeliveryStatuses.Failed;
            schedule.LastError = Clean(ex.Message, 500);
            if (advanceSchedule) schedule.NextRunAtUnixSeconds = now.AddMinutes(15).ToUnixTimeSeconds();
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    internal static DateTimeOffset NextOccurrence(
        string frequency,
        int deliveryDay,
        int deliveryHourUtc,
        DateTimeOffset after)
    {
        var utc = after.ToUniversalTime();
        if (frequency == AnalyticsReportFrequencies.Weekly)
        {
            var days = (deliveryDay - (int)utc.DayOfWeek + 7) % 7;
            var candidate = new DateTimeOffset(utc.Year, utc.Month, utc.Day, deliveryHourUtc, 0, 0, TimeSpan.Zero)
                .AddDays(days);
            return candidate <= utc ? candidate.AddDays(7) : candidate;
        }

        var monthly = new DateTimeOffset(utc.Year, utc.Month, deliveryDay, deliveryHourUtc, 0, 0, TimeSpan.Zero);
        if (monthly > utc) return monthly;
        var nextMonth = utc.AddMonths(1);
        return new DateTimeOffset(nextMonth.Year, nextMonth.Month, deliveryDay, deliveryHourUtc, 0, 0, TimeSpan.Zero);
    }

    private static GrowthReportEmail BuildEmail(
        AnalyticsReportSchedule schedule,
        GrowthAnalyticsDashboard dashboard,
        DateTimeOffset from,
        DateTimeOffset to,
        string dashboardUrl)
    {
        var period = $"{from:MMM d}–{to:MMM d, yyyy}";
        var subject = $"{schedule.Name}: Growth Studio report for {period}";
        var plain = new StringBuilder()
            .AppendLine(schedule.Name)
            .AppendLine($"Analytics period: {period}")
            .AppendLine()
            .AppendLine($"Unique visitors: {dashboard.Visitors:N0} ({ComparisonText(dashboard.VisitorsComparison)})")
            .AppendLine($"Page views: {dashboard.PageViews:N0} ({ComparisonText(dashboard.PageViewsComparison)})")
            .AppendLine($"Bounce rate: {dashboard.BounceRate:0.0}% ({ComparisonText(dashboard.BounceRateComparison)})")
            .AppendLine($"Average engagement: {FormatDuration(dashboard.AverageEngagement)} ({ComparisonText(dashboard.AverageEngagementComparison)})")
            .AppendLine()
            .AppendLine("Top pages:")
            .AppendJoin(Environment.NewLine, dashboard.TopPages.Take(5).Select(row => $"- {row.Label}: {row.Views:N0} views"))
            .AppendLine().AppendLine().AppendLine($"Open Growth Studio: {dashboardUrl}")
            .ToString();

        var html = $$"""
            <!doctype html><html><body style="margin:0;background:#100f0d;color:#f7f3ed;font:15px Arial,sans-serif">
            <div style="max-width:680px;margin:auto;padding:28px">
              <p style="color:#f59e0b;font-weight:700;margin:0 0 8px">GWS Growth Studio</p>
              <h1 style="font-size:26px;margin:0 0 6px">{{Html(schedule.Name)}}</h1>
              <p style="color:#aaa39a;margin:0 0 24px">Analytics period: {{Html(period)}}</p>
              <table role="presentation" style="width:100%;border-collapse:collapse"><tr>
                {{Metric("Unique visitors", dashboard.Visitors.ToString("N0"), dashboard.VisitorsComparison)}}
                {{Metric("Page views", dashboard.PageViews.ToString("N0"), dashboard.PageViewsComparison)}}
              </tr><tr>
                {{Metric("Bounce rate", $"{dashboard.BounceRate:0.0}%", dashboard.BounceRateComparison)}}
                {{Metric("Avg. engagement", FormatDuration(dashboard.AverageEngagement), dashboard.AverageEngagementComparison)}}
              </tr></table>
              {{Breakdown("Top pages", dashboard.TopPages.Take(5))}}
              {{Breakdown("Acquisition", dashboard.TopSources.Take(5))}}
              {{Annotations(dashboard.Annotations)}}
              <p style="margin-top:28px"><a href="{{Html(dashboardUrl)}}" style="display:inline-block;background:#f59e0b;color:#17130c;text-decoration:none;font-weight:700;padding:12px 18px;border-radius:8px">Open Growth Studio</a></p>
              <p style="color:#777;font-size:12px;margin-top:24px">This report contains first-party, cookieless analytics. Manage or pause its schedule in Growth Studio.</p>
            </div></body></html>
            """;
        return new(schedule.RecipientEmail, subject, plain, html);
    }

    private static string Metric(string label, string value, AnalyticsPeriodComparison comparison) =>
        $"<td style=\"width:50%;padding:12px;border:1px solid #332f29\"><span style=\"color:#aaa39a;font-size:12px\">{Html(label)}</span><br><strong style=\"font-size:24px\">{Html(value)}</strong><br><span style=\"color:#aaa39a;font-size:12px\">{Html(ComparisonText(comparison))}</span></td>";

    private static string Breakdown(string title, IEnumerable<AnalyticsBreakdownRow> rows)
    {
        var values = rows.ToList();
        if (values.Count == 0) return string.Empty;
        var items = string.Join(string.Empty, values.Select(row =>
            $"<tr><td style=\"padding:7px 0;border-bottom:1px solid #2b2823\">{Html(row.Label)}</td><td style=\"padding:7px 0;border-bottom:1px solid #2b2823;text-align:right\">{row.Views:N0} views</td></tr>"));
        return $"<h2 style=\"font-size:18px;margin:24px 0 8px\">{Html(title)}</h2><table role=\"presentation\" style=\"width:100%;border-collapse:collapse\">{items}</table>";
    }

    private static string Annotations(IReadOnlyList<AnalyticsAnnotationView> annotations)
    {
        if (annotations.Count == 0) return string.Empty;
        var items = string.Join(string.Empty, annotations.Select(item =>
            $"<li style=\"margin:6px 0\"><strong>{item.Date:MMM d}</strong> — {Html(item.Note)}</li>"));
        return $"<h2 style=\"font-size:18px;margin:24px 0 8px\">Period annotations</h2><ul style=\"padding-left:20px\">{items}</ul>";
    }

    private static string ComparisonText(AnalyticsPeriodComparison comparison) => comparison.ChangePercent switch
    {
        null => "new versus previous period",
        > 0 => $"up {comparison.ChangePercent.Value:0.#}% vs previous period",
        < 0 => $"down {Math.Abs(comparison.ChangePercent.Value):0.#}% vs previous period",
        _ => "unchanged vs previous period"
    };

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
        : $"{duration.Seconds}s";

    private static string Html(string value) => WebUtility.HtmlEncode(value);
    private static string Clean(string? value, int maxLength)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean.Length <= maxLength ? clean : clean[..maxLength];
    }

    private static AnalyticsReportScheduleView ToView(AnalyticsReportSchedule schedule) => new(
        schedule.Id,
        schedule.Name,
        schedule.RecipientEmail,
        schedule.Frequency,
        schedule.RangeDays,
        schedule.DeliveryDay,
        schedule.DeliveryHourUtc,
        schedule.IsActive,
        schedule.NextRunAtUnixSeconds is { } next ? DateTimeOffset.FromUnixTimeSeconds(next) : null,
        schedule.LastAttemptAt,
        schedule.LastDeliveredAt,
        schedule.LastStatus,
        schedule.LastError);
}
