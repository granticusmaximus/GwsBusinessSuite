using FluentAssertions;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Application.Operations;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace GwsBusinessSuite.Tests;

// The app's mail account in Settings > Email (31-10-26): what used to be only Smtp__* in .env.
public sealed class MailServerSettingsTests
{
    private static MailServerSettingsUpdate Google(string? password = "abcd efgh ijkl mnop", bool enabled = true) =>
        new(enabled, " smtp.gmail.com ", 587, MailServerSecurity.StartTls, "grant@gwsapp.net", password, "");

    [Fact]
    public async Task Saving_ShouldKeepThePasswordProtected_AndPutTheAccountInUseAtOnce()
    {
        var f = await CreateAsync();

        var view = await f.Service.SaveAsync(Google(), "grant");

        view.Should().BeEquivalentTo(new { Enabled = true, Host = "smtp.gmail.com", HasPassword = true, UpdatedBy = "grant" });
        var row = await f.Db.MailServerSettings.SingleAsync();
        row.PasswordProtected.Should().NotContain("abcd").And.NotBeEmpty();
        f.Account.Current.Should().BeEquivalentTo(new { Host = "smtp.gmail.com", Username = "grant@gwsapp.net", Password = "abcdefghijklmnop", FromAddress = "grant@gwsapp.net" },
            "spaces in a pasted Google app password are dropped; the login is the From address when none is set");

        // Saving again without retyping it keeps the password; turning it off takes it out of use.
        await f.Service.SaveAsync(Google(password: null, enabled: false), "grant");
        (await f.Service.GetAsync()).HasPassword.Should().BeTrue();
        f.Account.Current.Should().BeNull();

        var reloaded = new AppMailAccount();
        reloaded.Set(AppMailAccount.From(await f.Db.MailServerSettings.SingleAsync(), f.Protector));
        reloaded.Current.Should().BeNull();
    }

    [Theory]
    [InlineData("", 587, MailServerSecurity.StartTls, "grant@gwsapp.net", "pw", "", "SMTP server's host name")]
    [InlineData("smtp.gmail.com", 0, MailServerSecurity.StartTls, "grant@gwsapp.net", "pw", "", "port")]
    [InlineData("smtp.gmail.com", 587, "Telnet", "grant@gwsapp.net", "pw", "", "secure the connection")]
    [InlineData("smtp.gmail.com", 587, MailServerSecurity.StartTls, "grant@gwsapp.net", null, "", "password")]
    [InlineData("smtp.example.com", 587, MailServerSecurity.StartTls, "grant", "pw", "", "From address")]
    [InlineData("smtp.example.com", 587, MailServerSecurity.StartTls, "grant", "pw", "not an address", "valid email address")]
    public async Task Saving_ShouldSayWhatToFix(string host, int port, string security, string username, string? password, string from, string expected)
    {
        var f = await CreateAsync();

        var save = () => f.Service.SaveAsync(new MailServerSettingsUpdate(true, host, port, security, username, password, from), "grant");

        (await save.Should().ThrowAsync<MailServerSettingsException>()).Which.Message.Should().Contain(expected);
        f.Account.Current.Should().BeNull();
    }

