using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Automation;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// OAuthCredentialId: signed in with the app's Google connection (no password; see ConnectAsync).
public sealed record ServerMailbox(string Host, int Port, string Username, string Password, string Address, string AuthServId, Guid? OAuthCredentialId = null);

// The app-wide Google connection (Automation > Credentials) when it may read mail over IMAP: kept
// fresh by MailTransport.RefreshGmailStatusAsync so ServerMailAccount.For stays synchronous.
public sealed class GoogleMailConnection
{
    private volatile Snapshot? _current;

    public sealed record Snapshot(Guid CredentialId, string Address);

    public Snapshot? Current => _current;

    public void Set(Snapshot? snapshot) => _current = snapshot;
}

// The server's own mail account - the app's mail account in Settings > Email when it's on,
// otherwise the shared Smtp section in .env that already sends the app's mail - lent to the IMAP
// mail-in features (email-to-ticket, Sentinel email-in), so they work with
// nothing typed into their panels. Google only: a Gmail/Workspace login with an app password opens
// IMAP as well, and Google's receiving server name (for the DMARC check) is known. Each feature
// reads only mail sent to its own plus-address (grant+support@...), which Google delivers to the
// same inbox, so the rest of the inbox is never read or marked.
//
// With no Google app-password login anywhere, the app's Google connection reads the same plus-
// addresses over IMAP with its sign-in token (31-10-26), once Google has granted full Gmail access.
public sealed class ServerMailAccount(
    IConfiguration configuration,
    AppMailAccount? appAccount = null,
    GoogleMailConnection? googleConnection = null,
    IServiceScopeFactory? scopeFactory = null)
{
    private static readonly string[] GoogleSmtpHosts = ["smtp.gmail.com", "smtp.googlemail.com"];

    public ServerMailbox? For(string tag)
    {
        var withPassword = appAccount?.Current is { } account
            ? Resolve(account.Host, account.Username, account.Password, tag)
            : Resolve(SharedSmtpOptions.ResolveSharedSection(configuration), tag);
        if (withPassword is not null || googleConnection?.Current is not { } google) return withPassword;
        return new ServerMailbox("imap.gmail.com", 993, google.Address, string.Empty, PlusAddress(google.Address, tag), "mx.google.com", google.CredentialId);
    }

    // Signs in to the mailbox For returned: its app password, or the Google connection's token
    // (refreshed first when it's about to expire, and once more if Gmail turns it down).
    public async Task<ImapClient> ConnectAsync(ServerMailbox mailbox, CancellationToken cancellationToken)
    {
        if (mailbox.OAuthCredentialId is not { } credentialId || scopeFactory is null)
            return await ImapMailboxConnector.ConnectAsync(mailbox.Host, mailbox.Port, mailbox.Username, mailbox.Password, cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<IAutomationCredentialService>();
        try
        {
            return await ImapMailboxConnector.ConnectWithTokenAsync(mailbox.Host, mailbox.Port, mailbox.Username,
                await AccessTokenAsync(credentials, credentialId, cancellationToken), cancellationToken);
        }
        catch (AuthenticationException)
        {
            if (!await credentials.RefreshOAuthCredentialAsync(credentialId, cancellationToken)) throw;
        }
        return await ImapMailboxConnector.ConnectWithTokenAsync(mailbox.Host, mailbox.Port, mailbox.Username,
            await AccessTokenAsync(credentials, credentialId, cancellationToken), cancellationToken);
    }

    private static async Task<string> AccessTokenAsync(IAutomationCredentialService credentials, Guid credentialId, CancellationToken cancellationToken)
    {
        var data = JsonNode.Parse(await credentials.GetDecryptedDataAsync(credentialId, cancellationToken) ?? "{}");
        if (DateTimeOffset.TryParse(data?["expiresAt"]?.GetValue<string>(), out var expiresAt) && expiresAt < DateTimeOffset.UtcNow.AddMinutes(2)
            && await credentials.RefreshOAuthCredentialAsync(credentialId, cancellationToken))
            data = JsonNode.Parse(await credentials.GetDecryptedDataAsync(credentialId, cancellationToken) ?? "{}");
        return data?["accessToken"]?.GetValue<string>() ?? throw new InvalidOperationException("The Google connection has no access token - connect Google again.");
    }

    private static string PlusAddress(string address, string tag)
    {
        var at = address.IndexOf('@');
        var local = address[..at];
        var plus = local.IndexOf('+');
        if (plus > 0) local = local[..plus];
        return $"{local}+{tag}@{address[(at + 1)..]}".ToLowerInvariant();
    }

    public static ServerMailbox? Resolve(IConfigurationSection smtp, string tag) =>
        Resolve(smtp["Host"], smtp["Username"], smtp["Password"], tag);

    public static ServerMailbox? Resolve(string? smtpHost, string? smtpUsername, string? smtpPassword, string tag)
    {
        var host = smtpHost?.Trim().ToLowerInvariant();
        var username = smtpUsername?.Trim() ?? string.Empty;
        var password = (smtpPassword ?? string.Empty).Replace(" ", string.Empty);
        var at = username.IndexOf('@');
        if (host is null || !GoogleSmtpHosts.Contains(host) || at <= 0 || password.Length == 0) return null;
        return new ServerMailbox("imap.gmail.com", 993, username, password, PlusAddress(username, tag), "mx.google.com");
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
