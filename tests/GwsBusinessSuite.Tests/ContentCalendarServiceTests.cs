using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentCalendar;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class ContentCalendarServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetItems_ShouldGatherEveryScheduledThing_AndOnlyLetScheduledItemsMove()
    {
        await using var f = await Fixture.CreateAsync();
        var ids = await f.SeedAsync();

        var items = await f.Service.GetItemsAsync(Now.AddDays(-7), Now.AddDays(14));

        items.Select(i => i.At).Should().BeInAscendingOrder();
        items.Single(i => i.Id == ids.ScheduledPost).Should().Match<ContentCalendarItem>(i =>
            i.Kind == ContentCalendarKinds.Post && i.Status == "Scheduled" && i.Upcoming && i.CanReschedule && i.Link == $"/admin/article-editor/{ids.ScheduledPost}");
        items.Single(i => i.Id == ids.PublishedPost).Should().Match<ContentCalendarItem>(i => i.Status == "Published" && !i.Upcoming && !i.CanReschedule);
        items.Should().NotContain(i => i.Id == ids.DraftPost, "a draft isn't scheduled");
        items.Should().NotContain(i => i.Id == ids.TrashedPost);
        items.Single(i => i.Id == ids.ScheduledPage).CanReschedule.Should().BeTrue();
        items.Should().NotContain(i => i.Id == ids.HeaderRegion, "site header/footer regions aren't content");
        items.Single(i => i.Id == ids.ScheduledSocial).CanReschedule.Should().BeTrue();
        items.Single(i => i.Kind == ContentCalendarKinds.AlertEmail).Title.Should().Be("Alert: Fall update");
        items.Single(i => i.Kind == ContentCalendarKinds.Drip).Should().Match<ContentCalendarItem>(i =>
            i.Title == "Onboarding: 2 emails" && !i.CanReschedule, "two sends on one day are one row");
        items.Single(i => i.Kind == ContentCalendarKinds.LiveShow).Upcoming.Should().BeFalse();
    }

    [Fact]
    public async Task Reschedule_ShouldMoveScheduledItems_AndRefuseAnythingAlreadyOut()
    {
        await using var f = await Fixture.CreateAsync();
        var ids = await f.SeedAsync();
        var newTime = Now.AddDays(5).AddHours(3);

        await f.Service.RescheduleAsync(ContentCalendarKinds.Post, ids.ScheduledPost, newTime, "grant");
        await f.Service.RescheduleAsync(ContentCalendarKinds.Page, ids.ScheduledPage, newTime, "grant");
        await f.Service.RescheduleAsync(ContentCalendarKinds.Social, ids.ScheduledSocial, newTime, "grant");

        await using (var db = f.NewDb())
        {
            var post = await db.Articles.SingleAsync(a => a.Id == ids.ScheduledPost);
            post.PublishedAt.Should().Be(newTime);
            post.PublishedAtUnixSeconds.Should().Be(newTime.ToUnixTimeSeconds());
            var page = await db.CmsPages.SingleAsync(p => p.Id == ids.ScheduledPage);
            page.PublishedAt.Should().Be(newTime);
            page.ScheduledPublishTriggerPending.Should().BeTrue("the publish sweep still has to fire at the new time");
            (await db.SocialPosts.SingleAsync(s => s.Id == ids.ScheduledSocial)).ScheduledFor.Should().Be(newTime);
        }

        await FluentActions.Awaiting(() => f.Service.RescheduleAsync(ContentCalendarKinds.Post, ids.PublishedPost, newTime, "grant"))
            .Should().ThrowAsync<InvalidOperationException>("a live post can't be un-published by dragging it");
        await FluentActions.Awaiting(() => f.Service.RescheduleAsync(ContentCalendarKinds.Post, ids.ScheduledPost, Now.AddMinutes(-5), "grant"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*future*");
        await FluentActions.Awaiting(() => f.Service.RescheduleAsync(ContentCalendarKinds.Drip, Guid.NewGuid(), newTime, "grant"))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed record SeedIds(Guid ScheduledPost, Guid PublishedPost, Guid DraftPost, Guid TrashedPost, Guid ScheduledPage, Guid HeaderRegion, Guid ScheduledSocial);

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        private DbContextOptions<ApplicationDbContext> _options = null!;
        public ContentCalendarService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture { _connection = new SqliteConnection("Data Source=:memory:") };
            await f._connection.OpenAsync();
            f._options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f._connection).Options;
            await using (var db = f.NewDb()) await db.Database.EnsureCreatedAsync();
            f.Service = new ContentCalendarService(new Factory(f._options), new FixedClock(Now));
            return f;
        }

        public ApplicationDbContext NewDb() => new(_options);

        public async Task<SeedIds> SeedAsync()
        {
            await using var db = NewDb();
            var scheduled = new Article { Slug = "s", Title = "Coming soon", Status = ArticleStatuses.Published, PublishedAt = Now.AddDays(2) };
            var published = new Article { Slug = "p", Title = "Already out", Status = ArticleStatuses.Published, PublishedAt = Now.AddDays(-2) };
            var draft = new Article { Slug = "d", Title = "Draft", Status = ArticleStatuses.Draft, PublishedAt = Now.AddDays(3) };
            var trashed = new Article { Slug = "t", Title = "Trashed", Status = ArticleStatuses.Published, PublishedAt = Now.AddDays(3), TrashedAt = Now };
            db.Articles.AddRange(scheduled, published, draft, trashed);
            var site = new CmsSite { Name = "Site", Slug = "site" };
            db.CmsSites.Add(site);
            var page = new CmsPage { SiteId = site.Id, Title = "Launch page", Slug = "launch", Status = CmsPageStatuses.Published, PublishedAt = Now.AddDays(4), ScheduledPublishTriggerPending = true };
            var header = new CmsPage { SiteId = site.Id, Title = "Header", Slug = "header", Region = "header", Status = CmsPageStatuses.Published, PublishedAt = Now.AddDays(1) };
            db.CmsPages.AddRange(page, header);
            var social = new SocialPost { Title = "Teaser", Status = SocialPostStatuses.Scheduled, ScheduledFor = Now.AddDays(1) };
            db.SocialPosts.Add(social);
            var alerts = new EmailCampaign { Name = "Blog alerts", Kind = EmailCampaignKinds.ArticleAlerts, Status = EmailCampaignStatuses.Active };
            var drip = new EmailCampaign { Name = "Onboarding", Status = EmailCampaignStatuses.Active };
            db.EmailCampaigns.AddRange(alerts, drip);
            db.ArticleAnnouncements.Add(new ArticleAnnouncement { CampaignId = alerts.Id, ArticleId = scheduled.Id, ArticleTitle = "Fall update", DueAt = Now.AddDays(2).AddMinutes(30), DueAtUnixSeconds = Now.AddDays(2).AddMinutes(30).ToUnixTimeSeconds() });
            db.EmailCampaignEnrollments.AddRange(
                new EmailCampaignEnrollment { CampaignId = drip.Id, ContactId = Guid.NewGuid(), NextSendAt = Now.AddDays(3) },
                new EmailCampaignEnrollment { CampaignId = drip.Id, ContactId = Guid.NewGuid(), NextSendAt = Now.AddDays(3).AddMinutes(10) });
            db.LiveShowSessions.Add(new LiveShowSession { Title = "Q&A", InviteToken = "x", StartedAt = Now.AddDays(-1), InviteExpiresAt = Now });
            await db.SaveChangesAsync();
            return new SeedIds(scheduled.Id, published.Id, draft.Id, trashed.Id, page.Id, header.Id, social.Id);
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
