using GwsBusinessSuite.Application.Operations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class OperationalAlertOptions
{
    public const string SectionName = "OperationalAlerts";

    // Left blank by default - alerting is opt-in until an admin sets a recipient, same as
    // every other optional external integration in this app (CongressApi:ApiKey, backup
    // offsite settings, etc.) rather than failing startup or nagging in logs for a feature
    // nobody asked to enable yet.
    public string NotifyEmail { get; set; } = string.Empty;

    public int CooldownMinutes { get; set; } = 60;
}

// Reuses GrowthReportEmail's SMTP transport settings (the one SMTP account already
// configured in this app) rather than asking for a second full set of host/port/credentials
// just for alerting - only the recipient address is new configuration.
public sealed class OperationalAlertService(
    IOptions<OperationalAlertOptions> alertOptions,
    IOptions<GrowthReportEmailOptions> smtpOptions,
    IMemoryCache cache,
    ILogger<OperationalAlertService> logger) : IOperationalAlertService
{
    private readonly OperationalAlertOptions _alertOptions = alertOptions.Value;
    private readonly GrowthReportEmailOptions _smtp = smtpOptions.Value;

    public async Task NotifyFailureAsync(
        string source, string summary, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_alertOptions.NotifyEmail) || !MailboxAddress.TryParse(_alertOptions.NotifyEmail, out var recipient))
            return;
        if (string.IsNullOrWhiteSpace(_smtp.Host) && string.IsNullOrWhiteSpace(_smtp.PickupDirectory))
            return;
        if (!MailboxAddress.TryParse(_smtp.FromAddress, out var from))
            return;

        var cooldownKey = $"operational-alert-cooldown:{source}";
        if (cache.TryGetValue(cooldownKey, out _)) return;
        cache.Set(cooldownKey, true, TimeSpan.FromMinutes(Math.Max(1, _alertOptions.CooldownMinutes)));

        try
        {
            var message = new MimeMessage();
            message.From.Add(from);
            message.To.Add(recipient);
            message.Subject = $"[GWS Suite] {source} failed";
            var body = $"{summary}\n\nSource: {source}\nWhen (UTC): {DateTimeOffset.UtcNow:O}";
            if (exception is not null) body += $"\n\n{exception}";
            message.Body = new TextPart("plain") { Text = body };

            if (!string.IsNullOrWhiteSpace(_smtp.PickupDirectory))
            {
                Directory.CreateDirectory(_smtp.PickupDirectory);
                var path = Path.Combine(_smtp.PickupDirectory, $"operational-alert-{Guid.NewGuid():N}.eml");
                await message.WriteToAsync(path, cancellationToken);
                return;
            }

            var security = _smtp.Security.Trim().ToLowerInvariant() switch
            {
                "auto" => SecureSocketOptions.Auto,
                "sslonconnect" => SecureSocketOptions.SslOnConnect,
                "none" => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls
            };
            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host.Trim(), _smtp.Port, security, cancellationToken);
            if (!string.IsNullOrWhiteSpace(_smtp.Username))
                await client.AuthenticateAsync(_smtp.Username, _smtp.Password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex)
        {
            // Alerting itself must never throw into the caller's failure path - that would
            // turn "send me an email when X breaks" into a second, silent way for X's own
            // error handling to break.
            logger.LogError(ex, "Failed to send operational alert email for {Source}.", source);
        }
    }
}
