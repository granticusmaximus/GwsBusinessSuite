using System.Globalization;
using System.Net;
using System.Text;

namespace GwsBusinessSuite.WorldNewsKit;

// The body of the Mac app's event view: who reported it, in which languages and countries, and
// every report grouped by language (largest first), each linking out to the outlet.
public static class WorldNewsHtml
{
    public static string Meta(EventOverview e, TimeZoneInfo zone) =>
        $"{Count(e.OutletCount, "outlet")} · {Count(e.Languages.Count, "language")} · {Count(e.Countries.Count, "country", "countries")} · {Time(e.LastSeen, zone)}";

    public static string EventDetail(EventOverview e, IReadOnlyList<WorldArticle> articles, TimeZoneInfo zone)
    {
        var html = new StringBuilder();
        html.Append("<p style=\"color:#a8a29e\">")
            .Append(Encode($"{Count(e.OutletCount, "outlet")}, {Count(e.ArticleCount, "report")}. First seen {Time(e.FirstSeen, zone)}, latest {Time(e.LastSeen, zone)}."))
            .Append("</p>");
        html.Append("<p><strong>Languages:</strong> ").Append(Encode(string.Join(", ", e.Languages.Order()))).Append("<br>")
            .Append("<strong>Reported from:</strong> ").Append(Encode(string.Join(", ", e.Countries.Order()))).Append("</p>");

        foreach (var language in articles.GroupBy(a => a.Language).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            html.Append("<h3>").Append(Encode($"{language.Key} ({language.Count()})")).Append("</h3><ul>");
            foreach (var a in language.OrderByDescending(a => a.SeenAt))
            {
                html.Append("<li><a href=\"").Append(Encode(a.Url)).Append("\">").Append(Encode(a.Title)).Append("</a>")
                    .Append("<br><span style=\"color:#a8a29e;font-size:13px\">")
                    .Append(Encode($"{a.Outlet} · {a.Country} · {Time(a.SeenAt, zone)}")).Append("</span></li>");
            }
            html.Append("</ul>");
        }
        return html.ToString();
    }

    private static string Count(int n, string one, string? many = null) =>
        $"{n.ToString("N0", CultureInfo.InvariantCulture)} {(n == 1 ? one : many ?? one + "s")}";

    // dd-MM-yy HH:mm, the app's one date format.
    private static string Time(DateTimeOffset when, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(when, zone).ToString("dd-MM-yy HH:mm", CultureInfo.InvariantCulture);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
