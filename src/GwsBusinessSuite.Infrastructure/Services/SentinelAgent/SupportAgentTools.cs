using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Support;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Support tickets for the agent (Phase D, 30-10-26), through ISupportTicketService like the
// Support page (AdminOnly). Status, priority and assignee go through the list-edit triage, whose
// snapshot puts a ticket back exactly on Undo, SLA clock included. A reply emails the contact,
// so it waits for a Confirm.
public sealed class SupportAgentTools(ISupportTicketService tickets) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int TicketsShown = 25;
    private const int MessagesShown = 10;
    private const int MessageCharacters = 1_500;

    public SentinelAgentArea Area { get; } = new("support", "support tickets: triage, tags and replies", ["ticket", "support", "sla"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "find_tickets",
            "Find support tickets, optionally by status (Open, Pending, Resolved, Closed) and words in the subject or contact's name. Gives ids, priority, who it's assigned to and when replies are due.",
            SentinelAgentJson.Schema(
                ("status", "string", "Only tickets with this status", false),
                ("search", "string", "Words in the subject or the contact's name", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at the support tickets",
            FindAsync),
        new SentinelAgentTool(
            "read_ticket",
            "Read one support ticket: its details, tags and latest messages.",
            SentinelAgentJson.Schema(("ticketId", "string", "The ticket id", true)),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading a ticket",
            ReadAsync),
        new SentinelAgentTool(
            "update_ticket",
            "Change a ticket's status (Open, Pending, Resolved, Closed), priority (Low, Normal, High, Urgent) or who it's assigned to (a username, or none). Give only what changes.",
            SentinelAgentJson.Schema(
                ("ticketId", "string", "The ticket id", true),
                ("status", "string", "The new status", false),
                ("priority", "string", "The new priority", false),
                ("assignTo", "string", "The username to assign it to, or none", false)),
            SentinelAgentRisk.Change, Roles,
            _ => "Updating a ticket",
            UpdateAsync,
            UndoUpdateAsync),
        new SentinelAgentTool(
            "tag_ticket",
            "Set a ticket's tags (replaces them). Read the ticket first to keep the ones it has.",
            SentinelAgentJson.Schema(
                ("ticketId", "string", "The ticket id", true),
                ("tags", "string[]", "The tags", true)),
            SentinelAgentRisk.Change, Roles,
            _ => "Tagging a ticket",
            TagAsync,
            UndoTagAsync),
        new SentinelAgentTool(
            "reply_to_ticket",
            "Reply to a ticket as staff. The contact is emailed the reply, so this only proposes: the person confirms first.",
            SentinelAgentJson.Schema(
                ("ticketId", "string", "The ticket id", true),
                ("message", "string", "The reply, written to the contact", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing a reply",
            ReplyAsync,
            preview: async (args, _, ct) =>
            {
                var ticket = await TicketAsync(args.Id("ticketId"), ct);
                return $"Reply to '{ticket.Subject}' (emails {ticket.ContactName}): {Shorten(args.Text("message"), 300)}";
            })
    ];

    public static string LinkTo(Guid ticketId) => $"/admin/support?ticket={ticketId}";

    private async Task<SentinelAgentToolResult> FindAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var status = args.OptionalText("status") is { } named ? Pick(named, SupportTicketStatuses.All, "status") : null;
        var words = (args.OptionalText("search") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var list = (await tickets.ListTicketsAsync(status, ct))
            .Where(ticket => words.All(word =>
                ticket.Subject.Contains(word, StringComparison.OrdinalIgnoreCase)
                || ticket.ContactName.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return SentinelAgentToolResult.Of(new
        {
            found = list.Count,
            tickets = list.Take(TicketsShown).Select(ticket => new
            {
                id = ticket.Id,
                subject = ticket.Subject,
                contact = ticket.ContactName,
                status = ticket.Status,
                priority = ticket.Priority,
                assignedTo = ticket.AssignedToUsername,
                lastReply = Moment(ticket.LastRepliedAt),
                firstReplyDue = ticket.Messages.Any(message => message.AuthorType == SupportTicketAuthorTypes.Staff) ? null : Moment(ticket.FirstResponseDueAt)
            })
        });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var ticket = await TicketAsync(args.Id("ticketId"), ct);
        return SentinelAgentToolResult.Of(new
        {
            id = ticket.Id,
            subject = ticket.Subject,
            contact = ticket.ContactName,
            contactId = ticket.ContactId,
            status = ticket.Status,
            priority = ticket.Priority,
            assignedTo = ticket.AssignedToUsername,
            tags = Tags(ticket.TagsCsv),
            opened = Moment(ticket.CreatedAt),
            resolutionDue = Moment(ticket.ResolutionDueAt),
            messages = ticket.Messages.OrderBy(message => message.CreatedAt).TakeLast(MessagesShown).Select(message => new
            {
                from = $"{message.AuthorName} ({message.AuthorType})",
                at = Moment(message.CreatedAt),
                text = Shorten(message.Body, MessageCharacters),
                attachments = message.Attachments.Count > 0 ? message.Attachments.Select(file => file.FileName) : null
            })
        });
    }

    private async Task<SentinelAgentToolResult> UpdateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var ticket = await TicketAsync(args.Id("ticketId"), ct);
        var changes = new List<SupportTicketTriageChange>();
        if (args.OptionalText("status") is { } status) changes.Add(SupportTicketTriageChange.ToStatus(Pick(status, SupportTicketStatuses.All, "status")));
        if (args.OptionalText("priority") is { } priority) changes.Add(SupportTicketTriageChange.ToPriority(Pick(priority, SupportTicketPriorities.All, "priority")));
        if (args.OptionalText("assignTo") is { } assignee)
            changes.Add(SupportTicketTriageChange.AssignTo(assignee.ToLowerInvariant() is "none" or "nobody" or "unassigned" ? null : assignee));
        if (changes.Count == 0) throw new SentinelAgentToolException("Give a status, a priority or someone to assign it to.");

        // The first snapshot is the ticket as it was before any of these changes.
        SupportTicketTriageSnapshot? before = null;
        foreach (var change in changes)
        {
            var snapshot = (await tickets.ApplyTriageAsync([ticket.Id], change, context.User.Username, ct)).FirstOrDefault();
            before ??= snapshot;
        }
        if (before is null) throw new SentinelAgentToolException($"'{ticket.Subject}' is already like that.");

        var after = await TicketAsync(ticket.Id, ct);
        return SentinelAgentToolResult.Of(
            new { updated = after.Subject, status = after.Status, priority = after.Priority, assignedTo = after.AssignedToUsername },
            new SentinelAgentChange($"Updated the ticket '{after.Subject}' ({Triage(after)})", "support-ticket", after.Id.ToString(), after.Subject, LinkTo(after.Id),
                SentinelAgentJson.Serialize(new TriageUndo(before, Triage(after)))));
    }

    private async Task UndoUpdateAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<TriageUndo>(undoJson);
        var ticket = await TicketAsync(undo.Before.TicketId, ct);
        if (Triage(ticket) != undo.After)
            throw new SentinelAgentToolException($"'{ticket.Subject}' has changed since, so it's left as it is.");
        await tickets.RestoreTriageAsync([undo.Before], context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> TagAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var ticket = await TicketAsync(args.Id("ticketId"), ct);
        var tagsCsv = string.Join(", ", args.Texts("tags").Distinct(StringComparer.OrdinalIgnoreCase));
        var saved = await tickets.SetTagsAsync(ticket.Id, tagsCsv, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { tagged = saved.Subject, tags = Tags(saved.TagsCsv) },
            new SentinelAgentChange($"Tagged the ticket '{saved.Subject}': {(saved.TagsCsv.Length == 0 ? "no tags" : saved.TagsCsv)}", "support-ticket", saved.Id.ToString(), saved.Subject, LinkTo(saved.Id),
                SentinelAgentJson.Serialize(new TagsUndo(saved.Id, ticket.TagsCsv, saved.TagsCsv))));
    }

    private async Task UndoTagAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<TagsUndo>(undoJson);
        var ticket = await TicketAsync(undo.TicketId, ct);
        if (ticket.TagsCsv != undo.After)
            throw new SentinelAgentToolException($"'{ticket.Subject}' has been tagged differently since, so its tags are left as they are.");
        await tickets.SetTagsAsync(ticket.Id, undo.Before, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> ReplyAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var ticket = await TicketAsync(args.Id("ticketId"), ct);
        await tickets.AddReplyAsync(ticket.Id, SupportTicketAuthorTypes.Staff, context.User.Username, args.Text("message"), cancellationToken: ct);
        return SentinelAgentToolResult.Of(new { replied = ticket.Subject, emailed = ticket.ContactName },
            new SentinelAgentChange($"Replied to '{ticket.Subject}' (emailed {ticket.ContactName})", "support-reply", ticket.Id.ToString(), ticket.Subject, LinkTo(ticket.Id), null));
    }

    private async Task<SupportTicketView> TicketAsync(Guid ticketId, CancellationToken ct) =>
        await tickets.GetTicketAsync(ticketId, ct)
            ?? throw new SentinelAgentToolException($"There's no support ticket with id {ticketId}. Find the tickets first.");

    private static string Pick(string name, IEnumerable<string> allowed, string what)
    {
        var options = allowed.ToList();
        return options.FirstOrDefault(option => string.Equals(option, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new SentinelAgentToolException($"'{name}' isn't a ticket {what}. They are: {string.Join(", ", options)}.");
    }

    private static string Triage(SupportTicketView ticket) =>
        $"{ticket.Status}, {ticket.Priority}, {(ticket.AssignedToUsername is { } who ? $"assigned to {who}" : "unassigned")}";

    private static IReadOnlyList<string> Tags(string tagsCsv) =>
        tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Moment(DateTimeOffset? at) => at?.ToString("yyyy-MM-dd HH:mm");

    private static string Shorten(string text, int characters) =>
        text.Length <= characters ? text : text[..characters].TrimEnd() + "...";

    private sealed record TriageUndo(SupportTicketTriageSnapshot Before, string After);

    private sealed record TagsUndo(Guid TicketId, string Before, string After);
}
