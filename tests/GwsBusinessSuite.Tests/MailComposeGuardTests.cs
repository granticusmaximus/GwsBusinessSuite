using FluentAssertions;
using GwsBusinessSuite.Application.Mail;

namespace GwsBusinessSuite.Tests;

public sealed class MailComposeGuardTests
{
    [Theory]
    [InlineData("Invoice attached", "Here you go")]
    [InlineData("Invoice", "Please see the attached file.")]
    [InlineData("Photos", "I have attached the originals.")]
    public void MentionsAttachment_ShouldRecognizeExplicitAttachmentLanguage(string subject, string body) =>
        MailComposeGuard.MentionsAttachment(subject, body).Should().BeTrue();

    [Theory]
    [InlineData("Meeting", "Let's attach importance to this.")]
    [InlineData("Invoice", "Here you go")]
    public void MentionsAttachment_ShouldAvoidUnrelatedWording(string subject, string body) =>
        MailComposeGuard.MentionsAttachment(subject, body).Should().BeFalse();

    [Fact]
    public void WithSignature_ShouldAppendOneStandardSeparator() =>
        MailComposeGuard.WithSignature("Hello", " Grant Watson \n GWS ")
            .Should().Be("Hello\n\n-- \nGrant Watson \n GWS");

    [Fact]
    public void WithSignature_WithoutASignature_ShouldLeaveTheBodyAlone() =>
        MailComposeGuard.WithSignature("Hello\n", " ").Should().Be("Hello\n");
}
