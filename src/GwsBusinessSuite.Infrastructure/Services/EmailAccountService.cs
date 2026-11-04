using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class EmailAccountService : IEmailAccountService
{
    public async Task<EmailAccountValidationResult> ValidateAsync(EmailAccountSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var validationMessage = settings.Validate();
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            return new EmailAccountValidationResult(false, validationMessage);
        }

        settings.ApplyProviderDefaults();

        using var client = new SmtpClient();

        try
        {
            var socketOptions = settings.SmtpUseSsl
                ? (settings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable)
                : SecureSocketOptions.None;

            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, socketOptions, ct);
            await client.AuthenticateAsync(settings.EmailAddress, settings.Password, ct);
            await client.DisconnectAsync(true, ct);

            return new EmailAccountValidationResult(true, "Email account validated successfully.");
        }
        catch (Exception ex)
        {
            return new EmailAccountValidationResult(false, $"SMTP validation failed: {ex.Message}");
        }
    }

    public async Task<EmailAccountSendResult> SendAsync(EmailAccountSettings settings, string toAddress, string subject, string body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(toAddress))
        {
            return new EmailAccountSendResult(false, "Recipient email address is required.");
        }

        var validationMessage = settings.Validate();
        if (!string.IsNullOrWhiteSpace(validationMessage))
        {
            return new EmailAccountSendResult(false, validationMessage);
        }

        settings.ApplyProviderDefaults();

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(settings.EmailAddress, settings.EmailAddress));
            message.To.Add(MailboxAddress.Parse(toAddress));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            var socketOptions = settings.SmtpUseSsl
                ? (settings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable)
                : SecureSocketOptions.None;

            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, socketOptions, ct);
            await client.AuthenticateAsync(settings.EmailAddress, settings.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            return new EmailAccountSendResult(true, "Email sent successfully.");
        }
        catch (Exception ex)
        {
            return new EmailAccountSendResult(false, $"Email send failed: {ex.Message}");
        }
    }
}
