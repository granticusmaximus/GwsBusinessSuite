using System.Globalization;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// One figure a report gives: "at least 64 dead" is (deaths, 64, AtLeast). Person is set for an
// age ("Stephen Hubbard, 72"), which is only compared with the same person's age elsewhere.
public sealed record ClaimFigure(string Measure, long Value, ClaimQualifier Qualifier, string? Person = null, string Excerpt = "");

// A report that gives a value, and the sentence it's in, so a reader can see the context (a
// description can mention last year's crash too).
public sealed record ClaimSource(WorldArticle Article, string Excerpt);

public enum ClaimQualifier { Exact, AtLeast, About, UpTo }

// One value of a measure and every report that gave it (one per outlet).
public sealed record ClaimValue(long Value, ClaimQualifier Qualifier, IReadOnlyList<ClaimSource> Sources)
{
    public DateTimeOffset FirstSeen => Sources.Min(s => s.Article.SeenAt);
    public DateTimeOffset LastSeen => Sources.Max(s => s.Article.SeenAt);
    public int Outlets => Sources.Count;
}

// A measure the coverage reports. Disputed when two values can't both be true, and Corroborated
// when two such values each have 2+ outlets behind them (a one-outlet outlier is more often a
// misreading or another event than a real disagreement). MayHaveRisen when every lower figure came
// before the highest one: a toll being updated rather than outlets disagreeing.
public sealed record CoverageClaim(string Measure, string Label, IReadOnlyList<ClaimValue> Values, bool Disputed, bool Corroborated,
    bool MayHaveRisen)
{
    public bool Consensus => !Disputed && Values.Count == 1 && Values[0].Outlets >= 2;
}

// "Coverage disagrees" without a language model: the 28-10-26 quality gate found the server's
// model attributing figures to the wrong outlets, so figures are read straight from each report's
// own headline and description with plain patterns, in English and the main European languages.
// A figure is only ever shown with the reports that actually contain it.
public static partial class CoverageClaims
{
    public const string Deaths = "deaths";
    public const string Injured = "injured";
    public const string Arrested = "arrested";
    public const string Missing = "missing";
    public const string Evacuated = "evacuated";
    public const string Cases = "cases";

    // The words that say what a number counts, by measure, in en/es/fr/de/it/pt.
    private static readonly (string Measure, string Pattern)[] Measures =
    [
        (Deaths, @"dead|deaths?|killed|died|fatalities|lives|bodies|muertos|fallecidos|víctimas mortales|morts|tués|décès|tote|toten|todesopfer|morti|vittime|mortos|mortes"),
        (Injured, @"injured|injuries|wounded|hurt|herid[oa]s|lesionad[oa]s|blessée?s|verletzte|verletzten|ferit[ie]|ferid[oa]s"),
        (Arrested, @"arrested|arrests|detained|detentions|in custody|detenid[oa]s|arrestad[oa]s|interpellations?|interpellée?s|arrêtée?s|arrestations|festnahmen|festgenommen|arrestat[ie]|fermat[ie]|pres[oa]s|detid[oa]s"),
        (Missing, @"missing|unaccounted for|desaparecid[oa]s|disparue?s|vermisste|vermissten|dispers[ie]"),
        (Evacuated, @"evacuated|evacuees|evacuad[oa]s|évacuée?s|evakuiert|evacuat[ie]"),
        (Cases, @"cases|infections|casos|cas confirmés|fälle|casi")
    ];

    public static string Label(string measure) => measure switch
    {
        Deaths => "Deaths",
        Injured => "Injured",
        Arrested => "Arrests",
        Missing => "Missing",
        Evacuated => "Evacuated",
        Cases => "Cases",
        _ when measure.StartsWith("age:", StringComparison.Ordinal) => $"Age of {measure[4..]}",
        _ => measure
    };

    // Every figure in one report's headline and description.
    public static IReadOnlyList<ClaimFigure> Extract(string title, string? snippet)
    {
        var text = $"{title}. {snippet}";
        var figures = new List<ClaimFigure>();
        foreach (var (measure, numberThenWord, wordThenNumber) in Patterns)
        {
            // "Cases" counts infections: in a court story it's something else.
            if (measure == Cases && !Illness().IsMatch(text)) continue;
            // "64 dead", "at least 64 people were killed", "1.300 Festnahmen", then "death toll
            // rises to 64", "killing 64", "deaths reach 64"
            foreach (var match in numberThenWord.Matches(text).Concat(wordThenNumber.Matches(text)))
                if (Number(match.Groups["n"].Value) is { } n)
                    figures.Add(new ClaimFigure(measure, n, Qualifier(match.Groups["q"].Value), Excerpt: Sentence(text, match.Index)));
        }
        foreach (Match m in AgeAfterName().Matches(text))
            if (Number(m.Groups["age"].Value) is { } age and > 0 and < 120)
                figures.Add(new ClaimFigure($"age:{m.Groups["name"].Value}", age, ClaimQualifier.Exact, m.Groups["name"].Value, Sentence(text, m.Index)));
        foreach (Match m in AgeBeforeName().Matches(text))
            if (Number(m.Groups["age"].Value) is { } age and > 0 and < 120)
                figures.Add(new ClaimFigure($"age:{m.Groups["name"].Value}", age, ClaimQualifier.Exact, m.Groups["name"].Value, Sentence(text, m.Index)));
        // One figure per measure per report: a report that updates itself ("from 41 to 64") keeps
        // its highest.
        return figures.GroupBy(f => f.Measure).Select(g => g.MaxBy(f => f.Value)!).ToList();
    }

