using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// Checks on top of similarity when a report joins an event or two events merge (18-11-26).
// PlaceGuard: two headlines that each name countries, none of them shared, are about different
// places ("Floods in Spain" and "Floods in Portugal" score as alike as one story told twice).
// QuietPenalty: an event that has gone quiet needs a closer match to take a new report, rising
// linearly from QuietAfter to the event window's end, so a fresh story doesn't drift into
// yesterday's similar one.
public sealed record GroupingRules(bool PlaceGuard = true, double QuietPenalty = 0.05)
{
    public static readonly TimeSpan QuietAfter = TimeSpan.FromHours(12);

    public bool Conflicts(string titleA, string titleB) =>
        PlaceGuard && HeadlineCountries.Conflict(HeadlineCountries.In(titleA), HeadlineCountries.In(titleB));

    // The similarity a report needs to join an event last seen `gap` before it.
    public double Required(double threshold, TimeSpan gap, TimeSpan window)
    {
        if (QuietPenalty <= 0 || gap <= QuietAfter || window <= QuietAfter) return threshold;
        var share = Math.Min(1, (gap - QuietAfter) / (window - QuietAfter));
        return threshold + QuietPenalty * share;
    }
}

// The countries an English headline names, by name, short form or adjective. Other languages name
// none here, so the place guard leaves them to similarity alone.
public static partial class HeadlineCountries
{
    private static readonly Dictionary<string, string> Names = Build(new Dictionary<string, string[]>
    {
        ["United States"] = ["united states", "us", "u.s.", "usa", "america", "american", "americans"],
        ["United Kingdom"] = ["united kingdom", "uk", "u.k.", "britain", "british", "scotland", "scottish", "wales", "welsh"],
        ["Russia"] = ["russia", "russian", "russians"], ["Ukraine"] = ["ukraine", "ukrainian", "ukrainians"],
        ["China"] = ["china", "chinese"], ["Japan"] = ["japan", "japanese"], ["India"] = ["india", "indian"],
        ["Pakistan"] = ["pakistan", "pakistani"], ["Iran"] = ["iran", "iranian"], ["Iraq"] = ["iraq", "iraqi"],
        ["Israel"] = ["israel", "israeli", "israelis"], ["Palestine"] = ["gaza", "palestinian", "palestinians", "west bank"],
        ["Lebanon"] = ["lebanon", "lebanese"], ["Syria"] = ["syria", "syrian"], ["Turkey"] = ["turkey", "turkish", "türkiye"],
        ["Egypt"] = ["egypt", "egyptian"], ["Saudi Arabia"] = ["saudi", "saudi arabia"], ["Yemen"] = ["yemen", "yemeni", "houthi", "houthis"],
        ["France"] = ["france", "french"], ["Germany"] = ["germany", "german"], ["Italy"] = ["italy", "italian"],
        ["Spain"] = ["spain", "spanish"], ["Portugal"] = ["portugal", "portuguese"], ["Greece"] = ["greece", "greek"],
        ["Netherlands"] = ["netherlands", "dutch"], ["Belgium"] = ["belgium", "belgian"], ["Poland"] = ["poland", "polish"],
        ["Sweden"] = ["sweden", "swedish"], ["Norway"] = ["norway", "norwegian"], ["Ireland"] = ["ireland", "irish"],
        ["Switzerland"] = ["switzerland", "swiss"], ["Austria"] = ["austria", "austrian"], ["Romania"] = ["romania", "romanian"],
        ["Canada"] = ["canada", "canadian"], ["Mexico"] = ["mexico", "mexican"], ["Brazil"] = ["brazil", "brazilian"],
        ["Argentina"] = ["argentina", "argentine", "argentinian"], ["Chile"] = ["chile", "chilean"], ["Colombia"] = ["colombia", "colombian"],
        ["Peru"] = ["peru", "peruvian"], ["Venezuela"] = ["venezuela", "venezuelan"], ["Cuba"] = ["cuba", "cuban"],
        ["Nigeria"] = ["nigeria", "nigerian"], ["Kenya"] = ["kenya", "kenyan"], ["South Africa"] = ["south africa", "south african"],
        ["Ethiopia"] = ["ethiopia", "ethiopian"], ["Sudan"] = ["sudan", "sudanese"], ["Congo"] = ["congo", "congolese"],
        ["Indonesia"] = ["indonesia", "indonesian"], ["Philippines"] = ["philippines", "philippine", "filipino"],
        ["Vietnam"] = ["vietnam", "vietnamese"], ["Thailand"] = ["thailand", "thai"], ["Malaysia"] = ["malaysia", "malaysian"],
        ["South Korea"] = ["south korea", "south korean", "seoul"], ["North Korea"] = ["north korea", "north korean", "pyongyang"],
        ["Taiwan"] = ["taiwan", "taiwanese"], ["Bangladesh"] = ["bangladesh", "bangladeshi"], ["Afghanistan"] = ["afghanistan", "afghan"],
        ["Australia"] = ["australia", "australian"], ["New Zealand"] = ["new zealand"],
    });

    public static IReadOnlySet<string> In(string headline)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var words = Word().Matches(headline.ToLowerInvariant())
            .Select(match => Names.ContainsKey(match.Value) ? match.Value : match.Value.TrimEnd('.'))
            .ToList();
        for (var i = 0; i < words.Count; i++)
        {
            // Up to three words, longest first, so "south korea" isn't read as "korea".
            for (var length = Math.Min(3, words.Count - i); length >= 1; length--)
            {
                var phrase = string.Join(' ', words.Skip(i).Take(length));
                if (!Names.TryGetValue(phrase, out var country)) continue;
                // Lower-case "us" is the pronoun; only "US" or "U.S." names the country.
                if (phrase == "us" && !UnitedStatesAbbreviation().IsMatch(headline)) break;
                found.Add(country);
                i += length - 1;
                break;
            }
        }
        return found;
    }

    public static bool Conflict(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Count > 0 && b.Count > 0 && !a.Overlaps(b);

    private static Dictionary<string, string> Build(Dictionary<string, string[]> byCountry)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (country, forms) in byCountry)
        foreach (var form in forms)
            names[form] = country;
        return names;
    }

    [GeneratedRegex(@"\p{L}+(?:\.\p{L}+)*\.?")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<!\p{L})US(?!\p{L})")]
    private static partial Regex UnitedStatesAbbreviation();
}