    [Fact]
    public async Task WhileItsOn_EveryFeaturesMail_ShouldGoThroughIt_FromItsAddress_KeepingTheSendersName()
    {
        var pickup = Directory.CreateTempSubdirectory("gws-mail-account-");
        try
        {
            var account = new AppMailAccount();
            var transport = new MailTransport(null!, null!, NullLogger<MailTransport>.Instance, account);
            var feature = new EmailCampaignEmailOptions { Host = "smtp.env-server.test", FromAddress = "news@gwsapp.net" };
            transport.Describe(feature).Description.Should().Be("SMTP smtp.env-server.test:587", "off, nothing changes");

            account.Set(new AppMailAccountOptions { Host = "smtp.gmail.com", Port = 587, Username = "grant@gwsapp.net", FromAddress = "grant@gwsapp.net", PickupDirectory = pickup.FullName });
            transport.Describe(feature).Description.Should().Be("the app's mail account (grant@gwsapp.net on smtp.gmail.com:587)");

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("GWS Bookings", "bookings@gwsapp.net"));
            message.To.Add(MailboxAddress.Parse("client@example.com"));
            message.Subject = "Booked";
            message.Body = new TextPart("plain") { Text = "See you then." };
            await transport.SendAsync(message, feature, "booking");

            var written = MimeMessage.Load(Directory.GetFiles(pickup.FullName).Single());
            written.From.Mailboxes.Single().Should().BeEquivalentTo(new { Name = "GWS Bookings", Address = "grant@gwsapp.net" });
        }
        finally
        {
            pickup.Delete(recursive: true);
        }
    }

    [Fact]
    public void ItsGoogleLogin_ShouldAlsoBeTheMailInAccount_AheadOfEnv()
    {
        var env = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.gmail.com", ["Smtp:Username"] = "old@gwsapp.net", ["Smtp:Password"] = "envpassword"
        }).Build();
        var account = new AppMailAccount();
        var mailIn = new ServerMailAccount(env, account);
        mailIn.For("support")!.Address.Should().Be("old+support@gwsapp.net");

        account.Set(new AppMailAccountOptions { Host = "smtp.gmail.com", Username = "grant@gwsapp.net", Password = "app password" });
        mailIn.For("support").Should().BeEquivalentTo(new { Host = "imap.gmail.com", Username = "grant@gwsapp.net", Password = "apppassword", Address = "grant+support@gwsapp.net" });

        account.Set(new AppMailAccountOptions { Host = "smtp.fastmail.com", Username = "grant@fastmail.test", Password = "pw" });
        mailIn.For("support").Should().BeNull("plus-address mail-in reads Gmail only");
    }

    [Fact]
    public void WithNoAppPasswordAnywhere_MailIn_ShouldUseTheGoogleConnection()
    {
        var google = new GoogleMailConnection();
        var credentialId = Guid.NewGuid();
        var account = new AppMailAccount();
        var mailIn = new ServerMailAccount(new ConfigurationBuilder().Build(), account, google);
        mailIn.For("sentinel").Should().BeNull();

        google.Set(new GoogleMailConnection.Snapshot(credentialId, "Grant+old@gwsapp.net"));
        mailIn.For("sentinel").Should().BeEquivalentTo(new
        {
            Host = "imap.gmail.com", Port = 993, Username = "Grant+old@gwsapp.net", Password = "", Address = "grant+sentinel@gwsapp.net",
            AuthServId = "mx.google.com", OAuthCredentialId = (Guid?)credentialId
        });

        account.Set(new AppMailAccountOptions { Host = "smtp.gmail.com", Username = "grant@gwsapp.net", Password = "pw" });
        mailIn.For("sentinel")!.OAuthCredentialId.Should().BeNull("an app-password login still comes first");
    }

    [Theory]
    [InlineData("https://www.googleapis.com/auth/gmail.send", false, false)]
    [InlineData("https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.modify", true, false)]
    [InlineData("https://mail.google.com/ https://www.googleapis.com/auth/calendar.events", true, true)]
    public void GoogleScopes_ShouldSayWhatTheConnectionCanDo(string scopes, bool readMail, bool imap)
    {
        GoogleOAuthService.CanReadMail(scopes).Should().Be(readMail);
        GoogleOAuthService.CanUseImap(scopes).Should().Be(imap);
    }

    private sealed record Fixture(ApplicationDbContext Db, MailServerSettingsService Service, AppMailAccount Account, DataProtectionSecretProtector Protector);

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var account = new AppMailAccount();
        return new Fixture(db, new MailServerSettingsService(db, protector, account, TimeProvider.System), account, protector);
    }
}
