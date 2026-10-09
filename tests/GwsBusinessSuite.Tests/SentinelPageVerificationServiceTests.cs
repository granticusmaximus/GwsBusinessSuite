using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelPageVerificationServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task VerifyAsync_ShouldRecordWhoAndUntilWhen_AndMakeThemOwnerIfThereIsNone()
    {
        var (db, time, service) = await CreateAsync();
        var page = await AddPageAsync(db, "Expense policy");

        var view = await service.VerifyAsync(page.Id, 90, "grant");

        view.State.Should().Be(SentinelVerificationStates.Verified);
        view.VerifiedBy.Should().Be("grant");
        view.OwnerUsername.Should().Be("grant");
        view.VerifiedUntil.Should().Be(Start.AddDays(90));

        time.UtcNow = Start.AddDays(91);
        (await service.GetAsync(page.Id))!.State.Should().Be(SentinelVerificationStates.Expired);
    }

    [Fact]
    public async Task VerifyAsync_ShouldRejectAPeriodThatIsNotOffered()
    {
        var (db, _, service) = await CreateAsync();
        var page = await AddPageAsync(db, "Policy");

        var act = () => service.VerifyAsync(page.Id, 7, "grant");

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task VerifyAsync_ShouldNeedEditAccess()
    {
        var (db, _, service) = await CreateAsync();
        var page = await AddPageAsync(db, "Restricted");
        // Sam can only view this page.
        db.SentinelResourcePermissions.Add(new SentinelResourcePermission
        {
            TargetId = page.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View
        });
        await db.SaveChangesAsync();

        var act = () => service.VerifyAsync(page.Id, 30, "sam");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SetOwnerAsync_ShouldOnlyAcceptActiveUsers()
    {
        var (db, _, service) = await CreateAsync();
        var page = await AddPageAsync(db, "Policy");

        (await service.SetOwnerAsync(page.Id, "Sam", "grant")).OwnerUsername.Should().Be("sam");
        var act = () => service.SetOwnerAsync(page.Id, "nobody", "grant");
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await service.SetOwnerAsync(page.Id, null, "grant")).OwnerUsername.Should().BeNull();
    }

    [Fact]
    public async Task NotifyLapsedAsync_ShouldBellTheOwnerOnce_AndAgainOnlyAfterReverifying()
    {
        var (db, time, service) = await CreateAsync();
        var page = await AddPageAsync(db, "Onboarding");
        await service.VerifyAsync(page.Id, 30, "grant");
        await service.SetOwnerAsync(page.Id, "sam", "grant");

        time.UtcNow = Start.AddDays(29);
        (await service.NotifyLapsedAsync()).Should().Be(0);

        time.UtcNow = Start.AddDays(31);
        (await service.NotifyLapsedAsync()).Should().Be(1);
        (await service.NotifyLapsedAsync()).Should().Be(0);
        var bell = await db.SentinelNotifications.SingleAsync();
        bell.Username.Should().Be("sam");
        bell.Kind.Should().Be(SentinelPageVerificationRules.ExpiredNotificationKind);
        bell.WikiPageId.Should().Be(page.Id);

        await service.VerifyAsync(page.Id, 30, "grant");
        time.UtcNow = Start.AddDays(62);
        (await service.NotifyLapsedAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ListNeedsReviewAsync_ShouldListLapsedAndStaleOwnedPages_ButNotUntrackedOrTrashedOnes()
    {
        var (db, time, service) = await CreateAsync();
        var lapsed = await AddPageAsync(db, "Lapsed");
        await service.VerifyAsync(lapsed.Id, 30, "grant");
        var staleOwned = await AddPageAsync(db, "Stale owned", editedAt: Start.AddDays(-400));
        await service.SetOwnerAsync(staleOwned.Id, "grant", "grant");
        var freshOwned = await AddPageAsync(db, "Fresh owned", editedAt: Start.AddDays(-10));
        await service.SetOwnerAsync(freshOwned.Id, "grant", "grant");
        await AddPageAsync(db, "Old but untracked", editedAt: Start.AddDays(-900));
        var trashed = await AddPageAsync(db, "Trashed");
        await service.VerifyAsync(trashed.Id, 30, "grant");
        trashed.TrashedAt = Start;
        await db.SaveChangesAsync();

        time.UtcNow = Start.AddDays(31);
        var items = await service.ListNeedsReviewAsync("grant");

        items.Select(item => (item.Title, item.Reason)).Should().Equal(
            ("Lapsed", SentinelNeedsReviewReasons.Expired),
            ("Stale owned", SentinelNeedsReviewReasons.NeverVerified));
    }

    [Fact]
    public async Task Search_ShouldRankAVerifiedPageAboveAnEqualUnverifiedOne()
    {
        var (db, _, service) = await CreateAsync();
        var unverified = await AddPageAsync(db, "Travel policy A");
        var verified = await AddPageAsync(db, "Travel policy B");
        await service.VerifyAsync(verified.Id, 90, "grant");
        var search = new SentinelWorkspaceService(db, new FakeTimeProvider { UtcNow = Start });

        var results = await search.SearchAsync("travel policy", "grant");

        results.Select(result => result.Id).Should().ContainInOrder(verified.Id, unverified.Id);
        results.First().MatchKind.Should().EndWith("verified");
    }

    private static async Task<WikiPage> AddPageAsync(ApplicationDbContext db, string title, DateTimeOffset? editedAt = null)
    {
        var page = new WikiPage
        {
            Title = title,
            Slug = title.ToLowerInvariant().Replace(' ', '-'),
            CreatedAt = editedAt ?? Start,
            UpdatedAt = editedAt
        };
        db.WikiPages.Add(page);
        await db.SaveChangesAsync();
        return page;
    }

    private static async Task<(ApplicationDbContext Db, FakeTimeProvider Time, SentinelPageVerificationService Service)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "Sam" });
        await db.SaveChangesAsync();
        var time = new FakeTimeProvider { UtcNow = Start };
        return (db, time, new SentinelPageVerificationService(db, time, new SentinelAccessService(db)));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
