namespace GwsBusinessSuite.WorldNewsKit;

// Hourly re-check of the last 48 hours (19-11-26, deferred from the grouping plan of 18-11-26).
// The place guard only stops new joins, so a story merged before it, or by a join it didn't catch,
// stayed merged. This applies the story page's split suggestions (SplitSuggestions: reports naming
// only countries the rest of the story doesn't) automatically. A story a person has already
// corrected is left alone: their decision stands.
public static class RecentMergeCheck
{
    public const string Actor = "automatic (hourly re-check)";
    public static readonly TimeSpan Window = TimeSpan.FromHours(48);
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);
    public const int MaxStories = 300;
    public const int MaxSplits = 25;

    // Returns how many splits were made.
    public static int Run(WorldNewsStore store, DateTimeOffset now)
    {
        var splits = 0;
        foreach (var story in store.TopEvents(now - Window, MaxStories))
        {
            if (splits >= MaxSplits) break;
            if (store.GroupingHistory(story.Id).Any(entry => !entry.Actor.StartsWith("automatic", StringComparison.Ordinal))) continue;
            foreach (var suggestion in SplitSuggestions.For(store.ArticlesForEvent(story.Id)))
            {
                if (splits >= MaxSplits) break;
                if (store.SplitReports(story.Id, suggestion.ArticleIds, vectors: null, Actor, now) is not null) splits++;
            }
        }
        return splits;
    }
}
