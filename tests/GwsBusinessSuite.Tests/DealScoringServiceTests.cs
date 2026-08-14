using FluentAssertions;
using GwsBusinessSuite.Application.Scoring;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class DealScoringServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldUseANeutralBaseline_WhenThereIsNoClosedDealHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        await fixture.AddDealAsync(contact.Id, "New deal", DealStages.Lead, createdAt: Now.AddDays(-1));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        result.Baseline.ClosedDealCount.Should().Be(0);
        result.Baseline.HistoricalWinRatePercent.Should().Be(50);
    }

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldComputeTheHistoricalWinRateFromClosedDeals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        await fixture.AddDealAsync(contact.Id, "Won 1", DealStages.Won, createdAt: Now.AddDays(-10), closedAt: Now.AddDays(-5));
        await fixture.AddDealAsync(contact.Id, "Won 2", DealStages.Won, createdAt: Now.AddDays(-20), closedAt: Now.AddDays(-10));
        await fixture.AddDealAsync(contact.Id, "Lost 1", DealStages.Lost, createdAt: Now.AddDays(-10), closedAt: Now.AddDays(-5));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        result.Baseline.ClosedDealCount.Should().Be(3);
        result.Baseline.WonDealCount.Should().Be(2);
        result.Baseline.HistoricalWinRatePercent.Should().Be(66.7);
    }

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldOnlyScoreOpenDeals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        await fixture.AddDealAsync(contact.Id, "Open deal", DealStages.Qualified, createdAt: Now.AddDays(-1));
        await fixture.AddDealAsync(contact.Id, "Closed deal", DealStages.Won, createdAt: Now.AddDays(-10), closedAt: Now.AddDays(-5));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        result.Deals.Should().ContainSingle(deal => deal.DealTitle == "Open deal");
    }

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldRewardRecentEngagement_AndPenalizeNoActivity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var engaged = await fixture.AddContactAsync("Jamie Rivera");
        await fixture.AddActivityAsync(engaged.Id, Now.AddDays(-2));
        var quiet = await fixture.AddContactAsync("Alex Chen");

        await fixture.AddDealAsync(engaged.Id, "Engaged deal", DealStages.Qualified, createdAt: Now.AddDays(-1));
        await fixture.AddDealAsync(quiet.Id, "Quiet deal", DealStages.Qualified, createdAt: Now.AddDays(-1));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        var engagedDeal = result.Deals.Single(deal => deal.DealTitle == "Engaged deal");
        var quietDeal = result.Deals.Single(deal => deal.DealTitle == "Quiet deal");
        engagedDeal.Factors.Should().Contain(factor => factor.Label == "Engagement" && factor.Points == 15);
        quietDeal.Factors.Should().Contain(factor => factor.Label == "Engagement" && factor.Points == -10);
        engagedDeal.Score.Should().BeGreaterThan(quietDeal.Score);
    }

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldPenalizeAnOverdueExpectedCloseDate_AndRewardAFutureOne()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        await fixture.AddDealAsync(contact.Id, "Overdue", DealStages.Qualified, createdAt: Now.AddDays(-1), expectedCloseDate: Now.AddDays(-3));
        await fixture.AddDealAsync(contact.Id, "On track", DealStages.Qualified, createdAt: Now.AddDays(-1), expectedCloseDate: Now.AddDays(10));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        result.Deals.Single(deal => deal.DealTitle == "Overdue").Factors
            .Should().Contain(factor => factor.Label == "Close date" && factor.Points == -10);
        result.Deals.Single(deal => deal.DealTitle == "On track").Factors
            .Should().Contain(factor => factor.Label == "Close date" && factor.Points == 5);
    }

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldRewardPaceWithinTheHistoricalAverage_AndPenalizeStaleDeals()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        // Historical average time-to-win is 10 days (both won deals took exactly 10 days).
        await fixture.AddDealAsync(contact.Id, "Won 1", DealStages.Won, createdAt: Now.AddDays(-20), closedAt: Now.AddDays(-10));
        await fixture.AddDealAsync(contact.Id, "Won 2", DealStages.Won, createdAt: Now.AddDays(-30), closedAt: Now.AddDays(-20));

        await fixture.AddDealAsync(contact.Id, "On pace", DealStages.Qualified, createdAt: Now.AddDays(-5));
        await fixture.AddDealAsync(contact.Id, "Stale", DealStages.Qualified, createdAt: Now.AddDays(-25));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        result.Deals.Single(deal => deal.DealTitle == "On pace").Factors
            .Should().Contain(factor => factor.Label == "Pace" && factor.Points == 10);
        result.Deals.Single(deal => deal.DealTitle == "Stale").Factors
            .Should().Contain(factor => factor.Label == "Pace" && factor.Points == -15);
    }

    [Fact]
    public async Task ScoreOpenDealsAsync_ShouldClampScoresToTheZeroToHundredRange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        // Stack every negative factor: no closed history won't apply here since we want a low
        // win rate; add many Lost deals so the baseline itself is near zero, plus no activity
        // and an overdue close date.
        for (var i = 0; i < 5; i++)
        {
            await fixture.AddDealAsync(contact.Id, $"Lost {i}", DealStages.Lost, createdAt: Now.AddDays(-10), closedAt: Now.AddDays(-5));
        }
        await fixture.AddDealAsync(contact.Id, "Doomed deal", DealStages.Qualified, createdAt: Now.AddDays(-1), expectedCloseDate: Now.AddDays(-3));

        var result = await fixture.Service.ScoreOpenDealsAsync();

        var doomed = result.Deals.Single(deal => deal.DealTitle == "Doomed deal");
        doomed.Score.Should().BeInRange(0, 100);
        doomed.Band.Should().Be(DealScoreBands.Cold);
    }

    [Fact]
    public async Task ScoreDealAsync_ShouldReturnNull_ForADealThatIsNotOpen()
    {
        await using var fixture = await Fixture.CreateAsync();
        var contact = await fixture.AddContactAsync("Jamie Rivera");
        var closed = await fixture.AddDealAsync(contact.Id, "Closed", DealStages.Won, createdAt: Now.AddDays(-10), closedAt: Now.AddDays(-5));

        (await fixture.Service.ScoreDealAsync(closed.Id)).Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new DealScoringService(db, new FixedTimeProvider(Now));
        }

        public ApplicationDbContext Db { get; }
        public DealScoringService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async Task<Contact> AddContactAsync(string fullName)
        {
            var contact = new Contact { FullName = fullName };
            Db.Contacts.Add(contact);
            await Db.SaveChangesAsync();
            return contact;
        }

        public async Task AddActivityAsync(Guid contactId, DateTimeOffset createdAt)
        {
            Db.ContactActivities.Add(new ContactActivity { ContactId = contactId, Note = "Called", CreatedAt = createdAt });
            await Db.SaveChangesAsync();
        }

        public async Task<Deal> AddDealAsync(
            Guid contactId, string title, string stage, DateTimeOffset createdAt,
            DateTimeOffset? closedAt = null, DateTimeOffset? expectedCloseDate = null)
        {
            var deal = new Deal
            {
                ContactId = contactId,
                Title = title,
                Stage = stage,
                CreatedAt = createdAt,
                ClosedAt = closedAt,
                ExpectedCloseDate = expectedCloseDate
            };
            Db.Deals.Add(deal);
            await Db.SaveChangesAsync();
            return deal;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
