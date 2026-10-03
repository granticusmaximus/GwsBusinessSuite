using FluentAssertions;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Tests;

public sealed class MailTransportTests
{
    private sealed class FakeGmailTransport : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];

        public MailRoute Describe(ISmtpTransportOptions options) =>
            new(MailRouteKind.GmailApi, "the connected Google account (Gmail API)");

        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task CampaignSender_WithNoSmtpServer_SendsThroughTheConnectedGoogleAccount()
    {
        var transport = new FakeGmailTransport();
        var sender = new EmailCampaignEmailSender(
            Options.Create(new EmailCampaignEmailOptions { FromAddress = "grant@gwsapp.net" }),
            NullLogger<EmailCampaignEmailSender>.Instance,
            transport);

        sender.IsConfigured.Should().BeTrue();
        var sent = await sender.SendAsync(new OutgoingCampaignEmail(
            "Grant Watson Software", "grant@gwsapp.net", "", "reader@example.com", "New post", "<p>Html</p>", "Text",
            "https://example.test/unsubscribe/abc"));

        sent.Should().BeTrue();
        var message = transport.Sent.Should().ContainSingle().Subject;
        message.From.Mailboxes.Single().Address.Should().Be("grant@gwsapp.net");
        message.Headers["List-Unsubscribe"].Should().Contain("unsubscribe/abc");
    }

    [Fact]
    public void SmtpOnlyTransport_ReportsNothingConfigured_WithoutAServer()
    {
        var route = MailTransport.SmtpOnly.Describe(new BookingEmailOptions());

        route.CanSend.Should().BeFalse();
        route.Kind.Should().Be(MailRouteKind.None);
    }
}
