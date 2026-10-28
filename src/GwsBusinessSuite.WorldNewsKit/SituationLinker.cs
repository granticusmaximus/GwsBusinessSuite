namespace GwsBusinessSuite.WorldNewsKit;

// A situation: one developing story across days (clstr's "situation") - the election from the
// first rallies to the results, a train crash and then its inquiry. Sum is the running sum of its
// events' unit centroids, so each event counts once however many reports it has.
public sealed class NewsSituation
{
    public required string Id { get; init; }
    public required float[] Sum { get; init; }
    public DateTimeOffset LastSeen { get; set; }

    private float[]? _centroid;
    public float[] Centroid => _centroid ??= EventClusterer.Normalize(Sum);

    public void Add(NewsEvent e)
    {
        var unit = e.Centroid;
        for (var i = 0; i < Sum.Length; i++) Sum[i] += unit[i];
        _centroid = null;
        if (e.LastSeen > LastSeen) LastSeen = e.LastSeen;
    }
}

public sealed record SituationLink(string EventId, NewsSituation Situation);

// Links events into situations the way events gather reports: each event, oldest first, joins the
// closest situation active within the window if its centroid is similar enough, and otherwise
// starts one (a situation is only shown once a second event joins). Events are linked once, when
// they reach MinArticles - smaller ones are too noisy to place.
// Tuned 28-10-26 on 1,724 live events of 3+ reports over eight days: pairs above 0.78 were nearly
// always one story; 0.72-0.78 mostly one story (the Israeli election, the Anthropic IPO, the
// Ballon d'Or); 0.66-0.72 mixed in topics (Russian recruiting in Africa with African farm
// investment); under 0.66 mostly topics. Linking by the situation's centroid at 0.72 kept the
// Israeli election as one situation (at 0.73+ it split into Hebrew and English coverage) and the
// Fed decision apart from general market news, giving 149 situations covering 440 events.
public static class SituationLinker
{
    public const double DefaultThreshold = 0.72;
    public const int MinArticles = 3;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(14);
    // A situation with no new event for this long is QUIET rather than ACTIVE.
    public static readonly TimeSpan QuietAfter = TimeSpan.FromHours(48);

    public static IReadOnlyList<SituationLink> Link(
        IReadOnlyList<NewsEvent> events,
        List<NewsSituation> situations,
        double threshold,
        TimeSpan window,
        Func<string> newId)
    {
        var links = new List<SituationLink>();
        foreach (var e in events.OrderBy(e => e.FirstSeen))
        {
            NewsSituation? target = null;
            var best = threshold;
            var earliest = e.FirstSeen - window;
            foreach (var candidate in situations)
            {
                if (candidate.LastSeen < earliest) continue;
                var similarity = EventClusterer.Dot(candidate.Centroid, e.Centroid);
                if (similarity >= best)
                {
                    best = similarity;
                    target = candidate;
                }
            }

            if (target is null)
            {
                target = new NewsSituation { Id = newId(), Sum = new float[e.Sum.Length], LastSeen = e.LastSeen };
                situations.Add(target);
            }

            target.Add(e);
            links.Add(new SituationLink(e.Id, target));
        }

        return links;
    }

    public static bool IsActive(DateTimeOffset lastSeen, DateTimeOffset now) => now - lastSeen < QuietAfter;
}
