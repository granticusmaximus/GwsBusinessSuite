using System.Net;
using GwsBusinessSuite.Application.Scheduling;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// Same shape as ClientPortalEmailOptions/ClientPortalEmailSender - a dedicated SMTP config
// per feature, matching this codebase's existing convention.
public sealed class BookingEmailOptions
{
    public const string SectionName = "BookingEmail";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Security { get; set; } = "StartTls";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "GWS Scheduling";
    public string PickupDirectory { get; set; } = string.Empty;
    // Absolute origin (scheme+host) prepended to the relative "manage/cancel" link path
    // BookingService hands the email sender, since the sender has no HttpContext of its own.
    public string PublicBaseUrl { get; set; } = string.Empty;
}

public sealed class BookingEmailSender(
    IOptions<BookingEmailOptions> configuredOptions,
    ILogger<BookingEmailSender> logger) : IBookingEmailSender
{
    private readonly BookingEmailOptions options = configuredOptions.Value;

    public async Task SendConfirmationAsync(
        string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc, string manageUrl,
        CancellationToken cancellationToken = default)
    {
        var fullManageUrl = $"{options.PublicBaseUrl.TrimEnd('/')}{manageUrl}";
        var when = startsAtUtc.ToString("dddd, MMMM d 'at' h:mm tt 'UTC'");
        await SendAsync(
            attendeeEmail,
            $"Confirmed: {bookingTypeTitle}",
            $"Hi {attendeeName},\n\nYour booking for \"{bookingTypeTitle}\" is confirmed for {when}.\n\nNeed to cancel? Use this link:\n{fullManageUrl}",
            $"<p>Hi {WebUtility.HtmlEncode(attendeeName)},</p><p>Your booking for \"{WebUtility.HtmlEncode(bookingTypeTitle)}\" is confirmed for <strong>{WebUtility.HtmlEncode(when)}</strong>.</p><p>Need to cancel? <a href=\"{WebUtility.HtmlEncode(fullManageUrl)}\">Manage this booking</a>.</p>",
            "booking-confirmation",
            cancellationToken);
    }

    public async Task SendCancellationAsync(
        string attendeeEmail, string attendeeName, string bookingTypeTitle, DateTimeOffset startsAtUtc,
        CancellationToken cancellationToken = default)
    {
        var when = startsAtUtc.ToString("dddd, MMMM d 'at' h:mm tt 'UTC'");
        await SendAsync(
            attendeeEmail,
            $"Cancelled: {bookingTypeTitle}",
            $"Hi {attendeeName},\n\nYour booking for \"{bookingTypeTitle}\" on {when} has been cancelled.",
            $"<p>Hi {WebUtility.HtmlEncode(attendeeName)},</p><p>Your booking for \"{WebUtility.HtmlEncode(bookingTypeTitle)}\" on <strong>{WebUtility.HtmlEncode(when)}</strong> has been cancelled.</p>",
            "booking-cancellation",
            cancellationToken);
    }

    private async Task SendAsync(string toEmail, string subject, string textBody, string htmlBody, string filePrefix, CancellationToken cancellationToken)
    {
        if (!MailboxAddress.TryParse(options.FromAddress, out var from))
        {
            logger.LogError("Booking email not sent to {Email}: BookingEmail:FromAddress is not configured.", toEmail);
            return;
        }
        if (!MailboxAddress.TryParse(toEmail, out var to))
        {
            logger.LogWarning("Booking email not sent: '{Email}' is not a valid address.", toEmail);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(options.FromName.Trim(), from.Address));
        message.To.Add(to);
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody }.ToMessageBody();

        if (!string.IsNullOrWhiteSpace(options.PickupDirectory))
        {
            Directory.CreateDirectory(options.PickupDirectory);
            var path = Path.Combine(options.PickupDirectory, $"{filePrefix}-{Guid.NewGuid():N}.eml");
            await message.WriteToAsync(path, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            logger.LogError("Booking email not sent to {Email}: BookingEmail:Host is not configured.", toEmail);
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