    // The measures the reports give, with each value's sources; only measures reported by 2+
    // outlets, since one outlet's figure can't agree or disagree with anything.
    public static IReadOnlyList<CoverageClaim> For(IReadOnlyList<WorldArticle> articles)
    {
        var figures = articles
            .SelectMany(a => Extract(a.Title, a.Snippet).Select(f => (Figure: f, Article: a)))
            .ToList();
        var claims = new List<CoverageClaim>();
        foreach (var measure in figures.GroupBy(x => x.Figure.Measure))
        {
            var values = measure
                .GroupBy(x => (x.Figure.Value, x.Figure.Qualifier))
                .Select(g => new ClaimValue(g.Key.Value, g.Key.Qualifier,
                    g.DistinctBy(x => x.Article.Outlet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Article.SeenAt)
                        .Select(x => new ClaimSource(x.Article, x.Figure.Excerpt)).ToList()))
                .OrderByDescending(v => v.Value)
                .ToList();
            if (values.SelectMany(v => v.Sources).Select(s => s.Article.Outlet).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            var conflicts = values.SelectMany((a, i) => values.Skip(i + 1).Select(b => (a, b))).Where(pair => Conflict(pair.a, pair.b)).ToList();
            var disputed = conflicts.Count > 0;
            var corroborated = conflicts.Any(pair => pair.a.Outlets >= 2 && pair.b.Outlets >= 2);
            var highest = values.Where(v => v.Qualifier != ClaimQualifier.UpTo).MaxBy(v => v.Value);
            var mayHaveRisen = disputed && !measure.Key.StartsWith("age:", StringComparison.Ordinal) && highest is not null
                && values.Where(v => v.Value < highest.Value).All(v => v.LastSeen <= highest.FirstSeen);
            claims.Add(new CoverageClaim(measure.Key, Label(measure.Key), values, disputed, corroborated, mayHaveRisen));
        }
        // Disagreements first: they're what this is for.
        return claims.OrderByDescending(c => c.Corroborated).ThenByDescending(c => c.Disputed)
            .ThenByDescending(c => c.Values.Sum(v => v.Outlets)).ToList();
    }

    // Whether two values can't both be right. "At least 40" and "64" can; "64" and "41" can't;
    // "about 1,000" and "1,050" can.
    // "10,000 cases" is a round figure, so it doesn't contradict "10,060".
    public static bool Conflict(ClaimValue a, ClaimValue b)
    {
        var (high, low) = a.Value >= b.Value ? (a, b) : (b, a);
        if (high.Value == low.Value) return false;
        if (low.Qualifier == ClaimQualifier.AtLeast) return false;
        if (high.Qualifier == ClaimQualifier.UpTo) return false;
        if (Approximate(high) || Approximate(low)) return high.Value - low.Value > Math.Max(1, high.Value / 10);
        return true;
    }

    private static bool Approximate(ClaimValue value) =>
        value.Qualifier == ClaimQualifier.About || (value.Qualifier == ClaimQualifier.Exact && value.Value >= 100 && value.Value % 100 == 0);

    // The sentence around a match, at most about 200 characters.
    private static string Sentence(string text, int index)
    {
        // A full stop ends a sentence only before a space: "100.000" is one number.
        static bool Ends(string t, int i) => t[i] is '!' or '?' or '|' || (t[i] == '.' && (i + 1 == t.Length || char.IsWhiteSpace(t[i + 1])));
        var start = index;
        while (start > 0 && !Ends(text, start - 1)) start--;
        var end = index;
        while (end < text.Length && !Ends(text, end)) end++;
        var sentence = text[start..end].Trim();
        return sentence.Length <= 200 ? sentence : sentence[..200].TrimEnd() + "…";
    }

    private static ClaimQualifier Qualifier(string words) => words.Trim().ToLowerInvariant() switch
    {
        "" => ClaimQualifier.Exact,
        var q when AtLeastWords().IsMatch(q) => ClaimQualifier.AtLeast,
        var q when UpToWords().IsMatch(q) => ClaimQualifier.UpTo,
        _ => ClaimQualifier.About
    };

    // "64", "1,300", "1.300", "1 300", "2.5 million", "sixty".
    public static long? Number(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (Words.TryGetValue(text, out var word)) return word;
        var scale = 1L;
        if (text.EndsWith(" million", StringComparison.Ordinal) || text.EndsWith(" millones", StringComparison.Ordinal)
            || text.EndsWith(" millions", StringComparison.Ordinal) || text.EndsWith(" millionen", StringComparison.Ordinal))
        {
            scale = 1_000_000;
            text = text[..text.LastIndexOf(' ')];
            if (decimal.TryParse(text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var millions))
                return (long)(millions * scale);
            return null;
        }
        // Thousands separators only: a counted figure is a whole number.
        var digits = ThousandsSeparator().Replace(text, string.Empty);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n * scale : null;
    }

    private static readonly Dictionary<string, long> Words = new(StringComparer.Ordinal)
    {
        ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90, ["hundred"] = 100
    };

    private const string Num = @"(?<n>\d{1,3}(?:[,.  ]\d{3})+|\d+(?:[.,]\d+)?\s(?:million|millions|millones|millionen)|\d+|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)";
    private const string Qual = @"(?<q>at least|more than|over|tops|topped|passes|passed|surpasses|surpassed|exceeds|exceeded|nearly|almost|about|around|some|roughly|near|nears|nearing|approaching|up to|as many as|al menos|más de|unos|au moins|plus de|près de|environ|mindestens|mehr als|rund|etwa|bis zu|almeno|oltre|circa|pelo menos|mais de|cerca de)?";

    // "Oct. 26 arrested three suspects" is a date, not 26 arrests.
    private const string Months = "Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec|January|February|March|April|June|July|August|September|October|November|December";

    // Every measure word, so a number can't reach past one measure to another ("10,000 cases as
    // deaths ...") and joining words that start another clause.
    private static readonly string AnyMeasure = string.Join("|", Measures.Select(m => m.Pattern));
    private static readonly string Between = $@"(?:\s+(?!(?:and|or|as|with|while|but|y|et|und|e|o|ed|als|como|comme)\b)(?!(?:{AnyMeasure})\b)[\p{{L}}-]+){{0,3}}?";

    // Built once: per measure, "number then word" and "word then number".
    private static readonly (string Measure, Regex NumberThenWord, Regex WordThenNumber)[] Patterns = Measures
        .Select(m => (m.Measure, NumberThenWord(m.Pattern), WordThenNumber(m.Measure, m.Pattern)))
        .ToArray();

    // A number, a few words at most ("people were", "passengers"), then the measure word.
    private static Regex NumberThenWord(string words) => new(
        $@"(?<![\w.,])(?<!\b(?:{Months})\.?\s){Qual}\s*{Num}(?![\w]|[.,]\d){Between}\s+(?:{words})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // The measure word ("toll" only for deaths), then "rises to", "reach", "of" and the number;
    // or "killing 64", "injuring 80", "arresting 12".
    private static Regex WordThenNumber(string measure, string words)
    {
        var lead = measure == Deaths ? $"{words}|death toll|toll" : words;
        var verb = measure switch { Deaths => "killing", Injured => "injuring|wounding", Arrested => "arresting", _ => "(?!)" };
        return new Regex(
            $@"\b(?:{lead})\s+(?:(?:has\s+)?(?:rises?|rose|risen|climbs?|climbed|reach(?:es|ed)?|stands?|stood|hits?|is|was|of|to|at|now)\s+){{0,3}}{Qual}\s*{Num}(?![\w]|[.,]\d)|\b(?:{verb})\s+{Qual}\s*{Num}(?![\w]|[.,]\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    [GeneratedRegex(@"outbreak|virus|disease|infection|ebola|cholera|measles|covid|dengue|mpox|flu\b|epidemic|pandemic|brote|épidémie|epidemie|ausbruch|epidemia|surto", RegexOptions.IgnoreCase)]
    private static partial Regex Illness();

    [GeneratedRegex(@"(?<name>\p{Lu}\p{Ll}+(?:\s+\p{Lu}\p{Ll}+){1,2}),\s+(?<age>\d{1,3}),")]
    private static partial Regex AgeAfterName();

    [GeneratedRegex(@"(?<age>\d{1,3})-year-old\s+(?:[\p{Ll}]+\s+){0,2}(?<name>\p{Lu}\p{Ll}+(?:\s+\p{Lu}\p{Ll}+){1,2})")]
    private static partial Regex AgeBeforeName();

    [GeneratedRegex(@"at least|more than|over|tops|topped|passe[sd]|surpasse[sd]|exceed(?:s|ed)?|al menos|más de|au moins|plus de|mindestens|mehr als|almeno|oltre|pelo menos|mais de")]
    private static partial Regex AtLeastWords();

    [GeneratedRegex(@"up to|as many as|bis zu")]
    private static partial Regex UpToWords();

    [GeneratedRegex(@"(?<=\d)[,.  ](?=\d{3}(?!\d))")]
    private static partial Regex ThousandsSeparator();
}
