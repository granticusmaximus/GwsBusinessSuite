using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace GwsBusinessSuite.Tests;

// 05-11-26: the production droplet can't reach any SMTP port (DigitalOcean blocks 25, 465 and
// 587), so connecting Gmail or iCloud said "couldn't sign in to smtp.gmail.com ... The operation
// has timed out" - which read as a wrong app password that was never actually sent.
public sealed class SmtpReachabilityTests
{
    [Fact]
    public async Task ARealMailKitConnect_ToAPortNothingAnswers_ShouldBeCalledABlockedPort()
    {
        var port = ClosedPort();
        using var smtp = new SmtpClient { Timeout = 5_000 };

        var error = await Record.ExceptionAsync(() => smtp.ConnectAsync("127.0.0.1", port, SecureSocketOptions.StartTls));
        var failure = await SmtpReachability.DescribeConnectFailureAsync("127.0.0.1", port, error!, CancellationToken.None);

        failure.PortBlocked.Should().BeTrue();
        failure.Message.Should().Contain($"can't reach 127.0.0.1:{port}").And.Contain("blocking outgoing mail ports");
    }

    [Fact]
    public async Task ATimeout_FromAPortThatDoesAnswer_ShouldPointAtTheSecuritySetting_NotTheHost()
    {
        // Something listening that never says hello: what STARTTLS against an SSL-only port looks like.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var failure = await SmtpReachability.DescribeConnectFailureAsync("127.0.0.1", port, new TimeoutException("The operation has timed out."), CancellationToken.None);

        failure.PortBlocked.Should().BeFalse();
        failure.Message.Should().Contain("587 is STARTTLS, 465 is SSL");
    }

    [Fact]
    public async Task AServerNameThatDoesntExist_ShouldSaySo_WithoutBlamingTheHost()
    {
        var failure = await SmtpReachability.DescribeConnectFailureAsync("smtp.example.invalid", 587, new SocketException((int)SocketError.HostNotFound), CancellationToken.None);

        failure.Should().Be(new SmtpConnectFailure(false, "Couldn't find the mail server smtp.example.invalid - check its name."));
    }

    // A loopback port nothing listens on: connecting is refused at once.
    internal static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
