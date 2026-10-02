using FluentAssertions;
using GwsBusinessSuite.Application.Campaigns;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class ArticleAlertServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Excerpt_UsesOpeningProse_StripsMarkdownAndTokens_AndEndsWithEllipsis()
    {
        var markdown = """
            # The Article Title

            {{CJ_AD_SLOT_1}}

            Blazor **Server** keeps UI state on the server and streams [diffs](https://example.com) to the browser over SignalR, which makes it a great fit for internal line-of-business tools that need real-time updates without a separate API layer. [VERIFY: claim]

            ```csharp
            var hidden = "code never appears";
            ```
            """;

        var excerpt = ArticleAlertEmailRenderer.Excerpt(markdown, 180);

        excerpt.Should().StartWith("Blazor Server keeps UI state on the server and streams diffs to the browser");
        excerpt.Should().EndWith("…");
        excerpt.Length.Should().BeLessThanOrEqualTo(181);
        excerpt.Should().NotContain("Title").And.NotContain("CJ_AD").And.NotContain("VERIFY").And.NotContain("hidden").And.NotContain("**");
        excerpt[..^1].Should().NotEndWith(" ", "it is cut back to a whole word");
    }

    [Fact]
    public void Excerpt_ShortBody_HasNoEllipsis()
    {
        ArticleAlertEmailRenderer.Excerpt("Just one short paragraph.", 180).Should().Be("Just one short paragraph.");
    }

    [Fact]
    public async Task Subscribe_IsDoubleOptIn_WithCooldown_AndExpiringConfirmation()
    {
        await using var harness = await Harness.CreateAsync();
        var campaign = await harness.Service.CreateCampaignAsync("New posts", "grant");

        var outcome = await harness.Service.SubscribeAsync(campaign.Id, new ArticleAlertSignupRequest { Email = "Reader@Example.com", FirstName = "Riley", SourcePath = "/blog" });

        outcome.Should().Be(ArticleAlertSignupOutcome.CheckInbox);
        harness.Sender.Sent.Should().ContainSingle();
        var confirmation = harness.Sender.Sent.Single();
        confirmation.ToAddress.Should().Be("reader@example.com");
        confirmation.FromAddress.Should().Be("grant@gwsapp.net");
        confirmation.OneClickUnsubscribeUrl.Should().BeNull("a confirmation isn't a list send");
        (await harness.Service.ListSubscribersAsync(campaign.Id)).Single().Status.Should().Be(EmailCampaignSubscriptionStatuses.Pending);

        // Re-submitting straight away doesn't send a second email.
        await harness.Service.SubscribeAsync(campaign.Id, new ArticleAlertSignupRequest { Email = "reader@example.com" });
        harness.Sender.Sent.Should().ContainSingle();

        var token = ConfirmToken(confirmation);
        (await harness.Service.ConfirmAsync(token)).Should().Be(ArticleAlertConfirmOutcome.Confirmed);
        (await harness.Service.ConfirmAsync(token)).Should().Be(ArticleAlertConfirmOutcome.AlreadyConfirmed);
        (await harness.Service.ListSubscribersAsync(campaign.Id)).Single().Status.Should().Be(EmailCampaignSubscriptionStatuses.Confirmed);

        // A second address, confirmed too late.
        await harness.Service.SubscribeAsync(campaign.Id, new ArticleAlertSignupRequest { Email = "late@example.com" });
        harness.Clock.Advance(TimeSpan.FromDays(8));
        (await harness.Service.ConfirmAsync(ConfirmToken(harness.Sender.Sent.Last()))).Should().Be(ArticleAlertConfirmOutcome.Expired);
        (await harness.Service.ConfirmAsync("not-a-token")).Should().Be(ArticleAlertConfirmOutcome.Invalid);
    }

    [Fact]
    public async Task Sweep_AnnouncesNewArticlesOnce_AfterTheGracePeriod_ToConfirmedSubscribersOnly()
    {
        await using var harness = await Harness.CreateAsync();
        var campaign = await harness.Service.CreateCampaignAsync("New posts", "grant");
        await harness.SubscribeAndConfirmAsync(campaign.Id, "confirmed@example.com", "Casey");
        await harness.Service.SubscribeAsync(campaign.Id, new ArticleAlertSignupRequest { Email = "pending@example.com" });
        await harness.AddArticleAsync("Old post", publishedAt: Start.AddDays(-3));
        await harness.Service.SetActiveAsync(campaign.Id, active: true, "grant");
        harness.Sender.Sent.Clear();

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await harness.AddArticleAsync("Shiny new post", publishedAt: harness.Clock.GetUtcNow(),
            body: "This post explains everything about a topic in enough detail to need trimming for the email excerpt shown to subscribers, which is limited to a tidy length so the email stays scannable and the reader clicks through.");

        await harness.Service.ProcessAsync();
        harness.Sender.Sent.Should().BeEmpty("the 15-minute grace period hasn't passed");

        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        await harness.Service.ProcessAsync();
        await harness.Service.ProcessAsync();

        var alert = harness.Sender.Sent.Should().ContainSingle("only the confirmed subscriber, only once, and never the old post").Subject;
        alert.ToAddress.Should().Be("confirmed@example.com");
        alert.Subject.Should().Be("New post: Shiny new post");
        alert.HtmlBody.Should().Contain("Hi Casey,").And.Contain("Shiny new post").And.Contain("(Read More)").And.Contain("…")
            .And.Contain("https://grantwatson.dev/blog/shiny-new-post?utm_source=email");
        alert.OneClickUnsubscribeUrl.Should().StartWith("https://grantwatson.dev/campaigns/alerts/unsubscribe/");
        (await harness.Service.ListAnnouncementsAsync(campaign.Id)).Single().Should().Match<ArticleAnnouncementView>(a =>
            a.ArticleTitle == "Shiny new post" && a.Status == ArticleAnnouncementStatuses.Sent && a.DeliveredCount == 1);

        // Unsubscribing via the email's link stops future alerts.
        var unsubscribeToken = alert.OneClickUnsubscribeUrl!.Split('/').Last();
        (await harness.Service.UnsubscribeAsync(unsubscribeToken)).Should().BeTrue();
        harness.Sender.Sent.Clear();
        await harness.AddArticleAsync("Another post", publishedAt: harness.Clock.GetUtcNow());
        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        await harness.Service.ProcessAsync();
        harness.Sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_SkipsAnArticleUnpublishedDuringTheGracePeriod()
    {
        await using var harness = await Harness.CreateAsync();
        var campaign = await harness.Service.CreateCampaignAsync("New posts", "grant");
        await harness.SubscribeAndConfirmAsync(campaign.Id, "reader@example.com", "Riley");
        await harness.Service.SetActiveAsync(campaign.Id, active: true, "grant");
        harness.Sender.Sent.Clear();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var article = await harness.AddArticleAsync("Oops", publishedAt: harness.Clock.GetUtcNow());
        await harness.Service.ProcessAsync();

        article.Status = ArticleStatuses.Draft;
        await harness.Db.SaveChangesAsync();
        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        await harness.Service.ProcessAsync();

        harness.Sender.Sent.Should().BeEmpty();
        (await harness.Service.ListAnnouncementsAsync(campaign.Id)).Single().Status.Should().Be(ArticleAnnouncementStatuses.Skipped);
    }

    [Fact]
    public async Task Sweep_HonorsTheCategoryFilter()
    {
        await using var harness = await Harness.CreateAsync();
        var campaign = await harness.Service.CreateCampaignAsync(".NET only", "grant");
        var dotnet = new ArticleCategory { Name = ".NET", Slug = "dotnet" };
        harness.Db.ArticleCategories.Add(dotnet);
        await harness.Db.SaveChangesAsync();
        var settings = campaign.Settings;
        settings.CategoryIds = [dotnet.Id];
        await harness.Service.SaveCampaignAsync(campaign.Id, campaign.Name, campaign.Description, settings, "grant");
        await harness.SubscribeAndConfirmAsync(campaign.Id, "reader@example.com", "Riley");
        await harness.Service.SetActiveAsync(campaign.Id, active: true, "grant");
        harness.Sender.Sent.Clear();
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        await harness.AddArticleAsync("Cooking post", publishedAt: harness.Clock.GetUtcNow());
        await harness.AddArticleAsync("EF Core post", publishedAt: harness.Clock.GetUtcNow(), categoryId: dotnet.Id);
        harness.Clock.Advance(TimeSpan.FromMinutes(16));
        await harness.Service.ProcessAsync();

        harness.Sender.Sent.Should().ContainSingle().Which.Subject.Should().Contain("EF Core post");
    }

    private static string ConfirmToken(OutgoingCampaignEmail email)
    {
        var line = email.TextBody.Split('\n').First(l => l.StartsWith("Confirm: ", StringComparison.Ordinal));
        return line.Trim().Split("/campaigns/confirm/")[1];
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Harness(SqliteConnection connection, ApplicationDbContext db, ArticleAlertService service, CapturingSender sender, MutableClock clock)
        {
            _connection = connection;
            Db = db;
            Service = service;
            Sender = sender;
            Clock = clock;
        }

        public ApplicationDbContext Db { get; }
        public ArticleAlertService Service { get; }
        public CapturingSender Sender { get; }
        public MutableClock Clock { get; }

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var clock = new MutableClock(Start);
            var sender = new CapturingSender();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Canvas:PublicBaseUrl"] = "https://grantwatson.dev", ["Canvas:SiteSlug"] = "grantwatson-dev" })
                .Build();
            var service = new ArticleAlertService(db, new CrmService(db), new CmsBuilderService(db), sender,
                new EphemeralDataProtectionProvider(), configuration, clock, NullLogger<ArticleAlertService>.Instance);
            return new Harness(connection, db, service, sender, clock);
        }

        public async Task SubscribeAndConfirmAsync(Guid campaignId, string email, string firstName)
        {
            await Service.SubscribeAsync(campaignId, new ArticleAlertSignupRequest { Email = email, FirstName = firstName });
            (await Service.ConfirmAsync(ConfirmToken(Sender.Sent.Last()))).Should().Be(ArticleAlertConfirmOutcome.Confirmed);
        }

        public async Task<Article> AddArticleAsync(string title, DateTimeOffset publishedAt, string body = "A short body.", Guid? categoryId = null)
        {
            var article = new Article
            {
                Title = title,
                Slug = title.ToLowerInvariant().Replace(' ', '-'),
                BodyMarkdown = body,
                Status = ArticleStatuses.Published,
                PublishedAt = publishedAt,
                PublishedAtUnixSeconds = publishedAt.ToUnixTimeSeconds(),
                CategoryId = categoryId
            };
            Db.Articles.Add(article);
            await Db.SaveChangesAsync();
            return article;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class CapturingSender : IArticleAlertEmailSender
    {
        public List<OutgoingCampaignEmail> Sent { get; } = [];
        public bool IsConfigured => true;

        public Task<bool> SendAsync(OutgoingCampaignEmail email, CancellationToken cancellationToken = default)
        {
            Sent.Add(email);
            return Task.FromResult(true);
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
