using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// What a situation is about, so Overwatch can show the news that belongs on a map (a train crash,
// an outbreak) and leave the rest (a transfer rumour, a phone launch) to World News. A situation's
// meaning fingerprint is compared with example headlines of each kind; plain keywords stand in
// when the embedding model can't be reached.
public static partial class SituationKinds
{
    public const string Disaster = "disaster";
    public const string Transport = "transport";
    public const string Conflict = "conflict";
    public const string Health = "health";
    public const string Politics = "politics";
    public const string Business = "business";
    public const string Sport = "sport";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Disaster, Transport, Conflict, Health, Politics, Business, Sport, Other];

    // Shown on the globe unless someone turns more on.
    public static readonly IReadOnlyList<string> Operational = [Disaster, Transport, Conflict, Health];

    // Below this similarity to every example (embeddinggemma, cosine) a story is "other": an
    // obituary or a game listing shouldn't be forced into "health" or "sport".
    public const float MinimumScore = 0.40f;

    // Tuned on a day of real situations (29-10-26): written like headlines, because that's what
    // the fingerprints are made of.
    public static readonly IReadOnlyDictionary<string, string[]> Examples = new Dictionary<string, string[]>
    {
        [Disaster] =
        [
            "Earthquake kills dozens and destroys buildings", "Floods force thousands to evacuate their homes",
            "Wildfire burns homes as firefighters battle the blaze", "Hurricane makes landfall with destructive winds",
            "Explosion at chemical plant kills workers", "Volcano erupts and residents flee", "Heatwave and drought hit the region"
        ],
        [Transport] =
        [
            "Two trains collide, injuring passengers", "Plane crashes shortly after takeoff", "Flights cancelled as airport shuts down",
            "Ferry sinks and crew are rescued", "Rail strike halts train services across the country", "Motorway closed after major crash",
            "Ship attacked in shipping lane"
        ],
        [Conflict] =
        [
            "Missile strikes hit the capital overnight", "Troops advance as fighting intensifies on the front line",
            "Protesters clash with riot police", "Gunmen kill civilians in attack", "Ceasefire talks between the warring sides",
            "Drone attack targets military base", "Hostages taken by militants"
        ],
        [Health] =
        [
            "Virus outbreak spreads to new provinces", "Ebola cases rise as health workers race to contain outbreak",
            "Hospitals overwhelmed by patients", "Health officials confirm first case of new disease", "Cholera epidemic kills hundreds"
        ],
        [Politics] =
        [
            "Election results show no clear winner", "Parliament passes controversial new law", "Prime minister resigns after scandal",
            "President meets foreign leaders at summit", "Government unveils budget plans", "Court sentences opposition leader to prison"
        ],
        [Business] =
        [
            "Stock markets fall as investors worry", "Company reports record quarterly profits", "Oil prices rise on supply fears",
            "Central bank holds interest rates steady", "Tech giant unveils new smartphone", "Shares surge on stock market debut"
        ],
        [Sport] =
        [
            "Team wins the championship final", "Striker scores twice as club wins league match", "Player signs for new club in transfer deal",
            "Coach sacked after heavy defeat", "Cricket team wins test match", "Tennis star reaches the final",
            "Celebrity eliminated from TV dance show"
        ]
    };

    private static readonly IReadOnlyDictionary<string, string[]> Keywords = new Dictionary<string, string[]>
    {
        [Disaster] = ["earthquake", "quake", "flood", "floods", "flooding", "wildfire", "wildfires", "hurricane", "typhoon", "cyclone",
            "tornado", "landslide", "tsunami", "volcano", "eruption", "explosion", "blast", "drought", "heatwave"],
        [Transport] = ["train", "trains", "rail", "railway", "flight", "flights", "airport", "plane", "airline", "ferry", "ship",
            "cruise", "motorway", "highway", "metro", "subway", "derailment", "derails"],
        [Conflict] = ["war", "attack", "attacks", "missile", "missiles", "drone", "drones", "troops", "military", "fighting", "ceasefire",
            "truce", "protest", "protests", "protesters", "clashes", "riot", "bomb", "bombing", "shooting", "hostage", "hostages", "militants"],
        [Health] = ["outbreak", "virus", "ebola", "cholera", "measles", "epidemic", "pandemic", "disease", "vaccine", "hospital", "hospitals"],
        [Politics] = ["election", "elections", "vote", "parliament", "president", "minister", "government", "senate", "congress",
            "summit", "referendum", "budget", "court"],
        [Business] = ["market", "markets", "stocks", "shares", "prices", "economy", "inflation", "bank", "ipo", "earnings", "oil",
            "gold", "bitcoin", "tariff", "tariffs", "smartphone"],
        [Sport] = ["match", "league", "cup", "championship", "final", "striker", "coach", "tournament", "cricket", "football",
            "tennis", "nba", "nfl", "olympic", "olympics"]
    };

    public static string Label(string? kind) => kind switch
    {
        Disaster => "Disasters",
        Transport => "Transport",
        Conflict => "Conflict & unrest",
        Health => "Health",
        Politics => "Politics",
        Business => "Business",
        Sport => "Sport & entertainment",
        _ => "Other"
    };

    // The kind whose closest example is most like the situation; examples are unit vectors.
    public static string Classify(float[] situationSum, IReadOnlyDictionary<string, IReadOnlyList<float[]>> examples)
    {
        var unit = EventClusterer.Normalize(situationSum);
        var best = examples
            .Select(pair => (Kind: pair.Key, Score: pair.Value.Max(example => Dot(unit, example))))
            .MaxBy(candidate => candidate.Score);
        return best.Kind is not null && best.Score >= MinimumScore ? best.Kind : Other;
    }

    // The kind with the most keyword hits in English text, or Other.
    public static string FromWords(string text)
    {
        var words = Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var best = Keywords
            .Select(pair => (Kind: pair.Key, Hits: pair.Value.Count(words.Contains)))
            .Where(candidate => candidate.Hits > 0)
            .OrderByDescending(candidate => candidate.Hits)
            .ThenBy(candidate => All.ToList().IndexOf(candidate.Kind))
            .FirstOrDefault();
        return best.Kind ?? Other;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();
}
