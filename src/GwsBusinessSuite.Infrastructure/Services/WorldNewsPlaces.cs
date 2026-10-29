using System.Text.RegularExpressions;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record WorldNewsCountry(string Code, string Name, double Latitude, double Longitude);
public sealed record WorldNewsCity(string Name, string CountryCode, double Latitude, double Longitude, int Population);

public static class WorldNewsPrecision
{
    public const string City = "city";
    public const string Country = "country";
    // Pinned by hand on World News ("Wrong place?").
    public const string Corrected = "corrected";
}

// Where a story is placed and how sure that is. City is null at country level.
public sealed record WorldNewsLocation(string CountryCode, string Country, string? City, double Latitude, double Longitude,
    string Precision)
{
    public string Label => City is null ? Country : $"{City}, {Country}";

    public string PlacementText => Precision switch
    {
        WorldNewsPrecision.City => "city-level placement",
        WorldNewsPrecision.Corrected => "placed by hand on World News",
        _ => "country-level placement"
    };
}

// The countries and cities a headline can name (scripts/build-world-news-places.py builds the
// tables). Matching is by whole words, case-sensitive, because place names are proper nouns: "us"
// isn't the US and "polish" isn't Poland.
public sealed class WorldNewsPlaces
{
    // A city alone places a story only if it's this big and its name isn't shared by another
    // sizeable city abroad (Valencia is in Spain and Venezuela).
    public const int CityAlonePopulation = 1_000_000;
    private const int RivalCityPopulation = 100_000;

    private static readonly Lazy<WorldNewsPlaces> Shared = new(() => new WorldNewsPlaces(
        ReadResource("countries.tsv"), ReadResource("cities.tsv")));

    private static readonly Regex Word = new(@"[\p{L}\p{M}\p{N}]+(?:['’.\-][\p{L}\p{M}\p{N}]+)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Scripts written without spaces between words, matched as plain substrings.
    private static readonly Regex Unspaced = new(@"[぀-ヿ㐀-鿿가-힯฀-๿]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly List<WorldNewsCountry> _countries = [];
    private readonly Dictionary<string, WorldNewsCountry> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldNewsCountry> _countryAliases = new(StringComparer.Ordinal);
    private readonly List<(string Alias, WorldNewsCountry Country)> _unspacedAliases = [];
    private readonly List<WorldNewsCity> _cities = [];
    private readonly Dictionary<string, List<WorldNewsCity>> _cityAliases = new(StringComparer.Ordinal);
    private readonly int _longestAlias;

    public static WorldNewsPlaces Instance => Shared.Value;

    public IReadOnlyList<WorldNewsCountry> Countries => _countries;

    public WorldNewsPlaces(string countriesTsv, string citiesTsv)
    {
        foreach (var fields in Rows(countriesTsv))
        {
            var country = new WorldNewsCountry(fields[0], fields[1], double.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture));
            _countries.Add(country);
            _byCode[country.Code] = country;
            // The table leaves out names too ambiguous to place on their own (Georgia, Jordan).
            foreach (var alias in Aliases(fields[4]))
            {
                if (Unspaced.IsMatch(alias)) _unspacedAliases.Add((alias, country));
                else _countryAliases.TryAdd(Key(alias), country);
            }
        }
        _countries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

        foreach (var fields in Rows(citiesTsv))
        {
            var city = new WorldNewsCity(fields[0], fields[1], double.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture), int.Parse(fields[4], System.Globalization.CultureInfo.InvariantCulture));
            _cities.Add(city);
            foreach (var key in Aliases(fields[5]).Select(Key).Distinct(StringComparer.Ordinal))
            {
                if (!_cityAliases.TryGetValue(key, out var list)) _cityAliases[key] = list = [];
                list.Add(city);
            }
        }
        _longestAlias = _countryAliases.Keys.Concat(_cityAliases.Keys).Max(key => key.Count(c => c == ' ') + 1);
    }

    public WorldNewsCountry? Country(string? code) => code is not null && _byCode.TryGetValue(code, out var c) ? c : null;

