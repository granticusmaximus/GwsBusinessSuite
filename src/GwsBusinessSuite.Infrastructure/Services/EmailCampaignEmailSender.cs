using System.Net;
using GwsBusinessSuite.Application.Campaigns;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as BookingEmailOptions/ClientPortalEmailOptions - a dedicated SMTP config per
// feature, matching this codebase's existing convention.
public sealed class EmailCampaignEmailOptions
{
    public const string SectionName = "EmailCampaignEmail";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "GWS Business Suite";
    public string PickupDirectory { get; set; } = string.Empty;
    // Absolute origin (scheme+host) prepended to the unsubscribe link path, since neither
    // EmailCampaignService nor its background sweep have an HttpContext of their own.
    public string PublicBaseUrl { get; set; } = string.Empty;
}

public sealed class EmailCampaignEmailSender(
    IOptions<EmailCampaignEmailOptions> configuredOptions,
    ILogger<EmailCampaignEmailSender> logger) : IEmailCampaignEmailSender
{
    private readonly EmailCampaignEmailOptions options = configuredOptions.Value;

    public async Task SendStepAsync(string toEmail, string subject, string body, string unsubscribeUrl, CancellationToken cancellationToken = default)
    {
        if (!MailboxAddress.TryParse(options.FromAddress, out var from))
        {
            logger.LogError("Campaign email not sent to {Email}: EmailCampaignEmail:FromAddress is not configured.", toEmail);
            return;
        }
        if (!MailboxAddress.TryParse(toEmail, out var to))
        {
            logger.LogWarning("Campaign email not sent: '{Email}' is not a valid address.", toEmail);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName.Trim(), from.Address));
        message.To.Add(to);
        message.Subject = subject;
        message.Body = new BodyBuilder
        {
            TextBody = $"{body}\n\n---\nUnsubscribe: {unsubscribeUrl}",
            HtmlBody = $"<div>{WebUtility.HtmlEncode(body).Replace("\n", "<br/>")}</div><p style=\"margin-top:2rem;font-size:.8rem;color:#888;\"><a href=\"{WebUtility.HtmlEncode(unsubscribeUrl)}\">Unsubscribe</a></p>"
        }.ToMessageBody();

        if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
        {
            Directory.CreateDirectory(options.PickupDirectory);
            var path = Path.Combine(options.PickupDirectory, $"campaign-{Guid.NewGuid():N}.eml");
            await message.WriteToAsync(path, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            logger.LogError("Campaign email not sent to {Email}: EmailCampaignEmail:Host is not configured.", toEmail);
            return;
        }

        TryGetSecurity(options.Security, out var security);
        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host.Trim(), options.Port, security, cancellationToken);
        if (!string.IsNullOrWhiteSpace(options.Username))
            await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    private static bool TryGetSecurity(string value, out SecureSocketOptions security)
    {
        security = value.Trim().ToLowerInvariant() switch
        {
            "auto" => SecureSocketOptions.Auto,
            "starttls" => SecureSocketOptions.StartTls,
            "sslonconnect" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => (SecureSocketOptions)(-1)
        };
        return (int)security >= 0;
    }
}
