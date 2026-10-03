using GwsBusinessSuite.Application.Growth;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class GrowthReportEmailOptions : ISmtpTransportOptions
{
    public const string SectionName = "GrowthReportEmail";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "GWS Growth Studio";
    public string PickupDirectory { get; set; } = string.Empty;
    public string DashboardUrl { get; set; } = "https://admin.gwsapp.net/admin/growth";
}

public sealed class GrowthReportEmailSender(
    IOptions<GrowthReportEmailOptions> configuredOptions,
    IMailTransport? mailTransport = null) : IGrowthReportEmailSender
{
    private readonly GrowthReportEmailOptions options = configuredOptions.Value;
    private readonly IMailTransport mail = mailTransport ?? MailTransport.SmtpOnly;

    public GrowthReportDeliveryConfiguration Configuration
    {
        get
        {
            if (!MailboxAddress.TryParse(options.FromAddress, out _))
                return new(false, "Set GrowthReportEmail:FromAddress before enabling report delivery.");
            var route = mail.Describe(options);
            if (route.Kind == MailRouteKind.PickupDirectory)
                return new(true, "Report delivery is configured for the local pickup directory.");
            if (route.Kind == MailRouteKind.GmailApi)
                return new(true, $"Email is delivered through {route.Description}.");
            if (!route.CanSend)
                return new(false, "Set Smtp__Host (and its sender settings) in .env, or connect a Google account under Automation > Credentials, to enable delivery.");
            if (options.Port is < 1 or > 65535)
                return new(false, "GrowthReportEmail SMTP port must be between 1 and 65535.");
            if (!TryGetSecurity(options.Security, out _))
                return new(false, "GrowthReportEmail security must be Auto, StartTls, SslOnConnect, or None.");
            return new(true, $"SMTP delivery is configured for {options.Host}:{options.Port}.");
        }
    }

    public async Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default)
    {
        if (!Configuration.IsConfigured) throw new InvalidOperationException(Configuration.Message);
        if (!MailboxAddress.TryParse(email.RecipientEmail, out var recipient))
            throw new ArgumentException("Report recipient email is invalid.", nameof(email));

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName.Trim(), options.FromAddress.Trim()));
        message.To.Add(recipient);
        message.Subject = email.Subject;
        message.Body = new BodyBuilder
        {
            TextBody = email.PlainTextBody,
            HtmlBody = email.HtmlBody
        }.ToMessageBody();

        await mail.SendAsync(message, options, "growth-report", cancellationToken);
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
