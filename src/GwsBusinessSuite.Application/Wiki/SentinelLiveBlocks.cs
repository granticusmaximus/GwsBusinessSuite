namespace GwsBusinessSuite.Application.Wiki;

// Live business blocks (Sentinel item 5): a "business" block shows a CRM contact or deal, a
// support ticket, a BI dashboard chart, this week's content calendar or this week's time totals,
// re-read from its own service every time the editor refreshes it. The block itself stores only
// the kind, the record id and a label (props kind / recordId / recordLabel) - never the data, so
// a public share or an export has nothing to leak and shows only that a live block was there.
// The source areas are all Admin-only, so only Admins see the data.
public static class SentinelLiveBlockKinds
{
    public const string Contact = "contact";
    public const string Deal = "deal";
    public const string Ticket = "ticket";
    public const string Chart = "chart";
    public const string Calendar = "calendar";
    public const string Time = "time";

    public static readonly IReadOnlyList<string> All = [Contact, Deal, Ticket, Chart, Calendar, Time];

    // Calendar and Time are about the current week, so there is no record to pick.
    public static bool NeedsRecord(string kind) => kind is Contact or Deal or Ticket or Chart;

    public static string Name(string kind) => kind switch
    {
        Contact => "CRM contact",
        Deal => "Deal",
        Ticket => "Support ticket",
        Chart => "BI chart",
        Calendar => "Content calendar",
        Time => "Time tracking",
        _ => "Business data"
    };

    // One line for previews, Markdown and exports: "Live CRM contact: Sam Client".
    public static string Describe(WikiBlock block)
    {
        var kind = block.Props.GetValueOrDefault("kind") ?? string.Empty;
        var label = block.Props.GetValueOrDefault("recordLabel")?.Trim();
        return NeedsRecord(kind)
            ? $"Live {Name(kind)}: {(string.IsNullOrEmpty(label) ? "not chosen" : label)}"
            : $"Live {Name(kind)} (this week)";
    }
}

public sealed record SentinelLiveTarget(string Id, string Label, string Detail);

public sealed record SentinelLiveField(string Label, string Value);

// A labelled bar (chart points, hours per contact); Share is 0-1 of the largest value.
public sealed record SentinelLiveBar(string Label, string Value, double Share);

// A dated line (calendar items): Day "Mon 12-10", Text, optional Status and Link.
public sealed record SentinelLiveRow(string Day, string Text, string? Status, string? Link);

public sealed record SentinelLiveCard(
    string Kind,
    string Title,
    string? Subtitle,
    string? Link,
    IReadOnlyList<SentinelLiveField> Fields,
    IReadOnlyList<SentinelLiveBar> Bars,
    IReadOnlyList<SentinelLiveRow> Rows,
    string AsOf,
    string? Error = null)
{
    public static SentinelLiveCard Unavailable(string kind, string error) =>
        new(kind, SentinelLiveBlockKinds.Name(kind), null, null, [], [], [], string.Empty, error);
}

public interface ISentinelLiveBlockService
{
    // Records the viewer can embed for a kind that needs one, best matches first (max 12).
    Task<IReadOnlyList<SentinelLiveTarget>> SearchAsync(string kind, string? query, string username, CancellationToken cancellationToken = default);

    // The card as it is right now. A missing record or unknown kind comes back with Error set.
    Task<SentinelLiveCard> GetCardAsync(string kind, string? recordId, string username, CancellationToken cancellationToken = default);
}
