using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Email for the agent (Phase D, 30-10-26). It leaves the app, so it always waits for a Confirm and
// can't be undone. Mail goes out the way every other feature's does (IMailTransport with the
// shared SMTP settings, or the connected Google account). An email to a CRM contact is noted on
// their activity log. Admin only.
public sealed class EmailAgentTools(ICrmService crm, IOptions<GrowthReportEmailOptions> mailOptions, IMailTransport? mailTransport = null) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int NotePreviewCharacters = 200;

    public SentinelAgentArea Area { get; } = new("email", "sending an email", ["email", "e-mail", "mail", "send"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "send_email",
            "Send an email to a CRM contact (by id) or to an email address. Plain text. It leaves the app, so this only proposes: the person confirms first.",
            SentinelAgentJson.Schema(
                ("contactId", "string", "The CRM contact to email (or use 'to')", false),
                ("to", "string", "An email address, when it isn't a CRM contact", false),
                ("subject", "string", "The subject line", true),
                ("body", "string", "The email, as plain text", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing an email",
            SendAsync,
            preview: async (args, _, ct) =>
            {
                var (name, address, _) = await RecipientAsync(args, ct);
                args.Text("body");
                return $"Email {name} <{address}>: '{args.Text("subject")}'";
            })
    ];

    private async Task<SentinelAgentToolResult> SendAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var (name, address, contactId) = await RecipientAsync(args, ct);
        if (mailTransport is null || !mailTransport.Describe(mailOptions.Value).CanSend)
            throw new SentinelAgentToolException("Email isn't set up on this server (Settings > Email), so nothing was sent.");

        var subject = args.Text("subject");
        var body = args.Text("body");
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("GWS Business Suite", mailOptions.Value.FromAddress));
        message.To.Add(new MailboxAddress(name, address));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        await mailTransport.SendAsync(message, mailOptions.Value, "sentinelgpt-email", ct);

        if (contactId is { } id)
            await crm.AddActivityAsync(id, $"Emailed '{subject}' (sent by SentinelGPT for {context.User.Username}): {(body.Length <= NotePreviewCharacters ? body : body[..NotePreviewCharacters] + "...")}", ct);
        return SentinelAgentToolResult.Of(new { sent = true, to = address, subject },
            new SentinelAgentChange($"Emailed {name}: '{subject}'", "email", contactId?.ToString() ?? address, subject,
                contactId is { } contact ? CrmAgentTools.LinkTo(contact) : null, null));
    }

    private async Task<(string Name, string Address, Guid? ContactId)> RecipientAsync(JsonObject args, CancellationToken ct)
    {
        if (args.OptionalId("contactId") is { } contactId)
        {
            var contact = await crm.GetContactAsync(contactId, ct)
                ?? throw new SentinelAgentToolException($"There's no contact with id {contactId}. Find the contact first.");
            if (string.IsNullOrWhiteSpace(contact.Email) || !MailboxAddress.TryParse(contact.Email, out _))
                throw new SentinelAgentToolException($"'{contact.FullName}' has no email address in the CRM.");
            return (contact.FullName, contact.Email, contact.Id);
        }

        var to = args.OptionalText("to") ?? throw new SentinelAgentToolException("Say who it's to: a contactId or a 'to' address.");
        return MailboxAddress.TryParse(to, out var mailbox) && mailbox.Address.Contains('@')
            ? (mailbox.Name is { Length: > 0 } named ? named : mailbox.Address, mailbox.Address, null)
            : throw new SentinelAgentToolException($"'{to}' isn't an email address.");
    }
}