    // The country a name, code or demonym refers to ("Spain", "ES", "España", "Spanish").
    public WorldNewsCountry? FindCountry(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return Country(text) ?? (_countryAliases.TryGetValue(Key(text), out var byName) ? byName
            : _countries.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.OrdinalIgnoreCase)));
    }

    // The biggest city of that name in the country, matched ignoring case.
    public WorldNewsCity? FindCity(string countryCode, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Key(name.Trim());
        return _cityAliases
            .Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            .SelectMany(pair => pair.Value)
            .Where(city => string.Equals(city.CountryCode, countryCode, StringComparison.OrdinalIgnoreCase))
            .MaxBy(city => city.Population);
    }

    public WorldNewsLocation At(WorldNewsCountry country, WorldNewsCity? city, string precision) =>
        new(country.Code, country.Name, city?.Name, city?.Latitude ?? country.Latitude, city?.Longitude ?? country.Longitude, precision);

    // The place a piece of text names, or null. The longest country mention wins ("South
    // African" over a passing "US"); a city then sharpens it if one in that country is named too.
    // With no country, a big city named alone places the story.
    public WorldNewsLocation? Locate(string text, string? detail = null)
    {
        var mentions = Mentions(text);
        var country = mentions.Countries.OrderByDescending(m => m.Length).ThenBy(m => m.Position).Select(m => m.Country).FirstOrDefault()
            ?? _unspacedAliases.Where(a => text.Contains(a.Alias, StringComparison.Ordinal)).OrderByDescending(a => a.Alias.Length)
                .Select(a => a.Country).FirstOrDefault();
        if (country is not null)
        {
            var cities = detail is null ? mentions.Cities : mentions.Cities.Concat(Mentions(detail).Cities);
            var city = cities.Where(c => c.CountryCode == country.Code).MaxBy(c => c.Population);
            return At(country, city, city is null ? WorldNewsPrecision.Country : WorldNewsPrecision.City);
        }

        var alone = mentions.Keys
            .Where(key => _cityAliases[key].Any(c => c.Population >= CityAlonePopulation))
            .Where(key => _cityAliases[key].Where(c => c.Population >= RivalCityPopulation).Select(c => c.CountryCode).Distinct().Count() == 1)
            .Select(key => _cityAliases[key].MaxBy(c => c.Population)!)
            .MaxBy(c => c.Population);
        return alone is not null && Country(alone.CountryCode) is { } home ? At(home, alone, WorldNewsPrecision.City) : null;
    }

    private sealed record Mention(WorldNewsCountry Country, int Length, int Position);

    private (List<Mention> Countries, List<WorldNewsCity> Cities, List<string> Keys) Mentions(string text)
    {
        var words = Word.Matches(text).Select(m => m.Value).ToList();
        var countries = new List<Mention>();
        var cities = new List<WorldNewsCity>();
        var keys = new List<string>();
        for (var start = 0; start < words.Count; start++)
        {
            for (var length = 1; length <= _longestAlias && start + length <= words.Count; length++)
            {
                foreach (var key in Candidates(words, start, length))
                {
                    // "New Mexico" and "New Jersey" aren't Mexico and Jersey.
                    if (start > 0 && words[start - 1] == "New" && !key.StartsWith("New ", StringComparison.Ordinal)) continue;
                    if (_countryAliases.TryGetValue(key, out var country)) countries.Add(new Mention(country, key.Length, start));
                    if (_cityAliases.TryGetValue(key, out var list)) { cities.AddRange(list); keys.Add(key); }
                }
            }
        }
        return (countries, cities, keys);
    }

    // A run of words as lookup keys: possessives dropped ("Spain's"), and a single hyphenated word
    // also tried piece by piece ("Spanish-led").
    private static IEnumerable<string> Candidates(List<string> words, int start, int length)
    {
        var run = words.GetRange(start, length).Select(StripPossessive).ToList();
        yield return string.Join(' ', run);
        if (length == 1 && run[0].Contains('-'))
            foreach (var piece in run[0].Split('-', StringSplitOptions.RemoveEmptyEntries)) yield return piece;
    }

    private static string StripPossessive(string word) =>
        word.EndsWith("'s", StringComparison.Ordinal) || word.EndsWith("’s", StringComparison.Ordinal) ? word[..^2] : word;

    private static string Key(string alias) => string.Join(' ', Word.Matches(alias).Select(m => m.Value));

    private static IEnumerable<string> Aliases(string field) =>
        field.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string[]> Rows(string tsv) => tsv.Split('\n')
        .Where(line => line.Length > 0 && line[0] != '#')
        .Select(line => line.TrimEnd('\r').Split('\t'));

    private static string ReadResource(string name)
    {
        using var stream = typeof(WorldNewsPlaces).Assembly.GetManifestResourceStream($"GwsBusinessSuite.Infrastructure.WorldNewsPlaces.{name}")
            ?? throw new InvalidOperationException($"World News place table {name} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
