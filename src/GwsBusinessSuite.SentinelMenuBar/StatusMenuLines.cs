namespace GwsBusinessSuite.SentinelMenuBar;

// One status line in the menu. Path (under /admin) is where clicking it goes; null = info only.
internal sealed record StatusMenuLine(string Text, string? Path);

// Turns a status summary into menu lines - plain logic, kept apart from AppKit so it stays simple.
internal static class StatusMenuLines
{
    public static IReadOnlyList<StatusMenuLine> Build(StatusSummary? summary, StatusProblem problem, DateTimeOffset now)
    {
        if (summary is null)
        {
            return problem switch
            {
                StatusProblem.NoKey => [new("Set an API key to see status", null)],
                StatusProblem.KeyRejected => [new("API key was rejected - set a new one", null)],
                StatusProblem.Unreachable => [new("Can't reach the server", null)],
                _ => [new("Checking...", null)]
            };
        }

        var lines = new List<StatusMenuLine>();
        if (problem == StatusProblem.Unreachable) lines.Add(new("Can't reach the server - showing last status", null));
        lines.Add(summary.Health switch
        {
            "critical" => new($"● Server problem: {Count(summary.UnreadHealthAlerts, "alert")}", "/docker-health"),
            "warning" => new($"▲ Server warning: {Count(summary.UnreadHealthAlerts, "alert")}", "/docker-health"),
            _ => new("✓ All systems OK", "/docker-health")
        });
        if (summary.OpenTickets > 0)
        {
            lines.Add(new($"Support: {summary.OpenTickets} open{(summary.OverdueTickets > 0 ? $", {summary.OverdueTickets} overdue" : "")}", "/support"));
        }
        if (summary.DueFollowUps > 0) lines.Add(new(Count(summary.DueFollowUps, "follow-up") + " due", "/crm"));
        if (summary.UnreadMessages > 0) lines.Add(new(Count(summary.UnreadMessages, "unread message"), "/community/messages"));
        if (summary.UnreadAreaAlerts > 0) lines.Add(new(Count(summary.UnreadAreaAlerts, "Overwatch area alert"), "/osint"));
        if (summary.UnreadFormSubmissions > 0) lines.Add(new(Count(summary.UnreadFormSubmissions, "new form submission"), "/form-submissions"));
        if (summary.PendingComments > 0) lines.Add(new(Count(summary.PendingComments, "comment") + " to moderate", "/comments"));
        if (summary.NextBooking is { } booking)
        {
            lines.Add(new($"Next: {booking.Title} with {booking.AttendeeName}, {When(booking.StartsAt.ToLocalTime(), now.ToLocalTime())}", "/scheduling"));
        }
        if (summary.RunningTimer is { } timer)
        {
            var elapsed = now - timer.StartedAt;
            lines.Add(new($"Timer: {timer.ContactName} · {(int)elapsed.TotalHours}:{elapsed.Minutes:00}", "/time"));
        }
        return lines;
    }

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string When(DateTimeOffset at, DateTimeOffset now) =>
        at.Date == now.Date ? $"today {at:h:mm tt}"
        : at.Date == now.Date.AddDays(1) ? $"tomorrow {at:h:mm tt}"
        : $"{at:ddd MMM d, h:mm tt}";
}
