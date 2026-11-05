using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Mail;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// The person's own mailbox for the agent (05-11-26, the role coverage pass): reading their mail and
// writing or sending replies, through IMailService like the Mail page (every role, one mailbox
// each). It only ever opens the signed-in person's own mailboxes, never anyone else's. A draft is
// routine, and Undo moves it to the trash. Sending waits for a Confirm.
public sealed class MailAgentTools(IMailService mail) : ISentinelAgentToolset
{
    private static readonly string[] Roles = AppRoles.All;
    private const int Shown = 20;
    private const int BodyCharacters = 3_000;

    public SentinelAgentArea Area { get; } = new("mail", "your own mailbox: reading mail, drafting and sending replies", ["mail", "inbox", "email", "reply"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "list_mail",
            "List messages in the person's own mailbox, newest first: the inbox unless a folder is given (inbox, sent, drafts, archive, spam, trash), optionally matching words. Gives ids, sender, subject, a snippet and whether it's unread.",
            SentinelAgentJson.Schema(
                ("folder", "string", "inbox (the default), sent, drafts, archive, spam or trash", false),
                ("search", "string", "Words to look for", false),
                ("mailbox", "string", "Which mailbox, by its address, when the person has more than one", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at your mail",
            ListAsync),
        new SentinelAgentTool(
            "read_mail",
            "Read one message from the person's own mailbox: who it's from and to, the date and the text.",
            SentinelAgentJson.Schema(
                ("messageId", "string", "The message id from list_mail", true),
                ("mailbox", "string", "Which mailbox, by its address, when the person has more than one", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading a message",
            ReadAsync),
        new SentinelAgentTool(
            "draft_mail",
            "Save an email in the person's Drafts without sending it, for them to look over. For a reply, give the message id it answers.",
            DraftSchema(),
            SentinelAgentRisk.Change, Roles,
            args => $"Drafting '{args.OptionalText("subject") ?? "an email"}'",
            DraftAsync,
            UndoDraftAsync),
        new SentinelAgentTool(
            "send_mail",
            "Send an email from the person's own mailbox. For a reply, give the message id it answers. Only proposes: the person confirms first.",
            DraftSchema(),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing an email",
            SendAsync,
            preview: async (args, context, ct) =>
            {
                var account = await AccountAsync(args, context, ct);
                return $"Send '{args.Text("subject")}' from {account.Address} to {string.Join(", ", Recipients(args))}: {Shorten(args.Text("body"), 200)}";
            })
    ];

    private sealed record DraftUndo(Guid AccountId, string DraftId);

    private static string DraftSchema() => SentinelAgentJson.Schema(
        ("to", "string[]", "Who it's to (email addresses)", true),
        ("subject", "string", "The subject", true),
        ("body", "string", "The message (Markdown is fine)", true),
        ("replyToMessageId", "string", "The id of the message this replies to", false),
        ("mailbox", "string", "Which mailbox to send from, by its address, when the person has more than one", false));

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var account = await AccountAsync(args, context, ct);
        var page = await mail.ListAsync(context.User.Username, account.Id, Folder(args.OptionalText("folder")), args.OptionalText("search"), null, ct);
        return SentinelAgentToolResult.Of(new
        {
            mailbox = account.Address,
            messages = page.Messages.Take(Shown).Select(message => new
            {
                id = message.Id,
                from = message.From.Display,
                subject = message.Subject,
                snippet = message.Snippet,
                date = message.Date.ToLocalTime().ToString("dd-MM-yy HH:mm"),
                unread = message.Unread
            })
        });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var account = await AccountAsync(args, context, ct);
        var message = await mail.GetAsync(context.User.Username, account.Id, args.Text("messageId"), showRemoteImages: false, ct);
        return SentinelAgentToolResult.Of(new
        {
            id = message.Id,
            from = message.From.Display,
            to = message.To.Select(address => address.Display),
            subject = message.Subject,
            date = message.Date.ToLocalTime().ToString("dd-MM-yy HH:mm"),
            text = Shorten(message.TextBody, BodyCharacters),
            attachments = message.Attachments.Select(attachment => attachment.FileName)
        });
    }

    private async Task<SentinelAgentToolResult> DraftAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var account = await AccountAsync(args, context, ct);
        var draftId = await mail.SaveDraftAsync(context.User.Username, account.Id, Draft(args), ct);
        var subject = args.Text("subject");
        return SentinelAgentToolResult.Of(new { drafted = subject, mailbox = account.Address },
            new SentinelAgentChange($"Drafted '{subject}' to {string.Join(", ", Recipients(args))}", "mail-draft", draftId, subject, "/admin/mail",
                SentinelAgentJson.Serialize(new DraftUndo(account.Id, draftId))));
    }

    private async Task UndoDraftAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<DraftUndo>(undoJson);
        await mail.TrashAsync(context.User.Username, undo.AccountId, undo.DraftId, ct);
    }

    private async Task<SentinelAgentToolResult> SendAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var account = await AccountAsync(args, context, ct);
        await mail.SendAsync(context.User.Username, account.Id, Draft(args), ct);
        var subject = args.Text("subject");
        return SentinelAgentToolResult.Of(new { sent = subject, from = account.Address },
            new SentinelAgentChange($"Sent '{subject}' to {string.Join(", ", Recipients(args))}", "mail", account.Id.ToString(), subject, "/admin/mail", null));
    }

    // The person's own mailbox: the one named, else their first.
    private async Task<MailAccountView> AccountAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var accounts = await mail.ListAccountsAsync(context.User.Username, ct);
        if (accounts.Count == 0) throw new SentinelAgentToolException("There's no mailbox connected yet: connect one on the Mail page first.");
        var named = args.OptionalText("mailbox");
        if (string.IsNullOrWhiteSpace(named)) return accounts[0];
        return accounts.FirstOrDefault(account => account.Address.Equals(named.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new SentinelAgentToolException($"There's no mailbox '{named}'. Yours: {string.Join(", ", accounts.Select(account => account.Address))}.");
    }

    private static MailDraft Draft(JsonObject args) =>
        new(Recipients(args), [], [], args.Text("subject"), args.Text("body"), args.OptionalText("replyToMessageId"));

    private static IReadOnlyList<string> Recipients(JsonObject args)
    {
        var to = args.Texts("to");
        return to.Count > 0 ? to : throw new SentinelAgentToolException("Say who it's to.");
    }

    private static string Folder(string? folder) => (folder?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "inbox" => MailFolders.Inbox,
        "sent" => MailFolders.Sent,
        "drafts" or "draft" => MailFolders.Drafts,
        "archive" => MailFolders.Archive,
        "spam" or "junk" => MailFolders.Spam,
        "trash" or "bin" => MailFolders.Trash,
        _ => throw new SentinelAgentToolException($"'{folder}' isn't a folder. Use inbox, sent, drafts, archive, spam or trash.")
    };

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
