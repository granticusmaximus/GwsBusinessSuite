using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Automation;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GwsBusinessSuite.Application.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public enum MailRouteKind
{
    None,
    PickupDirectory,
    Smtp,
    GmailApi
}

public sealed record MailRoute(MailRouteKind Kind, string Description)
{
    public bool CanSend => Kind != MailRouteKind.None;
}

// The one place every email feature actually hands a message off. A feature's own (already
// shared-fallback-applied) SMTP settings win; when there's no SMTP server at all, mail goes out
// through the Google account connected under Automation > Credentials (Gmail API, gmail.send
// scope) instead of being silently skipped.
public interface IMailTransport
{
    MailRoute Describe(ISmtpTransportOptions options);
    Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default);
    Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class MailTransport(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<MailTransport> logger) : IMailTransport
{
    public const string GoogleCredentialName = "Google";
    private const string GmailSendEndpoint = "https://gmail.googleapis.com/gmail/v1/users/me/messages/send";

    // Refreshed at startup, every few minutes, and after each Gmail send, so the synchronous
    // "is email configured?" checks the senders expose never block on a database read.
    private volatile bool _gmailConnected;

    // SMTP/pickup only - what a sender built without DI (tests, tools) gets.
    public static IMailTransport SmtpOnly { get; } = new SmtpOnlyMailTransport();

    public MailRoute Describe(ISmtpTransportOptions options)
    {
        var smtp = DescribeSmtp(options);
        if (smtp.CanSend) return smtp;
        return _gmailConnected
            ? new MailRoute(MailRouteKind.GmailApi, "the connected Google account (Gmail API)")
            : smtp;
    }

    public async Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
    {
        if (DescribeSmtp(options).CanSend)
        {
            await SendViaSmtpAsync(message, options, filePrefix, cancellationToken);
            return;
        }

        await SendViaGmailAsync(message, cancellationToken);
    }

    public async Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _gmailConnected = await FindGoogleCredentialIdAsync(cancellationToken) is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't check for a connected Google account for email delivery.");
        }
    }

    internal static MailRoute DescribeSmtp(ISmtpTransportOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
            return new MailRoute(MailRouteKind.PickupDirectory, $"the local pickup directory ({options.PickupDirectory})");
        if (!string.IsNullOrWhiteSpace(options.Host))
            return new MailRoute(MailRouteKind.Smtp, $"SMTP {options.Host.Trim()}:{options.Port}");
        return new MailRoute(MailRouteKind.None, "no SMTP server or connected Google account");
    }

    internal static async Task SendViaSmtpAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
        {
            Directory.CreateDirectory(options.PickupDirectory);
            await message.WriteToAsync(Path.Combine(options.PickupDirectory, $"{filePrefix}-{Guid.NewGuid():N}.eml"), cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
            throw new InvalidOperationException("No SMTP server is configured.");

        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host.Trim(), options.Port, ParseSecurity(options.Security), cancellationToken);
        if (!string.IsNullOrWhiteSpace(options.Username))
            await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public static SecureSocketOptions ParseSecurity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "auto" => SecureSocketOptions.Auto,
        "sslonconnect" => SecureSocketOptions.SslOnConnect,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.StartTls
    };

    private async Task SendViaGmailAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<IAutomationCredentialService>();
        var credentialId = await FindGoogleCredentialIdAsync(credentials, cancellationToken);
        _gmailConnected = credentialId is not null;
        if (credentialId is not { } id)
            throw new InvalidOperationException("No email delivery is configured: set Smtp__Host in .env, or connect a Google account under Automation > Credentials.");

        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, cancellationToken);
        var raw = Convert.ToBase64String(stream.ToArray()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var payload = JsonSerializer.Serialize(new { raw });

        var response = await PostToGmailAsync(credentials, id, payload, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && await credentials.RefreshOAuthCredentialAsync(id, cancellationToken))
        {
            response.Dispose();
            response = await PostToGmailAsync(credentials, id, payload, cancellationToken);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Gmail refused the message ({(int)response.StatusCode}): {Truncate(body, 300)}");
            }
        }
    }

    private async Task<HttpResponseMessage> PostToGmailAsync(IAutomationCredentialService credentials, Guid credentialId, string payload, CancellationToken cancellationToken)
    {
        var json = await credentials.GetDecryptedDataAsync(credentialId, cancellationToken);
        var accessToken = json is null ? null : JsonNode.Parse(json)?["accessToken"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("The connected Google account has no access token - reconnect it under Automation > Credentials.");

        using var request = new HttpRequestMessage(HttpMethod.Post, GmailSendEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await httpClientFactory.CreateClient(nameof(MailTransport)).SendAsync(request, cancellationToken);
    }

    private async Task<Guid?> FindGoogleCredentialIdAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        return await FindGoogleCredentialIdAsync(scope.ServiceProvider.GetRequiredService<IAutomationCredentialService>(), cancellationToken);
    }

    private static async Task<Guid?> FindGoogleCredentialIdAsync(IAutomationCredentialService credentials, CancellationToken cancellationToken)
    {
        var all = await credentials.ListAsync(cancellationToken);
        return all.FirstOrDefault(c =>
                string.Equals(c.TypeKey, AutomationCredentialService.OAuth2TypeKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Name, GoogleCredentialName, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed class SmtpOnlyMailTransport : IMailTransport
    {
        public MailRoute Describe(ISmtpTransportOptions options) => DescribeSmtp(options);

        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default) =>
            SendViaSmtpAsync(message, options, filePrefix, cancellationToken);

        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

// Logs which route each email feature will use at startup (no secrets), then keeps the
// "is a Google account connected?" flag fresh so connecting one takes effect without a restart.
public sealed class MailTransportStatusService(
    IMailTransport transport,
    IEmailDeliveryStatusService status,
    ILogger<MailTransportStatusService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var feature in await status.GetStatusAsync(stoppingToken))
            {
                if (feature.CanSend)
                    logger.LogInformation("Email delivery for {Feature}: via {Route}, from {From}.", feature.Feature, feature.Route, feature.FromAddress);
                else
                    logger.LogWarning("Email delivery for {Feature}: NOT SET UP ({Route}). Set Smtp__Host (and friends) in .env, or connect a Google account under Automation > Credentials.", feature.Feature, feature.Route);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't report email delivery status at startup.");
        }

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await transport.RefreshGmailStatusAsync(stoppingToken);
    }
}

public sealed class EmailDeliveryStatusService(
    IMailTransport transport,
    IOptions<GrowthReportEmailOptions> growth,
    IOptions<BookingEmailOptions> booking,
    IOptions<EmailCampaignEmailOptions> campaigns,
    IOptions<ClientPortalEmailOptions> portal) : IEmailDeliveryStatusService
{
    public async Task<IReadOnlyList<EmailDeliveryFeatureStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await transport.RefreshGmailStatusAsync(cancellationToken);
        return
        [
            Row("Form notifications, support alerts and Growth reports", growth.Value),
            Row("Email campaigns and new-article alerts", campaigns.Value),
            Row("Booking confirmations", booking.Value),
            Row("Client portal sign-in links", portal.Value)
        ];
    }

    public async Task<EmailDeliveryTestResult> SendTestAsync(string toAddress, CancellationToken cancellationToken = default)
    {
        if (!MailboxAddress.TryParse(toAddress, out var to))
            return new(false, $"'{toAddress}' isn't a valid email address.");
        var options = campaigns.Value;
        if (!MailboxAddress.TryParse(options.FromAddress, out var from))
            return new(false, "No valid From address is configured.");

        await transport.RefreshGmailStatusAsync(cancellationToken);
        var route = transport.Describe(options);
        if (!route.CanSend)
            return new(false, $"Nothing to send with: {route.Description}.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName.Trim(), from.Address));
        message.To.Add(to);
        message.Subject = "GWS Business Suite - test email";
        message.Body = new BodyBuilder
        {
            TextBody = $"This is a test from GWS Business Suite, sent via {route.Description}. If it arrived, email delivery works.",
            HtmlBody = $"<p>This is a test from GWS Business Suite, sent via <strong>{System.Net.WebUtility.HtmlEncode(route.Description)}</strong>.</p><p>If it arrived, email delivery works.</p>"
        }.ToMessageBody();

        try
        {
            await transport.SendAsync(message, options, "delivery-test", cancellationToken);
            return new(true, $"Sent to {to.Address} via {route.Description}. Check that inbox (and its spam folder).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, $"Sending via {route.Description} failed: {ex.Message}");
        }
    }

    private EmailDeliveryFeatureStatus Row(string feature, ISmtpTransportOptions options)
    {
        var route = transport.Describe(options);
        return new(feature, route.CanSend, route.Description, options.FromAddress);
    }
}
