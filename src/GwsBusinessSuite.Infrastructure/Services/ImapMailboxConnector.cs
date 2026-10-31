using MailKit.Net.Imap;
using MailKit.Security;

namespace GwsBusinessSuite.Infrastructure.Services;

// Signs in to an IMAP mailbox: TLS on 993 (or STARTTLS on 143). Shared by Sentinel's email-in
// and Support's email-to-ticket.
public static class ImapMailboxConnector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static Task<ImapClient> ConnectAsync(string host, int port, string username, string password, CancellationToken cancellationToken) =>
        ConnectAsync(host, port, (client, ct) => client.AuthenticateAsync(username, password, ct), cancellationToken);

    // Gmail with a Google sign-in token instead of a password (SASL XOAUTH2).
    public static Task<ImapClient> ConnectWithTokenAsync(string host, int port, string username, string accessToken, CancellationToken cancellationToken) =>
        ConnectAsync(host, port, (client, ct) => client.AuthenticateAsync(new SaslMechanismOAuth2(username, accessToken), ct), cancellationToken);

    private static async Task<ImapClient> ConnectAsync(string host, int port, Func<ImapClient, CancellationToken, Task> authenticate, CancellationToken cancellationToken)
    {
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            var security = port == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
            await client.ConnectAsync(host, port, security, cancellationToken);
            await authenticate(client, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
