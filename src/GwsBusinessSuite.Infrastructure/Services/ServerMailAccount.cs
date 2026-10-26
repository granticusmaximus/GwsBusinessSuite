using MailKit.Search;
using Microsoft.Extensions.Configuration;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record ServerMailbox(string Host, int Port, string Username, string Password, string Address, string AuthServId);

// The server's own mail account - the shared Smtp section in .env that already sends the app's
// mail - lent to the IMAP mail-in features (email-to-ticket, Sentinel email-in), so they work with
// nothing typed into their panels. Google only: a Gmail/Workspace login with an app password opens
// IMAP as well, and Google's receiving server name (for the DMARC check) is known. Each feature
// reads only mail sent to its own plus-address (grant+support@...), which Google delivers to the
// same inbox, so the rest of the inbox is never read or marked.
public sealed class ServerMailAccount(IConfiguration configuration)
{
    private static readonly string[] GoogleSmtpHosts = ["smtp.gmail.com", "smtp.googlemail.com"];

    public ServerMailbox? For(string tag) => Resolve(SharedSmtpOptions.ResolveSharedSection(configuration), tag);

    public static ServerMailbox? Resolve(IConfigurationSection smtp, string tag)
    {
        var host = smtp["Host"]?.Trim().ToLowerInvariant();
        var username = smtp["Username"]?.Trim() ?? string.Empty;
        var password = (smtp["Password"] ?? string.Empty).Replace(" ", string.Empty);
        var at = username.IndexOf('@');
        if (host is null || !GoogleSmtpHosts.Contains(host) || at <= 0 || password.Length == 0) return null;
        var local = username[..at];
        var plus = local.IndexOf('+');
        if (plus > 0) local = local[..plus];
        var address = $"{local}+{tag}@{username[(at + 1)..]}".ToLowerInvariant();
        return new ServerMailbox("imap.gmail.com", 993, username, password, address, "mx.google.com");
    }

    // Unread mail for the plus-address, found by Gmail's own search (deliveredto: also catches Bcc).
    public static SearchQuery UnreadFor(string address) => SearchQuery.NotSeen.And(SearchQuery.GMailRawSearch($"deliveredto:{address}"));

    // Checked again on the message itself before anything is done with it.
    public static bool IsAddressedTo(MimeMessage message, string address) =>
        message.To.Mailboxes.Concat(message.Cc.Mailboxes).Any(m => string.Equals(m.Address, address, StringComparison.OrdinalIgnoreCase))
        || message.Headers.Any(h => (string.Equals(h.Field, "Delivered-To", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(h.Field, "X-Original-To", StringComparison.OrdinalIgnoreCase))
                                    && h.Value.Contains(address, StringComparison.OrdinalIgnoreCase));
}
