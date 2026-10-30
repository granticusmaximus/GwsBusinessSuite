using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.TimeTracking;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Time tracking for the agent (Phase D, 30-10-26), through ITimeTrackingService like the Time page
// (AdminOnly). Always the asking person's own timer and entries; the rate is the one they used last.
public sealed class TimeAgentTools(ITimeTrackingService time, ICrmService crm, TimeProvider clock) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int EntriesShown = 25;

    public SentinelAgentArea Area { get; } = new("time", "time tracking: timers and logged hours", ["timer", "time track", "hours", "billable", "log time", "logged", "worked"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "my_time",
            "Your time tracking: the running timer, your entries for the last few days with totals, and the unbilled time per contact.",
            SentinelAgentJson.Schema(("days", "number", "How many days back (7 when left out)", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at your time",
            SummaryAsync),
        new SentinelAgentTool(
            "start_timer",
            "Start a timer for work for a CRM contact (stops any timer already running, keeping its time). Optionally for one of their deals or support tickets.",
            SentinelAgentJson.Schema(
                ("contactId", "string", "Who the work is for: the contact id", true),
                ("description", "string", "What the work is", true),
                ("dealId", "string", "Optional: the deal it's for", false),
                ("ticketId", "string", "Optional: the support ticket it's for", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Starting a timer for '{args.OptionalText("description")}'",
            StartAsync,
            UndoEntryAsync),
        new SentinelAgentTool(
            "stop_timer",
            "Stop your running timer and keep the time.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Change, Roles,
            _ => "Stopping your timer",
            StopAsync),
        new SentinelAgentTool(
            "log_time",
            "Record time already worked for a CRM contact: minutes or hours, what it was, and the day (today when left out).",
            SentinelAgentJson.Schema(
                ("contactId", "string", "Who the work was for: the contact id", true),
                ("description", "string", "What the work was", true),
                ("minutes", "number", "How long, in minutes", false),
                ("hours", "number", "How long, in hours (instead of minutes)", false),
                ("date", "string", "The day, as 2026-11-04", false),
                ("billable", "boolean", "Whether it's billed (true when left out)", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Logging time for '{args.OptionalText("description")}'",
            LogAsync,
            UndoEntryAsync)
    ];

    public static string LinkTo(Guid contactId) => $"/admin/time?contact={contactId}";

    private async Task<SentinelAgentToolResult> SummaryAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var days = (int)Math.Clamp(args.OptionalNumber("days") ?? 7, 1, 90);
        var now = clock.GetUtcNow();
        var entries = await time.ListAsync(now.AddDays(-days), now.AddMinutes(1), context.User.Username, ct);
        var running = await time.GetRunningAsync(context.User.Username, ct);
        var unbilled = await time.ListUnbilledAsync(ct);
        return SentinelAgentToolResult.Of(new
        {
            running = running is null ? null : new { running.Description, contact = running.ContactName, minutesSoFar = running.Minutes },
            days,
            totalHours = Math.Round(entries.Sum(entry => entry.Minutes) / 60m, 2),
            billableUsd = entries.Sum(entry => entry.AmountUsd),
            entries = entries.OrderByDescending(entry => entry.StartedAt).Take(EntriesShown).Select(entry => new
            {
                day = entry.StartedAt.ToString("yyyy-MM-dd"),
                entry.Description,
                contact = entry.ContactName,
                entry.Hours,
                billed = entry.InvoiceId is not null ? true : (bool?)null
            }),
            unbilled = unbilled.Select(summary => new { contact = summary.ContactName, hours = Math.Round(summary.Minutes / 60m, 2), summary.AmountUsd })
        });
    }

    private async Task<SentinelAgentToolResult> StartAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        var entry = await time.StartAsync(context.User.Username, await InputAsync(args, contact.Id, context, ct), ct);
        return SentinelAgentToolResult.Of(
            new { started = true, entry.Description, contact = entry.ContactName },
            new SentinelAgentChange($"Started a timer for '{entry.Description}' ({entry.ContactName})", "time-entry", entry.Id.ToString(), entry.Description, LinkTo(entry.ContactId),
                SentinelAgentJson.Serialize(new EntryUndo(entry.Id))));
    }

    private async Task<SentinelAgentToolResult> StopAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var entry = await time.StopAsync(context.User.Username, ct)
            ?? throw new SentinelAgentToolException("You have no timer running.");
        return SentinelAgentToolResult.Of(
            new { stopped = entry.Description, minutes = entry.Minutes, amountUsd = entry.AmountUsd },
            new SentinelAgentChange($"Stopped the timer for '{entry.Description}' ({Duration(entry.Minutes)})", "time-entry", entry.Id.ToString(), entry.Description, LinkTo(entry.ContactId), null));
    }

    private async Task<SentinelAgentToolResult> LogAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var contact = await ContactAsync(args.Id("contactId"), ct);
        var minutes = args.OptionalNumber("minutes") is { } m ? (int)Math.Round(m)
            : args.OptionalNumber("hours") is { } h ? (int)Math.Round(h * 60)
            : throw new SentinelAgentToolException("Say how long: 'minutes' or 'hours'.");
        var input = await InputAsync(args, contact.Id, context, ct);
        input.Minutes = minutes;
        input.Billable = args.Flag("billable", true);
        // A past day is kept at midday UTC so the entry stays on that date wherever it's viewed.
        input.StartedAt = args.OptionalDate("date") is { } day ? day.AddHours(12) : clock.GetUtcNow().AddMinutes(-minutes);
        var entry = await time.AddManualAsync(context.User.Username, input, ct);
        return SentinelAgentToolResult.Of(
            new { logged = true, entry.Description, contact = entry.ContactName, minutes = entry.Minutes, amountUsd = entry.AmountUsd },
            new SentinelAgentChange($"Logged {Duration(entry.Minutes)} for '{entry.Description}' ({entry.ContactName}, {entry.StartedAt:dd-MM-yy})", "time-entry", entry.Id.ToString(), entry.Description, LinkTo(entry.ContactId),
                SentinelAgentJson.Serialize(new EntryUndo(entry.Id))));
    }

    // Undoing a started timer removes it; a timer it stopped to start this one stays stopped.
    private Task UndoEntryAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        time.DeleteAsync(SentinelAgentJson.Deserialize<EntryUndo>(undoJson).EntryId, ct);

    private async Task<TimeEntryInput> InputAsync(JsonObject args, Guid contactId, SentinelAgentToolContext context, CancellationToken ct) => new()
    {
        ContactId = contactId,
        DealId = args.OptionalId("dealId"),
        TicketId = args.OptionalId("ticketId"),
        Description = args.Text("description"),
        HourlyRateUsd = await time.GetLastRateAsync(context.User.Username, ct)
    };

    private async Task<Contact> ContactAsync(Guid contactId, CancellationToken ct) =>
        await crm.GetContactAsync(contactId, ct)
            ?? throw new SentinelAgentToolException($"There's no contact with id {contactId}. Find the contact first.");

    private static string Duration(int minutes) =>
        minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";

    private sealed record EntryUndo(Guid EntryId);
}
