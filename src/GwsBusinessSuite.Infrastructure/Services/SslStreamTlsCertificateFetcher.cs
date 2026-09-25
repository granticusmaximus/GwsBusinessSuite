using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using GwsBusinessSuite.Application.ThreatIntel;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SslStreamTlsCertificateFetcher : ITlsCertificateFetcher
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);

    public async Task<X509Certificate2?> FetchAsync(string hostName, CancellationToken cancellationToken = default)
    {
        using var tcpClient = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ConnectTimeout);
        await tcpClient.ConnectAsync(hostName, 443, timeoutCts.Token);

        // Accept any certificate on purpose - see DomainIntelService's own comment: this
        // inspects whatever certificate a site presents (an expired/mismatched one is itself a
        // real finding), not trusting it for any real data exchange beyond the handshake itself.
        using var sslStream = new SslStream(tcpClient.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await sslStream.AuthenticateAsClientAsync(hostName);

        return sslStream.RemoteCertificate is null ? null : new X509Certificate2(sslStream.RemoteCertificate);
    }
}
