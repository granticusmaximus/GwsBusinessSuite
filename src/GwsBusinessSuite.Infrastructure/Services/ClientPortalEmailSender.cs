using System.Net;
using GwsBusinessSuite.Application.ClientPortal;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as GrowthReportEmailOptions/GrowthReportEmailSender - a dedicated SMTP config per
// feature, matching this codebase's existing convention rather than forcing a shared mailer
// abstraction that doesn't otherwise exist here.
public sealed class ClientPortalEmailOptions
{
    public const string SectionName = "ClientPortalEmail";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "GWS Client Portal";
    public string PickupDirectory { get; set; } = string.Empty;
}

public sealed class ClientPortalEmailSender(
    IOptions<ClientPortalEmailOptions> configuredOptions,
    ILogger<ClientPortalEmailSender> logger) : IClientPortalEmailSender
{
    private readonly ClientPortalEmailOptions options = configuredOptions.Value;

    public async Task SendLoginLinkAsync(string toEmail, string contactName, string loginUrl, CancellationToken cancellationToken = default)
    {
        if (!MailboxAddress.TryParse(options.FromAddress, out var from))
        {
            // A misconfigured sender shouldn't take down the login request flow with an
            // unhandled exception (RequestLoginLinkAsync already looks the same to the caller
            // whether or not a contact matched) - log loudly instead, since a client silently
            // never receiving their sign-in email is otherwise very hard to notice.
            logger.LogError("Client portal login email not sent to {Email}: ClientPortalEmail:FromAddress is not configured.", toEmail);
            return;
        }
        if (!MailboxAddress.TryParse(toEmail, out var to))
        {
            logger.LogWarning("Client portal login email not sent: '{Email}' is not a valid address.", toEmail);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName.Trim(), from.Address));
        message.To.Add(to);
        message.Subject = "Your GWS client portal sign-in link";
        message.Body = new BodyBuilder
        {
            TextBody = $"Hi {contactName},\n\nUse the link below to sign in to your client portal. It expires in 15 minutes and can only be used once.\n\n{loginUrl}\n\nIf you didn't request this, you can ignore this email.",
            HtmlBody = $"<p>Hi {WebUtility.HtmlEncode(contactName)},</p><p>Use the link below to sign in to your client portal. It expires in 15 minutes and can only be used once.</p><p><a href=\"{WebUtility.HtmlEncode(loginUrl)}\">Sign in to the client portal</a></p><p>If you didn't request this, you can ignore this email.</p>"
        }.ToMessageBody();

        if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
        {
            Directory.CreateDirectory(options.PickupDirectory);
            var path = Path.Combine(options.PickupDirectory, $"client-portal-login-{Guid.NewGuid():N}.eml");
            await message.WriteToAsync(path, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            logger.LogError("Client portal login email not sent to {Email}: ClientPortalEmail:Host is not configured.", toEmail);
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
