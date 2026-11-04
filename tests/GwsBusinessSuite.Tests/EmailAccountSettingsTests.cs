using FluentAssertions;
using GwsBusinessSuite.Application.Email;

namespace GwsBusinessSuite.Tests;

public sealed class EmailAccountSettingsTests
{
    [Theory]
    [InlineData("gmail", "smtp.gmail.com", 587, "imap.gmail.com", 993)]
    [InlineData("icloud", "smtp.mail.me.com", 587, "imap.mail.me.com", 993)]
    public void ApplyProviderDefaults_ShouldUseProviderSpecificHosts(
        string provider,
        string expectedSmtpHost,
        int expectedSmtpPort,
        string expectedImapHost,
        int expectedImapPort)
    {
        var settings = new EmailAccountSettings
        {
            Provider = provider,
            EmailAddress = "user@example.com",
            Password = "secret"
        };

        settings.ApplyProviderDefaults();

        settings.SmtpHost.Should().Be(expectedSmtpHost);
        settings.SmtpPort.Should().Be(expectedSmtpPort);
        settings.ImapHost.Should().Be(expectedImapHost);
        settings.ImapPort.Should().Be(expectedImapPort);
    }

    [Fact]
    public void Validate_ShouldRejectBlankPassword()
    {
        var settings = new EmailAccountSettings
        {
            Provider = "gmail",
            EmailAddress = "user@gmail.com",
            Password = ""
        };

        settings.Validate().Should().Contain("required");
    }
}
