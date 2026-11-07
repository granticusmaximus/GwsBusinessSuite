namespace GwsBusinessSuite.WorldNewsKit;

// Where an outlet is based, by region, to compare how parts of the world cover a story (06-11-26).
// "Western" means Europe, North America, Australia and New Zealand: the press most English-language
// readers already see. Countries are the English names the feeds use (WorldArticle.Country).
public static class WorldRegions
{
    public const string Europe = "Europe";
    public const string RussiaAndNeighbours = "Russia & neighbours";
    public const string NorthAmerica = "North America";
    public const string LatinAmerica = "Latin America";
    public const string MiddleEast = "Middle East & North Africa";
    public const string Africa = "Sub-Saharan Africa";
    public const string Asia = "Asia";
    public const string Oceania = "Oceania";
    public const string Other = "Other";

    // The order regions are offered in.
    public static readonly IReadOnlyList<string> All =
        [Europe, NorthAmerica, LatinAmerica, RussiaAndNeighbours, MiddleEast, Africa, Asia, Oceania, Other];

    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Austria"] = Europe, ["Belgium"] = Europe, ["Czechia"] = Europe, ["Denmark"] = Europe, ["Finland"] = Europe,
        ["France"] = Europe, ["Germany"] = Europe, ["Greece"] = Europe, ["Hungary"] = Europe, ["Ireland"] = Europe,
        ["Italy"] = Europe, ["Latvia"] = Europe, ["Lithuania"] = Europe, ["Estonia"] = Europe, ["Netherlands"] = Europe,
        ["Norway"] = Europe, ["Poland"] = Europe, ["Portugal"] = Europe, ["Romania"] = Europe, ["Spain"] = Europe,
        ["Sweden"] = Europe, ["Switzerland"] = Europe, ["United Kingdom"] = Europe, ["Serbia"] = Europe, ["Bulgaria"] = Europe,
        ["Russia"] = RussiaAndNeighbours, ["Ukraine"] = RussiaAndNeighbours, ["Belarus"] = RussiaAndNeighbours,
        ["Georgia"] = RussiaAndNeighbours, ["Kazakhstan"] = RussiaAndNeighbours,
        ["United States"] = NorthAmerica, ["Canada"] = NorthAmerica,
        ["Mexico"] = LatinAmerica, ["Argentina"] = LatinAmerica, ["Brazil"] = LatinAmerica, ["Colombia"] = LatinAmerica,
        ["Chile"] = LatinAmerica, ["Peru"] = LatinAmerica, ["Venezuela"] = LatinAmerica, ["Cuba"] = LatinAmerica,
        ["Egypt"] = MiddleEast, ["Israel"] = MiddleEast, ["Qatar"] = MiddleEast, ["Saudi Arabia"] = MiddleEast,
        ["United Arab Emirates"] = MiddleEast, ["Turkey"] = MiddleEast, ["Iran"] = MiddleEast, ["Iraq"] = MiddleEast,
        ["Lebanon"] = MiddleEast, ["Jordan"] = MiddleEast, ["Morocco"] = MiddleEast, ["Algeria"] = MiddleEast, ["Tunisia"] = MiddleEast,
        ["Kenya"] = Africa, ["Nigeria"] = Africa, ["South Africa"] = Africa, ["Ghana"] = Africa, ["Ethiopia"] = Africa,
        ["Republic of the Congo"] = Africa, ["Democratic Republic of the Congo"] = Africa,
        ["China"] = Asia, ["Hong Kong"] = Asia, ["India"] = Asia, ["Indonesia"] = Asia, ["Japan"] = Asia, ["Pakistan"] = Asia,
        ["Philippines"] = Asia, ["Singapore"] = Asia, ["South Korea"] = Asia, ["Taiwan"] = Asia, ["Thailand"] = Asia,
        ["Vietnam"] = Asia, ["Bangladesh"] = Asia, ["Malaysia"] = Asia,
        ["Australia"] = Oceania, ["New Zealand"] = Oceania
    };

    public static string Of(string? country) => country is not null && ByCountry.TryGetValue(country.Trim(), out var region) ? region : Other;

    public static bool IsWestern(string? country) => Of(country) is Europe or NorthAmerica or Oceania;
}
