using MailKit.Net.Imap;
using MailKit.Security;

namespace GwsBusinessSuite.Infrastructure.Services;

// Signs in to an IMAP mailbox: TLS on 993 (or STARTTLS on 143). Shared by Sentinel's email-in
// and Support's email-to-ticket.
public static class ImapMailboxConnector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<ImapClient> ConnectAsync(string host, int port, string username, string password, CancellationToken cancellationToken)
    {
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            var security = port == 143 ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect;
            await client.ConnectAsync(host, port, security, cancellationToken);
            await client.AuthenticateAsync(username, password, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
