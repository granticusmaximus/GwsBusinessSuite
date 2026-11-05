using System.Net.Sockets;

namespace GwsBusinessSuite.Infrastructure.Services;

// Why an SMTP connection never got going. PortBlocked: nothing answers host:port at all.
public sealed record SmtpConnectFailure(bool PortBlocked, string Message);

// Many hosts block outgoing mail ports on servers. DigitalOcean blocks 25, 465 and 587 on
// droplets until its support lifts the block (05-11-26: the production droplet reached
// imap.gmail.com:993 but neither smtp.gmail.com nor smtp.mail.me.com). The SMTP connect then
// hangs until MailKit gives up with "The operation has timed out", which reads like a bad
// password even though no password was ever sent. A bare TCP probe after the failure tells a
// blocked port apart from a server that answered and then went wrong (e.g. STARTTLS on 465).
public static class SmtpReachability
{
    public const string PasswordNeverTried = "Nothing was wrong with the password: it never got that far.";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // connectError is what SmtpClient.ConnectAsync threw, before any sign-in.
    public static async Task<SmtpConnectFailure> DescribeConnectFailureAsync(string host, int port, Exception connectError, CancellationToken cancellationToken)
    {
        if (connectError is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain })
            return new(false, $"Couldn't find the mail server {host} - check its name.");
        if (connectError is not (TimeoutException or SocketException or IOException))
            return new(false, connectError.Message);
        if (!await CanOpenAsync(host, port, cancellationToken))
            return new(true, $"This server can't reach {host}:{port} at all: its host is blocking outgoing mail ports "
                + "(DigitalOcean blocks 25, 465 and 587 on droplets until its support lifts the block).");
        return connectError is TimeoutException
            ? new(false, $"{host}:{port} answered but never started a secure connection - check the security setting matches the port (587 is STARTTLS, 465 is SSL).")
            : new(false, connectError.Message);
    }

    // Does anything accept a TCP connection at host:port?
    internal static async Task<bool> CanOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
