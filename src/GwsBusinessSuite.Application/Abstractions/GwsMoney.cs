using System.Globalization;

namespace GwsBusinessSuite.Application.Abstractions;

// Money and percentages shown the same whatever the server's culture (19-11-26): CI and the
// container run with the invariant culture, where "C0" prints "¤1,200" and "P0" prints "88 %".
public static class GwsMoney
{
    public static string Usd(decimal value) =>
        (value < 0 ? "-$" : "$") + Math.Abs(value).ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double share) =>
        Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Percent(decimal share) => Percent((double)share);
}
