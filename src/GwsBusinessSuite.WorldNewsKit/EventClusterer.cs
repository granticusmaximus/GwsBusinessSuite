using System.Numerics;

namespace GwsBusinessSuite.WorldNewsKit;

// One news event: every report, in any language, on the same thing happening (clstr's
// "cluster"). Sum is the running sum of its reports' unit vectors; the centroid is that, scaled
// back to unit length.
public sealed class NewsEvent
{
    public required string Id { get; init; }
    public required string Title { get; set; }
    public required string TitleLanguage { get; set; }
    public required float[] Sum { get; init; }
    public int ArticleCount { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }

    private float[]? _centroid;
    public float[] Centroid => _centroid ??= EventClusterer.Normalize(Sum);

    public void Add(WorldArticle article, float[] unitVector)
    {
        for (var i = 0; i < Sum.Length; i++) Sum[i] += unitVector[i];
        _centroid = null;
        ArticleCount++;
        if (ArticleCount == 1 || article.SeenAt < FirstSeen) FirstSeen = article.SeenAt;
        if (ArticleCount == 1 || article.SeenAt > LastSeen) LastSeen = article.SeenAt;
        PreferEnglishTitle(article.Title, article.Language);
    }

    public void Absorb(NewsEvent other)
    {
        for (var i = 0; i < Sum.Length; i++) Sum[i] += other.Sum[i];
        _centroid = null;
        ArticleCount += other.ArticleCount;
        if (other.FirstSeen < FirstSeen) FirstSeen = other.FirstSeen;
        if (other.LastSeen > LastSeen) LastSeen = other.LastSeen;
        PreferEnglishTitle(other.Title, other.TitleLanguage);
    }

    // Show an English headline once there is one; the reports stay in their own languages.
    private void PreferEnglishTitle(string title, string language)
    {
        if (TitleLanguage == "English" || language != "English") return;
        Title = title;
        TitleLanguage = language;
    }
}

public sealed record EventMerge(NewsEvent Keep, NewsEvent Drop);

public sealed record ClusterResult(IReadOnlyList<(string ArticleId, string EventId)> Assignments, IReadOnlyList<NewsEvent> Touched, int Created);

// Online grouping: each report, oldest first, joins the closest event seen within the window if
// its similarity to that event's centroid reaches the threshold, and otherwise starts a new
// event. Comparing against the centroid rather than single headlines is what keeps a weak
// cross-language pair together (measured 20-10-26 with embeddinggemma: the same flood reported
// in Turkish, Russian, German and English scored 0.47-0.80 pairwise, unrelated news 0.17-0.38,
// and a different country's flood up to 0.54). Reports a feed already grouped (GroupHint) stay
// together without a comparison. The threshold was tuned on 5,800 live reports from 41 Google
// News editions (20-10-26): at 0.6 centroids drift into topics (three separate Ukraine strikes,
// every iPhone story in one event); at 0.7 one story starts splitting by language; at 0.75 even
// the Alberta referendum split into language groups. 0.65 kept Alberta as one event in 11
// languages while separating the topics. Related events are linked later, as situations.
// A feed's grouping (GroupHint) is a strong suggestion, not an order: Google's related-coverage
// lists are looser than one event, so a hinted report joins its group's event only when it is
// within HintSlack of the threshold, and is otherwise matched like any other report.
public static class EventClusterer
{
    public const double DefaultThreshold = 0.65;
    public const double HintSlack = 0.1;
    // Reports are grouped one at a time, so two groups that formed early (often in different
    // languages) can end up as twin events. Twins whose centroids are this close are merged:
    // measured 20-10-26, true twins scored 0.86-0.91 (Alberta in two groups, the Revolut outage in
    // English and Portuguese, a Champions League match in Turkish and Spanish), while the closest
    // pair that must stay apart (Trump on a Putin-Xi summit vs Trump on sanctions waivers) was 0.84.
    public const double DefaultMergeThreshold = 0.85;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(48);

    public static ClusterResult Assign(
        IReadOnlyList<(WorldArticle Article, float[] Vector)> items,
        List<NewsEvent> events,
        Dictionary<string, string> hintEvents,
        double threshold,
        TimeSpan window,
        Func<string> newId)
    {
        var byId = events.ToDictionary(e => e.Id);
        var touched = new HashSet<NewsEvent>();
        var assignments = new List<(string, string)>();
        var created = 0;

        foreach (var (article, raw) in items.OrderBy(i => i.Article.SeenAt))
        {
            var vector = Normalize(raw);
            NewsEvent? target = null;
            if (article.GroupHint is { } hint && hintEvents.TryGetValue(hint, out var hinted)
                && byId.TryGetValue(hinted, out var hintedEvent) && Dot(hintedEvent.Centroid, vector) >= threshold - HintSlack)
                target = hintedEvent;

            if (target is null)
            {
                var best = threshold;
                var earliest = article.SeenAt - window;
                foreach (var candidate in events)
                {
                    if (candidate.LastSeen < earliest) continue;
                    var similarity = Dot(candidate.Centroid, vector);
                    if (similarity >= best)
                    {
                        best = similarity;
                        target = candidate;
                    }
                }
            }

            if (target is null)
            {
                target = new NewsEvent
                {
                    Id = newId(), Title = article.Title, TitleLanguage = article.Language, Sum = new float[vector.Length]
                };
                events.Add(target);
                byId[target.Id] = target;
                created++;
            }

            target.Add(article, vector);
            if (article.GroupHint is { } group) hintEvents.TryAdd(group, target.Id);
            touched.Add(target);
            assignments.Add((article.Id, target.Id));
        }

        return new ClusterResult(assignments, touched.ToList(), created);
    }

    // Merges each changed event into a closer, larger twin (or a smaller twin into it), closest
    // pairs first. The dropped event's reports move to the kept one.
    public static IReadOnlyList<EventMerge> MergeTwins(List<NewsEvent> events, IReadOnlyCollection<NewsEvent> changed, double mergeThreshold)
    {
        var candidates = new List<(float Similarity, NewsEvent A, NewsEvent B)>();
        foreach (var a in changed)
        foreach (var b in events)
        {
            if (ReferenceEquals(a, b) || (changed.Contains(b) && string.CompareOrdinal(a.Id, b.Id) > 0)) continue;
            var similarity = Dot(a.Centroid, b.Centroid);
            if (similarity >= mergeThreshold) candidates.Add((similarity, a, b));
        }

        var dropped = new HashSet<NewsEvent>();
        var merges = new List<EventMerge>();
        foreach (var (_, a, b) in candidates.OrderByDescending(c => c.Similarity))
        {
            if (dropped.Contains(a) || dropped.Contains(b)) continue;
            var (keep, drop) = a.ArticleCount >= b.ArticleCount ? (a, b) : (b, a);
            keep.Absorb(drop);
            dropped.Add(drop);
            merges.Add(new EventMerge(keep, drop));
        }
        events.RemoveAll(dropped.Contains);
        return merges;
    }

    public static float[] Normalize(float[] vector)
    {
        var length = MathF.Sqrt(Dot(vector, vector));
        if (length == 0) return (float[])vector.Clone();
        var unit = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) unit[i] = vector[i] / length;
        return unit;
    }

    public static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
                acc += new Vector<float>(a, i) * new Vector<float>(b, i);
            sum = Vector.Dot(acc, Vector<float>.One);
        }
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }
}
