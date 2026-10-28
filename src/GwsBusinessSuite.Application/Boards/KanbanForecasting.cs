namespace GwsBusinessSuite.Application.Boards;

// Forecasts (Boards K8): when will this work be done, from how the board really finishes cards.
// Monte Carlo - each trial replays days drawn at random from the board's recent history (how many
// cards it finished each day, idle days included) until the remaining cards are done; thousands
// of trials give the chance of being done by each date. Nobody estimates anything.
//
// The first simulated day is today, so work one good day can finish is done today. Curve: for each
// day from today, the share of trials finished by the end of it (0-1), up to P95.
public sealed record KanbanForecast(
    int Remaining,
    int SampleDays,
    int FinishedInSample,
    DateOnly? P50,
    DateOnly? P85,
    DateOnly? P95,
    IReadOnlyList<KanbanForecastPoint> Curve)
{
    // Too little history to say anything honest: nothing finished in the sample.
    public bool CanForecast => P85 is not null;
}

public sealed record KanbanForecastPoint(DateOnly Day, double Chance);

public static class KanbanForecasting
{
    public const int Trials = 4000;
    private const int MaxDays = 3650;

    // dailyDone: cards finished on each of the last days (oldest first), zeros included.
    public static KanbanForecast Forecast(IReadOnlyList<int> dailyDone, int remaining, DateOnly today, int seed = 20261028)
    {
        var finished = dailyDone.Sum();
        if (remaining <= 0)
            return new KanbanForecast(0, dailyDone.Count, finished, today, today, today, [new KanbanForecastPoint(today, 1)]);
        if (dailyDone.Count == 0 || finished == 0)
            return new KanbanForecast(remaining, dailyDone.Count, finished, null, null, null, []);

        var random = new Random(seed);
        var daysNeeded = new int[Trials];
        for (var trial = 0; trial < Trials; trial++)
        {
            var left = remaining;
            var days = 0;
            while (left > 0 && days < MaxDays)
            {
                left -= dailyDone[random.Next(dailyDone.Count)];
                days++;
            }
            daysNeeded[trial] = days;
        }
        Array.Sort(daysNeeded);
        int At(double share) => daysNeeded[Math.Clamp((int)Math.Ceiling(share * Trials) - 1, 0, Trials - 1)];

        DateOnly Day(int days) => today.AddDays(days - 1);
        var p95 = At(0.95);
        var curve = new List<KanbanForecastPoint>();
        var index = 0;
        for (var days = 1; days <= p95; days++)
        {
            while (index < Trials && daysNeeded[index] <= days) index++;
            curve.Add(new KanbanForecastPoint(Day(days), (double)index / Trials));
        }
        return new KanbanForecast(remaining, dailyDone.Count, finished, Day(At(0.5)), Day(At(0.85)), Day(p95), curve);
    }
}
