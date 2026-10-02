using System.Net;
using GwsBusinessSuite.Application.Campaigns;
using MailKit.Net.Smtp;
using MailKit.Security;
using Markdig;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as BookingEmailOptions/ClientPortalEmailOptions - a dedicated SMTP config per
// feature, matching this codebase's existing convention.
public sealed class EmailCampaignEmailOptions : ISmtpTransportOptions
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
    ILogger<EmailCampaignEmailSender> logger) : IEmailCampaignEmailSender, IArticleAlertEmailSender
{
    private static readonly MarkdownPipeline EmailMarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
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
        var textBody = Markdown.ToPlainText(body, EmailMarkdownPipeline).Trim();
        var htmlBody = Markdown.ToHtml(body, EmailMarkdownPipeline);
        message.Body = new BodyBuilder
        {
            TextBody = $"{textBody}\n\n---\nUnsubscribe: {unsubscribeUrl}",
            HtmlBody = $"<div>{htmlBody}</div><p style=\"margin-top:2rem;font-size:.8rem;color:#888;\"><a href=\"{WebUtility.HtmlEncode(unsubscribeUrl)}\">Unsubscribe</a></p>"
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

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(options.PickupDirectory) || !string.IsNullOrWhiteSpace(options.Host);

    // Article alerts / confirmations: each campaign sends as its own From name/address (e.g.
    // "Grant Watson Software" <grant@gwsapp.net>) over the same transport. The SMTP provider must be
    // allowed to send as that address (SPF/DKIM for its domain), or mail lands in spam.
    public async Task<bool> SendAsync(OutgoingCampaignEmail email, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Campaign email to {Email} not sent: no SMTP server is configured (set Smtp__Host).", email.ToAddress);
            return false;
        }

        var fromAddress = string.IsNullOrWhiteSpace(email.FromAddress) ? options.FromAddress : email.FromAddress;
        if (!MailboxAddress.TryParse(fromAddress, out var from))
            throw new InvalidOperationException("The campaign's From address is not a valid email address.");
        if (!MailboxAddress.TryParse(email.ToAddress, out var to))
            throw new ArgumentException($"'{email.ToAddress}' is not a valid email address.", nameof(email));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(email.FromName) ? options.FromName.Trim() : email.FromName.Trim(), from.Address));
        message.To.Add(to);
        if (!string.IsNullOrWhiteSpace(email.ReplyTo) && MailboxAddress.TryParse(email.ReplyTo, out var replyTo))
            message.ReplyTo.Add(replyTo);
        message.Subject = email.Subject;
        if (!string.IsNullOrWhiteSpace(email.OneClickUnsubscribeUrl))
        {
            // RFC 8058 one-click unsubscribe - required by Gmail/Yahoo for bulk senders since 2024.
            message.Headers.Add("List-Unsubscribe", $"<{email.OneClickUnsubscribeUrl}>");
            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();

        if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
        {
            Directory.CreateDirectory(options.PickupDirectory);
            await message.WriteToAsync(Path.Combine(options.PickupDirectory, $"campaign-{Guid.NewGuid():N}.eml"), cancellationToken);
            return true;
        }

        TryGetSecurity(options.Security, out var security);
        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host.Trim(), options.Port, security, cancellationToken);
        if (!string.IsNullOrWhiteSpace(options.Username))
            await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return true;
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
