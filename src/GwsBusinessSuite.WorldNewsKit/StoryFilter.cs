namespace GwsBusinessSuite.WorldNewsKit;

// The story document's region and language chips (06-11-26): which outlets "Who is reporting it",
// the who-reports-what grid, the per-outlet comparisons and the sources show. Region is null (all),
// Western, NonWestern or one of WorldRegions; Language is null (all) or a language name.
public sealed record StoryFilter(string? Region = null, string? Language = null)
{
    public const string Western = "Western";
    public const string NonWestern = "Non-Western";

    public static readonly StoryFilter Everything = new();

    public bool IsEverything => Region is null && Language is null;

    public bool Matches(string? country, string? language) =>
        (Region switch
        {
            null => true,
            Western => WorldRegions.IsWestern(country),
            NonWestern => !WorldRegions.IsWestern(country),
            _ => WorldRegions.Of(country) == Region
        })
        && (Language is null || string.Equals(language, Language, StringComparison.OrdinalIgnoreCase));

    public bool Matches(StoryOutlet outlet) => Matches(outlet.Country, outlet.Language);

    public bool Matches(WorldArticle article) => Matches(article.Country, article.Language);

    // An outlet the analysis names (a grid row, a comparison). The model can spell an outlet in a way
    // the story's outlets don't, and one it can't place is only shown unfiltered.
    public bool Matches(StoryDocument story, string outletName) =>
        IsEverything || story.Outlet(outletName) is { } outlet && Matches(outlet);

    public StoryFilter WithRegion(string? region) => this with { Region = region };

    public StoryFilter WithLanguage(string? language) => this with { Language = language };

    // The region chips: Western and Non-Western when the story has both, then each region present,
    // in WorldRegions.All order, with how many outlets each has.
    public static IReadOnlyList<(string Region, int Outlets)> Regions(StoryDocument story)
    {
        var chips = new List<(string, int)>();
        var western = story.Outlets.Count(outlet => outlet.IsWestern);
        if (western > 0 && western < story.Outlets.Count)
        {
            chips.Add((Western, western));
            chips.Add((NonWestern, story.Outlets.Count - western));
        }
        var byRegion = story.Outlets.GroupBy(outlet => outlet.Region).ToDictionary(group => group.Key, group => group.Count());
        if (byRegion.Count > 1)
            chips.AddRange(WorldRegions.All.Where(byRegion.ContainsKey).Select(region => (region, byRegion[region])));
        return chips;
    }

    // The language chips, most outlets first, when the story is in more than one language.
    public static IReadOnlyList<(string Language, int Outlets)> Languages(StoryDocument story)
    {
        var languages = story.Outlets.GroupBy(outlet => outlet.Language)
            .Select(group => (group.Key, group.Count()))
            .OrderByDescending(language => language.Item2)
            .ThenBy(language => language.Key, StringComparer.Ordinal)
            .ToList();
        return languages.Count > 1 ? languages : [];
    }
}
