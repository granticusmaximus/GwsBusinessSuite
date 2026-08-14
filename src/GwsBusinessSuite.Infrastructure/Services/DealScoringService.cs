using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Scoring;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// A heuristic, explainable scoring engine over this tenant's own historical Won/Lost deals -
// deliberately not a trained ML model (there's no training pipeline, feature store, or model
// file backing this repo, and pretending otherwise would be dishonest). Every point a deal
// scores traces back to a named, human-readable factor - see DealScoreFactor.
public sealed class DealScoringService(IAppDbContext db, TimeProvider timeProvider) : IDealScoringService
{
    private const int BaselineScoreWithNoHistory = 50;

    public async Task<DealScoringResult> ScoreOpenDealsAsync(CancellationToken cancellationToken = default)
    {
        var allDeals = await db.Deals.AsNoTracking().ToListAsync(cancellationToken);
        var baseline = ComputeBaseline(allDeals);

        var openDeals = allDeals.Where(deal => DealStages.Open.Contains(deal.Stage)).ToList();
        if (openDeals.Count == 0) return new DealScoringResult(baseline, []);

        var contactIds = openDeals.Select(deal => deal.ContactId).Distinct().ToList();
        var contactNames = await db.Contacts.AsNoTracking()
            .Where(contact => contactIds.Contains(contact.Id))
            .ToDictionaryAsync(contact => contact.Id, contact => contact.FullName, cancellationToken);
        var lastActivityByContact = (await db.ContactActivities.AsNoTracking()
            .Where(activity => contactIds.Contains(activity.ContactId))
            .Select(activity => new { activity.ContactId, activity.CreatedAt })
            .ToListAsync(cancellationToken))
            .GroupBy(activity => activity.ContactId)
            // Cast to DateTimeOffset? so GetValueOrDefault below returns a real null for a
            // contact with no activity at all, instead of silently defaulting to
            // DateTimeOffset.MinValue and being scored as "hasn't been contacted in ~740,000
            // years" rather than "never contacted".
            .ToDictionary(group => group.Key, group => (DateTimeOffset?)group.Max(activity => activity.CreatedAt));

        var now = timeProvider.GetUtcNow();
        var views = openDeals
            .Select(deal => Score(deal, baseline, contactNames.GetValueOrDefault(deal.ContactId, "Unknown contact"), lastActivityByContact.GetValueOrDefault(deal.ContactId), now))
            .OrderByDescending(view => view.Score)
            .ToList();

        return new DealScoringResult(baseline, views);
    }

    public async Task<DealScoreView?> ScoreDealAsync(Guid dealId, CancellationToken cancellationToken = default)
    {
        var result = await ScoreOpenDealsAsync(cancellationToken);
        return result.Deals.FirstOrDefault(view => view.DealId == dealId);
    }

    private static DealScoringBaseline ComputeBaseline(List<Deal> allDeals)
    {
        var closed = allDeals.Where(deal => deal.Stage is DealStages.Won or DealStages.Lost).ToList();
        var won = closed.Where(deal => deal.Stage == DealStages.Won).ToList();

        var winRate = closed.Count == 0
            ? BaselineScoreWithNoHistory
            : Math.Round(100.0 * won.Count / closed.Count, 1);

        var wonAges = won
            .Where(deal => deal.ClosedAt is not null)
            .Select(deal => (deal.ClosedAt!.Value - deal.CreatedAt).TotalDays)
            .Where(days => days >= 0)
            .ToList();
        double? averageWonAge = wonAges.Count == 0 ? null : Math.Round(wonAges.Average(), 1);

        return new DealScoringBaseline(closed.Count, won.Count, winRate, averageWonAge);
    }

    private static DealScoreView Score(
        Deal deal, DealScoringBaseline baseline, string contactName, DateTimeOffset? lastActivityAt, DateTimeOffset now)
    {
        var factors = new List<DealScoreFactor>
        {
            baseline.ClosedDealCount == 0
                ? new DealScoreFactor("Baseline", BaselineScoreWithNoHistory, "No closed deals yet to learn from - starting at a neutral 50.")
                : new DealScoreFactor("Historical win rate", (int)Math.Round(baseline.HistoricalWinRatePercent),
                    $"{baseline.WonDealCount} of {baseline.ClosedDealCount} past deals ({baseline.HistoricalWinRatePercent}%) were won.")
        };

        if (lastActivityAt is null)
        {
            factors.Add(new DealScoreFactor("Engagement", -10, "No logged activity with this contact yet."));
        }
        else
        {
            var daysSince = (now - lastActivityAt.Value).TotalDays;
            factors.Add(daysSince switch
            {
                <= 7 => new DealScoreFactor("Engagement", 15, $"Last contact activity was {(int)daysSince} day(s) ago."),
                <= 30 => new DealScoreFactor("Engagement", 5, $"Last contact activity was {(int)daysSince} day(s) ago."),
                _ => new DealScoreFactor("Engagement", -5, $"No contact activity in {(int)daysSince} days.")
            });
        }

        var ageDays = (now - deal.CreatedAt).TotalDays;
        if (baseline.AverageWonDealAgeDays is { } averageAge)
        {
            if (ageDays <= averageAge)
            {
                factors.Add(new DealScoreFactor("Pace", 10, $"Open {(int)ageDays} day(s) - within the {averageAge:0.#}-day average time-to-win."));
            }
            else if (ageDays > averageAge * 2)
            {
                factors.Add(new DealScoreFactor("Pace", -15, $"Open {(int)ageDays} day(s) - well past the {averageAge:0.#}-day average time-to-win."));
            }
        }

        if (deal.ExpectedCloseDate is { } expected)
        {
            factors.Add(expected >= now
                ? new DealScoreFactor("Close date", 5, $"Expected to close {expected:MMM d, yyyy}.")
                : new DealScoreFactor("Close date", -10, $"Expected close date ({expected:MMM d, yyyy}) has passed."));
        }

        var total = Math.Clamp(factors.Sum(factor => factor.Points), 0, 100);
        var band = total switch
        {
            >= 70 => DealScoreBands.Hot,
            >= 45 => DealScoreBands.Warm,
            >= 25 => DealScoreBands.Cool,
            _ => DealScoreBands.Cold
        };

        return new DealScoreView(deal.Id, deal.Title, contactName, deal.Stage, deal.ValueUsd, total, band, factors);
    }
}
