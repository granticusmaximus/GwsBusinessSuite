using System.Globalization;
using System.Text;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Assistant;

// The records pages describe to the SentinelGPT side panel, as plain text. Times are shown the
// way the pages show them (the server's local time, dd-MM-yy HH:mm) so answers match the screen.
public static class AssistantDescriptions
{
    public const int MaxMessages = 8;
    public const int MaxMessageLength = 800;
    public const int MaxHistoryEntries = 12;

    public static AssistantPageContext Ticket(SupportTicketView ticket, IReadOnlyList<ContactTimelineEntry> otherActivity)
    {
        var text = new StringBuilder();
        text.AppendLine($"Subject: {ticket.Subject}");
        text.AppendLine(Join($"Status: {ticket.Status}", $"Priority: {ticket.Priority}",
            string.IsNullOrWhiteSpace(ticket.AssignedToUsername) ? null : $"Assigned to: {ticket.AssignedToUsername}",
            string.IsNullOrWhiteSpace(ticket.TagsCsv) ? null : $"Tags: {ticket.TagsCsv}"));
        text.AppendLine($"Customer: {ticket.ContactName}");
        text.AppendLine($"Opened: {When(ticket.CreatedAt)}");

        var messages = ticket.Messages.OrderBy(message => message.CreatedAt).TakeLast(MaxMessages).ToList();
        text.AppendLine(messages.Count < ticket.Messages.Count
            ? $"Thread (the latest {messages.Count} of {ticket.Messages.Count} messages, oldest first):"
            : "Thread (oldest first):");
        foreach (var message in messages)
        {
            var role = message.AuthorType == SupportTicketAuthorTypes.Staff ? "staff" : message.AuthorType == SupportTicketAuthorTypes.Contact ? "customer" : message.AuthorType.ToLowerInvariant();
            text.AppendLine($"[{When(message.CreatedAt)}] {message.AuthorName} ({role}): {Shorten(message.Body, MaxMessageLength)}");
        }
        AppendActivity(text, "Other recent activity with this customer (newest first):", otherActivity);

        return new AssistantPageContext("Support", "Ticket", ticket.Subject, text.ToString().TrimEnd(),
            ["Draft a reply to the customer", "Summarize this thread", "What does the customer need?"], ticket.Id.ToString());
    }

    public static AssistantPageContext Contact(Contact contact, IReadOnlyList<ContactTimelineEntry> history)
    {
        var text = new StringBuilder();
        text.AppendLine($"Name: {contact.FullName}");
        if (!string.IsNullOrWhiteSpace(contact.Email)) text.AppendLine($"Email: {contact.Email}");
        if (!string.IsNullOrWhiteSpace(contact.Company)) text.AppendLine($"Company: {contact.Company}");
        text.AppendLine(Join($"Status: {contact.Status}", contact.FollowUpDate is { } followUp ? $"Follow up on: {followUp.ToLocalTime().ToString("dd-MM-yy", CultureInfo.InvariantCulture)}" : null));
        AppendActivity(text, "History (newest first):", history);

        return new AssistantPageContext("CRM", "Contact", contact.FullName, text.ToString().TrimEnd(),
            ["Summarize this customer", "Draft a follow-up email", "What should I do next with them?"], contact.Id.ToString());
    }

    private static void AppendActivity(StringBuilder text, string heading, IReadOnlyList<ContactTimelineEntry> entries)
    {
        if (entries.Count == 0) return;
        text.AppendLine(heading);
        foreach (var entry in entries.Take(MaxHistoryEntries))
        {
            text.AppendLine(string.IsNullOrWhiteSpace(entry.Detail)
                ? $"- {When(entry.At)} {entry.Title}"
                : $"- {When(entry.At)} {entry.Title} ({Shorten(entry.Detail, 200)})");
        }
        if (entries.Count > MaxHistoryEntries) text.AppendLine($"- …and {entries.Count - MaxHistoryEntries} older entries");
    }

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("dd-MM-yy HH:mm", CultureInfo.InvariantCulture);

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(part => part is not null));

    private static string Shorten(string text, int max)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }
}
