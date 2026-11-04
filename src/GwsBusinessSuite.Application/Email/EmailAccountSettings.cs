namespace GwsBusinessSuite.Application.Email;

public enum EmailProviderType
{
    Gmail,
    ICloud,
    Custom
}

public sealed class EmailAccountSettings
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "gmail";
    public string EmailAddress { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public bool ImapUseSsl { get; set; } = true;
    public bool UseAppPassword { get; set; } = true;

    public void ApplyProviderDefaults()
    {
        var provider = Provider.Trim();
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = "gmail";
        }

        Provider = provider;

        if (provider.Equals("gmail", StringComparison.OrdinalIgnoreCase))
        {
            SmtpHost ??= "smtp.gmail.com";
            SmtpPort = SmtpPort <= 0 ? 587 : SmtpPort;
            ImapHost ??= "imap.gmail.com";
            ImapPort = ImapPort <= 0 ? 993 : ImapPort;
            SmtpUseSsl = true;
            ImapUseSsl = true;
            return;
        }

        if (provider.Equals("icloud", StringComparison.OrdinalIgnoreCase))
        {
            SmtpHost ??= "smtp.mail.me.com";
            SmtpPort = SmtpPort <= 0 ? 587 : SmtpPort;
            ImapHost ??= "imap.mail.me.com";
            ImapPort = ImapPort <= 0 ? 993 : ImapPort;
            SmtpUseSsl = true;
            ImapUseSsl = true;
            return;
        }

        SmtpHost ??= "smtp.example.com";
        ImapHost ??= "imap.example.com";
        SmtpPort = SmtpPort <= 0 ? 587 : SmtpPort;
        ImapPort = ImapPort <= 0 ? 993 : ImapPort;
    }

    public string Validate()
    {
        ApplyProviderDefaults();

        if (string.IsNullOrWhiteSpace(EmailAddress))
        {
            return "Email address is required.";
        }

        if (string.IsNullOrWhiteSpace(Password))
        {
            return "An app password or account password is required.";
        }

        if (string.IsNullOrWhiteSpace(SmtpHost))
        {
            return "SMTP host is required.";
        }

        if (SmtpPort <= 0 || SmtpPort > 65535)
        {
            return "SMTP port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(ImapHost))
        {
            return "IMAP host is required.";
        }

        if (ImapPort <= 0 || ImapPort > 65535)
        {
            return "IMAP port must be between 1 and 65535.";
        }

        return string.Empty;
    }
}

public sealed record EmailAccountValidationResult(bool IsSuccess, string Message);

public sealed record EmailAccountSendResult(bool IsSuccess, string Message);
