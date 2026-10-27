using System.Text;

namespace GwsBusinessSuite.Web.Components;

// A list's rows as CSV for the bulk bar's Export (tech-forward T3). Fields are quoted when they
// hold a comma, quote or line break; a field starting with =, +, - or @ is prefixed with ' so a
// spreadsheet opens it as text rather than running it as a formula.
public static class CsvExport
{
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', headers.Select(Field)));
        foreach (var row in rows) csv.AppendLine(string.Join(',', row.Select(Field)));
        return csv.ToString();
    }

    private static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }
}
