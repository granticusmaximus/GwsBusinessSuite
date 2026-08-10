using System.Text;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

// Sentinel's Notion-parity "export" story (Phase 4.5) - the inverse of NotionMarkdownBlockParser
// on the page side, this covers the database side. One property per column, one row per
// WikiDatabaseRow, using the exact same display-text formatting the read-only public share view
// (SentinelPublicShare.razor) already applies via WikiPropertyValues.GetDisplayText, so what a
// user sees in the app and what lands in the CSV always agree.
public static class WikiCsvExporter
{
    public static string ExportDatabase(WikiDatabase database)
    {
        var properties = database.Properties.OrderBy(property => property.SortOrder).ToList();
        var csv = new StringBuilder();
        csv.AppendJoin(',', properties.Select(property => Escape(property.Name)));
        csv.Append("\r\n");

        foreach (var row in database.Rows.OrderBy(row => row.SortOrder))
        {
            var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
            csv.AppendJoin(
                ',',
                properties.Select(property => Escape(WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt))));
            csv.Append("\r\n");
        }

        return csv.ToString();
    }

    // Same convention as the CSV export endpoints already in Program.cs (Growth Studio, security
    // audit): quote every field and prefix a leading =/+/-/@ with a straight quote to defeat
    // formula injection when the file is opened in Excel/Sheets.
    private static string Escape(string? value)
    {
        var safe = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        var trimmed = safe.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))
        {
            safe = $"'{safe}";
        }

        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }
}
