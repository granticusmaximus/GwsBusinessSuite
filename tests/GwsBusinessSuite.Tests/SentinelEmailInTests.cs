using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelEmailInTests
{
    private const string AuthServ = "mx.google.com";
    private const string Pass = "mx.google.com; dkim=pass header.i=@acme.com header.s=g1; spf=pass smtp.mailfrom=grant@acme.com; dmarc=pass (p=NONE) header.from=acme.com";

    [Theory]
    [InlineData(Pass, "acme.com", true)]
    [InlineData("mx.google.com; dkim=pass header.d=acme.com; dmarc=none header.from=acme.com", "acme.com", true)]
    [InlineData("mx.google.com; dkim=pass header.i=@evil.com; spf=pass; dmarc=fail header.from=acme.com", "acme.com", false)]
    [InlineData("mx.google.com; spf=pass smtp.mailfrom=grant@acme.com; dmarc=none header.from=acme.com", "acme.com", false)]
    [InlineData("evil.example; dkim=pass header.d=acme.com; dmarc=pass header.from=acme.com", "acme.com", false)]
    [InlineData(Pass, "other.com", false)]
    public void IsAuthenticated_ShouldOnlyTrustTheReceivingServersPassForTheFromDomain(string header, string domain, bool expected)
    {
        SentinelEmailIn.IsAuthenticated([header], AuthServ, domain).Should().Be(expected);
    }

    [Fact]
    public void ParseSubject_ShouldTakeTagsOut_AndDropReplyPrefixes()
    {
        var (title, tags) = SentinelEmailIn.ParseSubject("Fwd: RE: Q3 plan #client/acme #todo");

        title.Should().Be("Q3 plan");
        tags.Should().Equal("client/acme", "todo");
    }

    [Fact]
    public async Task Import_ShouldCreateAPageUnderInbox_WithTagsBodyAndAttachments()
    {
        var (db, service) = await CreateAsync();
        var message = Message("grant@acme.com", "Meeting notes #client/acme", Pass, html: "<p>Agreed <b>scope</b>.</p><script>x()</script>");
        var builder = new BodyBuilder { HtmlBody = "<p>Agreed <b>scope</b>.</p><script>x()</script>" };
        builder.Attachments.Add("plan.pdf", "%PDF-1.4 test"u8.ToArray(), new ContentType("application", "pdf"));
        builder.Attachments.Add("photo.png", new byte[] { 137, 80, 78, 71 }, new ContentType("image", "png"));
        message.Body = builder.ToMessageBody();

        var log = await service.ImportAsync(message, AuthServ);

        log.Status.Should().Be(SentinelEmailIn.Statuses.Imported, log.Reason);
        log.Username.Should().Be("grant");
        log.AttachmentCount.Should().Be(2);
        var page = await db.WikiPages.SingleAsync(p => p.Id == log.WikiPageId);
        var inbox = await db.WikiPages.SingleAsync(p => p.SystemKey == SentinelEmailIn.FolderSystemKey);
        page.ParentWikiPageId.Should().Be(inbox.Id);
        page.Title.Should().Be("Meeting notes");
        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
        var text = string.Join("\n", blocks.Select(block => string.Concat(block.RichText.Select(span => span.Text))));
        text.Should().Contain("Emailed by").And.Contain("#client/acme").And.Contain("Agreed scope.").And.NotContain("x()");
        blocks.Should().Contain(block => block.Type == WikiBlockTypes.Embed && block.Props["url"].StartsWith("/admin/sentinel/files/"));
        blocks.Should().Contain(block => block.Type == WikiBlockTypes.Image);
        (await db.SentinelImportedFiles.CountAsync(file => file.NotionBlockId.StartsWith("email-in:"))).Should().Be(2);
    }

    [Fact]
    public async Task Import_ShouldKeepPlainTextParagraphs_AndNeverImportTheSameMessageTwice()
    {
        var (db, service) = await CreateAsync();
        var message = Message("grant@acme.com", "", Pass, text: "First line\nsame paragraph.\n\n- not *markdown*");

        var first = await service.ImportAsync(message, AuthServ);
        var again = await service.ImportAsync(message, AuthServ);

        again.Id.Should().Be(first.Id);
        (await db.SentinelInboundEmails.CountAsync()).Should().Be(1);
        var page = await db.WikiPages.SingleAsync(p => p.Id == first.WikiPageId);
        page.Title.Should().Be("Email from Grant");
        var paragraphs = WikiBlockJson.ParseBlocks(page.BlocksJson)
            .Where(block => block.Type == WikiBlockTypes.Paragraph)
            .Select(block => string.Concat(block.RichText.Select(span => span.Text))).ToList();
        paragraphs.Should().Contain("First line same paragraph.").And.Contain("- not *markdown*");
    }

    [Theory]
    [InlineData("stranger@acme.com", Pass, "known user")]
    [InlineData("grant@acme.com", "mx.google.com; spf=softfail; dmarc=fail header.from=acme.com", "forged")]
    [InlineData("grant@acme.com", "attacker.example; dmarc=pass header.from=acme.com", "forged")]
    public async Task Import_ShouldRejectUnknownOrUnauthenticatedSenders(string from, string authResults, string reason)
    {
        var (db, service) = await CreateAsync();

        var log = await service.ImportAsync(Message(from, "Hi", authResults, text: "body"), AuthServ);

        log.Status.Should().Be(SentinelEmailIn.Statuses.Rejected);
        log.Reason.Should().Contain(reason);
        log.WikiPageId.Should().BeNull();
        (await db.WikiPages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Import_ShouldNeedEditOnInbox_ForANonAdminSender()
    {
        var (db, service) = await CreateAsync();

        var denied = await service.ImportAsync(Message("sam@acme.com", "Idea", Pass.Replace("grant@", "sam@"), text: "x"), AuthServ);
        denied.Reason.Should().Contain("edit access");

        var inbox = await db.WikiPages.SingleAsync(p => p.SystemKey == SentinelEmailIn.FolderSystemKey);
        await new SentinelAccessService(db).SetPermissionAsync(inbox.Id, false, "sam", SentinelAccessLevels.Edit, "grant");
        var allowed = await service.ImportAsync(Message("sam@acme.com", "Idea 2", Pass.Replace("grant@", "sam@"), text: "x"), AuthServ);
        allowed.Status.Should().Be(SentinelEmailIn.Statuses.Imported);
    }

    [Fact]
    public async Task Settings_ShouldBeAdminOnly_StoreThePasswordProtected_AndKeepItWhenLeftEmpty()
    {
        var (db, service) = await CreateAsync();
        var update = new SentinelMailboxUpdate(true, "imap.gmail.com", 993, "sentinel@acme.com", "abcd efgh ijkl mnop", "INBOX", AuthServ);

        await service.Invoking(s => s.SaveSettingsAsync(update, "sam")).Should().ThrowAsync<SentinelEmailInException>();
        var saved = await service.SaveSettingsAsync(update, "grant");
        await service.SaveSettingsAsync(update with { Password = null, Folder = "Sentinel" }, "grant");

        saved.HasPassword.Should().BeTrue();
        var row = await db.SentinelMailboxSettings.AsNoTracking().SingleAsync();
        row.PasswordProtected.Should().Be("protected:abcdefghijklmnop");
        row.Folder.Should().Be("Sentinel");
        await service.Invoking(s => s.SaveSettingsAsync(update with { Host = "bad host" }, "grant")).Should().ThrowAsync<SentinelEmailInException>();
    }

    [Fact]
    public async Task Poll_ShouldDoNothingWhileDisabled()
    {
        var (_, service) = await CreateAsync();

        (await service.PollAsync()).Should().Be(0);
    }

    private static MimeMessage Message(string from, string subject, string authResults, string? html = null, string? text = null)
    {
        var name = from.Split('@')[0];
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(char.ToUpperInvariant(name[0]) + name[1..], from));
        message.To.Add(new MailboxAddress("Sentinel", "sentinel@acme.com"));
        message.Subject = subject;
        message.MessageId = $"{Guid.NewGuid():N}@acme.com";
        message.Headers.Add("Authentication-Results", authResults);
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();
        return message;
    }

    private static async Task<(ApplicationDbContext Db, SentinelEmailInService Service)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var grant = new AppUser { Username = "grant", Role = AppRoles.Admin };
        var sam = new AppUser { Username = "sam", Role = AppRoles.Author };
        db.AppUsers.AddRange(grant, sam);
        db.MemberProfiles.AddRange(
            new MemberProfile { AppUserId = grant.Id, Email = "Grant@Acme.com" },
            new MemberProfile { AppUserId = sam.Id, Email = "sam@acme.com" });
        await db.SaveChangesAsync();
        var service = new SentinelEmailInService(db, new WikiService(db), new SentinelAccessService(db), new PrefixProtector(),
            TimeProvider.System, NullLogger<SentinelEmailInService>.Instance);
        return (db, service);
    }

    private sealed class PrefixProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }
}
