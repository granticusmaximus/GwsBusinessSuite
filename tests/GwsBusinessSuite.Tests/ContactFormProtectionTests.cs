using System.Net;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Growth;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web;
using GwsBusinessSuite.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace GwsBusinessSuite.Tests;

public sealed class ContactFormProtectionTests
{
    private const string Verified = """{"success":true,"hostname":"grantwatson.dev","action":"contact"}""";

    [Theory]
    [InlineData("", Verified, TurnstileResult.Rejected)]
    [InlineData("token", Verified, TurnstileResult.Verified)]
    [InlineData("token", """{"success":false,"error-codes":["timeout-or-duplicate"]}""", TurnstileResult.Rejected)]
    [InlineData("token", """{"success":true,"hostname":"attacker.test","action":"contact"}""", TurnstileResult.Rejected)]
    [InlineData("token", """{"success":true,"hostname":"grantwatson.dev","action":"login"}""", TurnstileResult.Rejected)]
    [InlineData("token", "not json", TurnstileResult.Unavailable)]
    public async Task Verification_RequiresValidTokenHostnameAndAction(string token, string response, TurnstileResult expected)
    {
        using var handler = new VerificationHandler(response);
        var service = CreateVerifier(handler);
        Assert.Equal(expected, await service.VerifyAsync(token, "grantwatson.dev", "192.0.2.1"));
        Assert.Equal(string.IsNullOrEmpty(token) ? 0 : 1, handler.Calls);
    }

    [Fact]
    public async Task Verification_FailsClosedOnNetworkFailureAndMissingConfiguration()
    {
        using var handler = new VerificationHandler(Verified) { Fail = true };
        Assert.Equal(TurnstileResult.Unavailable,
            await CreateVerifier(handler).VerifyAsync("token", "grantwatson.dev", null));
        Assert.Equal(TurnstileResult.Unavailable,
            await CreateVerifier(handler, configured: false).VerifyAsync("token", "grantwatson.dev", null));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Verification_DoesNotCallProviderForOversizedTokenOrUnapprovedRequestHost()
    {
        using var handler = new VerificationHandler(Verified);
        var verifier = CreateVerifier(handler);
        Assert.Equal(TurnstileResult.Rejected, await verifier.VerifyAsync(new string('x', 2049), "grantwatson.dev", null));
        Assert.Equal(TurnstileResult.Rejected, await verifier.VerifyAsync("token", "attacker.test", null));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ContactEndpoint_GatesPersistenceAndRedirectsAfterSuccess(bool hasToken, bool configured, bool json)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var cms = new CmsBuilderService(db);
        var site = await cms.SaveSiteAsync(new CmsSiteEditorModel { Name = "Grant Watson", Slug = "grantwatson-dev" });
        var page = await cms.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id, Title = "Contact", Slug = "contact", Status = CmsPageStatuses.Published,
            BlocksJson = "{\"sections\":[]}"
        });
        var sender = new CapturingSender();
        site.FormNotificationEmail = "owner@example.test";
        await db.SaveChangesAsync();
        var submissions = new FormSubmissionService(db, sender, Options.Create(new FormNotificationOptions()),
            NullLogger<FormSubmissionService>.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Canvas:SiteSlug"] = site.Slug
        }).Build();
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("grantwatson.dev");
        if (json) context.Request.Headers.Accept = "application/json";
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["_path"] = "/contact/", ["message"] = "Please contact me.",
            [TurnstileService.ResponseField] = hasToken ? "token" : ""
        });
        using var handler = new VerificationHandler(Verified);
        var result = await CmsFormSubmissionEndpoint.HandleAsync(site.Slug, context.Request, context, cms,
            submissions, CreateVerifier(handler, configured), config, isPublicHost: true);

        if (hasToken && configured)
        {
            var submission = Assert.Single(await db.FormSubmissions.ToListAsync());
            Assert.Equal(page.Id, submission.PageId);
            Assert.DoesNotContain(TurnstileService.ResponseField, submission.FieldsJson);
            Assert.Single(sender.Messages);
            if (json)
                Assert.Contains("/contact/thank-you", System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result).Value));
            else
                Assert.Equal("/contact/thank-you", Assert.IsType<RedirectHttpResult>(result).Url);
        }
        else
        {
            Assert.Equal(configured ? 400 : 503, ((IStatusCodeHttpResult)result).StatusCode);
            Assert.Empty(await db.FormSubmissions.ToListAsync());
            Assert.Empty(sender.Messages);
        }
    }

    [Fact]
    public void Protection_IsScopedToConfiguredSitesRootContactPage()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Canvas:SiteSlug"] = "grantwatson-dev" }).Build();
        var site = new CmsSite { Name = "Site", Slug = "grantwatson-dev" };
        var page = new CmsPage { Title = "Contact", Slug = "contact" };
        Assert.Equal("", ContactFormProtection.SiteKeyFor(site, page, config));
        page.ParentPageId = Guid.NewGuid();
        Assert.Null(ContactFormProtection.SiteKeyFor(site, page, config));
        page.ParentPageId = null;
        site.Slug = "another-site";
        Assert.Null(ContactFormProtection.SiteKeyFor(site, page, config));
    }

    private static TurnstileService CreateVerifier(VerificationHandler handler, bool configured = true) => new(
        new HttpClient(handler), Options.Create(new TurnstileOptions
        {
            SiteKey = "site-key", SecretKey = configured ? "secret-key" : "",
            AllowedHostnames = ["grantwatson.dev"]
        }), NullLogger<TurnstileService>.Instance);

    private sealed class VerificationHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("Offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class CapturingSender : IGrowthReportEmailSender
    {
        public GrowthReportDeliveryConfiguration Configuration { get; } = new(true, "Configured");
        public List<GrowthReportEmail> Messages { get; } = [];
        public Task SendAsync(GrowthReportEmail email, CancellationToken cancellationToken = default)
        {
            Messages.Add(email);
            return Task.CompletedTask;
        }
    }
}
