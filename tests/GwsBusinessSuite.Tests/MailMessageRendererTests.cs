using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Services;
using MimeKit;

namespace GwsBusinessSuite.Tests;

// How Mail shows a message (31-10-26): nothing in it can run, and nothing tells the sender it was
// opened until "Show images".
public sealed class MailMessageRendererTests
{
    private static MimeMessage Html(string html, params MimeEntity[] parts)
    {
        var builder = new BodyBuilder { HtmlBody = html, TextBody = null };
        foreach (var part in parts.OfType<MimePart>()) builder.LinkedResources.Add(part);
        var message = new MimeMessage { Subject = "Hello", Date = new DateTimeOffset(2026, 10, 31, 9, 5, 0, TimeSpan.Zero) };
        message.From.Add(new MailboxAddress("Sam Sender", "sam@example.com"));
        message.To.Add(new MailboxAddress("Grant", "grant@gwsapp.net"));
        message.Body = builder.ToMessageBody();
        return message;
    }

    [Fact]
    public void Scripts_Handlers_AndFormsAreRemoved_LinksAndStylesStay()
    {
        var message = Html("""
            <html><head><style>.big{font-size:20px}</style></head><body>
            <script>alert(1)</script><p class="big" onclick="steal()">Hi <a href="https://example.com/x">link</a>
            <a href="javascript:alert(2)">bad</a></p><form action="https://evil.test"><input name="pw"></form>
            <iframe src="https://evil.test"></iframe></body></html>
            """);

        var detail = MailMessageRenderer.Render("m1", "t1", message, unread: true, starred: false, showRemoteImages: false);

        detail.HtmlDocument.Should().NotContain("<script").And.NotContain("alert(1)").And.NotContain("onclick").And.NotContain("javascript:")
            .And.NotContain("<form").And.NotContain("<input").And.NotContain("<iframe");
        detail.HtmlDocument.Should().Contain("href=\"https://example.com/x\"").And.Contain(".big").And.Contain("<base target=\"_blank\">")
            .And.Contain("no-referrer");
        detail.From.Should().BeEquivalentTo(new { Name = "Sam Sender", Address = "sam@example.com" });
        detail.TextBody.Should().Contain("Hi link", "a reply can quote an HTML-only message");
    }

    [Fact]
    public void RemoteImages_AreLeftOutAndCounted_UntilAskedFor_InlineImagesAlwaysShow()
    {
        var logo = new MimePart("image", "png") { Content = new MimeContent(new MemoryStream([1, 2, 3])), ContentId = "logo@x" };
        var html = """
            <p>Hi<img src="https://tracker.test/open.gif"><img src="cid:logo@x">
            </p><div style="background-image:url(https://tracker.test/bg.png)">x</div>
            """;

        var hidden = MailMessageRenderer.Render("m1", null, Html(html, logo), false, false, showRemoteImages: false);
        hidden.HtmlDocument.Should().NotContain("tracker.test").And.Contain("data:image/png;base64,AQID");
        hidden.BlockedRemoteImages.Should().Be(2);

        var shown = MailMessageRenderer.Render("m1", null, Html(html, logo), false, false, showRemoteImages: true);
        shown.HtmlDocument.Should().Contain("https://tracker.test/open.gif");
        shown.BlockedRemoteImages.Should().Be(0);
    }

    [Fact]
    public void PlainText_IsEscaped_WithLinksMadeClickable()
    {
        var message = new MimeMessage { Subject = "Plain" };
        message.From.Add(MailboxAddress.Parse("sam@example.com"));
        message.Body = new TextPart("plain") { Text = "Price <b>5</b>\nSee https://example.com/a?b=1" };

        var detail = MailMessageRenderer.Render("m2", null, message, false, false, false);

        detail.HtmlDocument.Should().Contain("&lt;b&gt;5&lt;/b&gt;").And.Contain("<a href=\"https://example.com/a?b=1\">");
        detail.TextBody.Should().Be("Price <b>5</b>\nSee https://example.com/a?b=1");
    }

    [Fact]
    public void Attachments_AreListedInOrder_ButInlineImagesAreNot()
    {
        var builder = new BodyBuilder { TextBody = "See attached" };
        builder.Attachments.Add("report.pdf", new byte[300], new ContentType("application", "pdf"));
        builder.Attachments.Add("photo.jpg", new byte[30], new ContentType("image", "jpeg"));
        var inline = builder.LinkedResources.Add("logo.png", new byte[10]);
        inline.ContentId = "logo@x";
        var message = new MimeMessage { Body = builder.ToMessageBody() };

        var detail = MailMessageRenderer.Render("m3", null, message, false, false, false);

        detail.Attachments.Select(a => (a.Index, a.FileName, a.ContentType)).Should().Equal((0, "report.pdf", "application/pdf"), (1, "photo.jpg", "image/jpeg"));
        detail.Attachments[0].Size.Should().BeInRange(290, 310);
    }
}
