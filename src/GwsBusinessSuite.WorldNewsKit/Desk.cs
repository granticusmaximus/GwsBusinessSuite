using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GwsBusinessSuite.WorldNewsKit;

// World News trust plan, Phase B newsroom N5-N15 (14-11-26): the desk's working tools. Models and
// rules only; WorldNewsStore.Desk.cs keeps them, the pages show them.

// N5: a beat: a standing area of responsibility. Kind is "region" (a WorldRegions name) or
// "topic" (a SituationKinds kind). New stories on a beat go to that person's queue and bell.
public sealed record Beat(string Id, string Username, string Kind, string Value, DateTimeOffset CreatedAt);

public static class BeatRules
{
    public const string Region = "region";
    public const string Topic = "topic";
    public const int MaxPerPerson = 20;
    // At most this many beat alerts a person gets from one read, the most covered stories first.
    public const int MaxAlertsPerRead = 5;

    public static bool Matches(Beat beat, IEnumerable<string> countries, string? kind) => beat.Kind switch
    {
        Region => countries.Any(country => string.Equals(WorldRegions.Of(country), beat.Value, StringComparison.OrdinalIgnoreCase)),
        Topic => string.Equals(kind, beat.Value, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public static string? Problem(string kind, string value) => kind switch
    {
        Region when !WorldRegions.All.Contains(value, StringComparer.OrdinalIgnoreCase) => "Pick a region from the list.",
        Topic when !SituationKinds.All.Contains(value, StringComparer.OrdinalIgnoreCase) => "Pick a kind of story from the list.",
        Region or Topic => null,
        _ => "A beat is a region or a kind of story.",
    };

    public static string Label(Beat beat) => beat.Kind == Topic ? SituationKinds.Label(beat.Value) : beat.Value;
}

// N7: a private note on a story, for the desk only. @name in the text notifies that person.
public sealed record DeskNote(string Id, string EventId, string Username, string Text, DateTimeOffset CreatedAt);

public static partial class DeskNotes
{
    public const int MaxLength = 4000;

    public static string? Problem(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Write a desk note first."
        : text.Trim().Length > MaxLength ? $"Keep a desk note to {MaxLength} characters or fewer." : null;

    public static IReadOnlyList<string> Mentions(string text) =>
        Mention().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex(@"(?<![\w@])@([A-Za-z0-9][A-Za-z0-9._-]{1,63})")]
    private static partial Regex Mention();
}

// N8: the source book: a person's own contacts, private to them. Reliability is 1 (unproven) to 5
// (always right). Uses record which stories a contact was used on and whether what they said held.
public sealed record SourceContact(string Id, string Owner, string Name, string Organisation, string Contact, int Reliability, string Notes, DateTimeOffset CreatedAt);

public sealed record SourceUse(string SourceId, string EventId, string Outcome, string Note, DateTimeOffset At);

public static class SourceBook
{
    public static readonly IReadOnlyList<string> Outcomes = ["unknown", "held", "wrong"];

    // Past accuracy: of the uses with a known outcome, how many held. Null until there's one.
    public static double? Accuracy(IEnumerable<SourceUse> uses)
    {
        var known = uses.Where(use => use.Outcome is "held" or "wrong").ToList();
        return Accuracy(known.Count(use => use.Outcome == "held"), known.Count(use => use.Outcome == "wrong"));
    }

    public static double? Accuracy(int held, int wrong) => held + wrong == 0 ? null : (double)held / (held + wrong);

    public static string? Problem(string name, int reliability) =>
        string.IsNullOrWhiteSpace(name) ? "A contact needs a name." : reliability is < 1 or > 5 ? "Reliability is 1 to 5." : null;
}

// N9: two-person sign-off. A verdict (the desk's checklist complete) or a publication is signed by
// two different people; each signature is chained to the one before, like the label ledger, so a
// signature can't be rewritten without breaking the chain.
public sealed record SignOff(long Id, string EventId, string Kind, string Username, string LabelLevel, DateTimeOffset At, string PreviousHash, string Hash);

public sealed record DeskApprovals(IReadOnlyList<SignOff> Records, bool ChecklistComplete, string LabelLevel);

public static class SignOffs
{
    public const string Verdict = "verdict";
    public const string Publish = "publish";
    public const int Needed = 2;

    public static bool IsComplete(IEnumerable<SignOff> signOffs, string kind) =>
        signOffs.Where(s => s.Kind == kind).Select(s => s.Username).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= Needed;

    // Null when this person may sign now; otherwise why not.
    public static string? Problem(IEnumerable<SignOff> existing, string kind, string username, bool checklistComplete)
    {
        if (kind is not (Verdict or Publish)) return "Choose a verdict or publication approval.";
        if (string.IsNullOrWhiteSpace(username)) return "Sign in before recording an approval.";
        if (!checklistComplete) return "Finish the verification checklist first.";
        if (kind == Publish && !IsComplete(existing, Verdict)) return "The verdict needs two sign-offs before publication can be signed.";
        if (existing.Any(s => s.Kind == kind && string.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase))) return "You've already signed this; a second person must sign.";
        return null;
    }

    public static string HashOf(string previous, string eventId, string kind, string username, string labelLevel, DateTimeOffset at) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{previous}|{eventId}|{kind}|{username}|{labelLevel}|{at.ToUnixTimeSeconds()}"))).ToLowerInvariant();

    public static long? FirstBroken(IEnumerable<SignOff> inOrder)
    {
        var previous = LabelLedger.Genesis;
        foreach (var s in inOrder)
        {
            if (s.PreviousHash != previous || s.Hash != HashOf(s.PreviousHash, s.EventId, s.Kind, s.Username, s.LabelLevel, s.At)) return s.Id;
            previous = s.Hash;
        }
        return null;
    }
}

// N10: a live-blog update on a story, shown on the public live page as it's posted. Each is tagged
// confirmed or unconfirmed; a correction is shown in place, with the original kept.
public sealed record LiveUpdate(string Id, string EventId, string Username, string Text, string Status, DateTimeOffset CreatedAt, string? CorrectedText, DateTimeOffset? CorrectedAt);

public sealed record PublicLiveUpdate(string Id, string Text, string Status, DateTimeOffset CreatedAt, string? CorrectedText, DateTimeOffset? CorrectedAt);
public sealed record PublicLiveStory(string EventId, string Title, DateTimeOffset LastAt, int Updates);

public static class LiveUpdates
{
    public const string Confirmed = "confirmed";
    public const string Unconfirmed = "unconfirmed";
    public const int MaxLength = 2000;
    public const int PageSize = 25;

    public static string? Problem(string text, string status) =>
        string.IsNullOrWhiteSpace(text) ? "Write the update first."
        : text.Length > MaxLength ? $"Keep an update under {MaxLength} characters."
        : status is not (Confirmed or Unconfirmed) ? "An update is confirmed or unconfirmed." : null;
}

// N11: embargo and hold. A story on hold sends no alerts and can't be drafted or posted live until
// its release time, and the reason is shown wherever it's held.
public sealed record StoryHold(string EventId, DateTimeOffset Until, string Reason, string SetBy, DateTimeOffset SetAt);

public static class Holds
{
    public const int MaxReasonLength = 1000;

    public static bool IsHeld(StoryHold? hold, DateTimeOffset now) => hold is not null && hold.Until > now;

    public static string Describe(StoryHold hold, Func<DateTimeOffset, string> formatTime) =>
        $"On hold until {formatTime(hold.Until)}: {hold.Reason} ({hold.SetBy})";

    public static string? Problem(DateTimeOffset until, string reason, DateTimeOffset now) =>
        until.ToUnixTimeSeconds() <= now.ToUnixTimeSeconds() ? "The release time has to be in the future."
        : string.IsNullOrWhiteSpace(reason) ? "Give the reason for the hold."
        : reason.Trim().Length > MaxReasonLength ? $"Keep the hold reason to {MaxReasonLength} characters or fewer." : null;
}

// N12: a published correction to a story, shown on the story, on the public corrections page, and
// alongside the methodology log.
public sealed record Correction(string Id, string EventId, string Title, string Text, string Username, DateTimeOffset At)
{
    public Guid? ArticleId { get; init; }
}

public sealed record PublicCorrection(string Id, string EventId, string Title, string Text, DateTimeOffset At);

public static class Corrections
{
    public const int MaxLength = 4000;
    public const int MaxTitleLength = 200;
    public const int PageSize = 25;

    public static string? Problem(string title, string text) =>
        string.IsNullOrWhiteSpace(title) ? "Give the correction a public title."
        : title.Trim().Length > MaxTitleLength ? $"Keep the title to {MaxTitleLength} characters or fewer."
        : string.IsNullOrWhiteSpace(text) ? "Explain what was wrong and what is correct."
        : text.Trim().Length > MaxLength ? $"Keep the correction to {MaxLength} characters or fewer." : null;
}

// N14: a dossier: what the desk pinned on a story (reports, official records, notes, links), kept
// with who pinned it, and exported as a PDF.
public sealed record DossierItem(string Id, string EventId, string Kind, string Title, string? Url, string Detail, string AddedBy, DateTimeOffset At);

public static class Dossiers
{
    public const string Report = "report";
    public const string Record = "record";
    public const string Note = "note";
    public const string Link = "link";
    public static readonly IReadOnlyList<string> Kinds = [Report, Record, Note, Link];
    public const int MaxItems = 200;
    public const int MaxTitleLength = 200;
    public const int MaxDetailLength = 4000;
    public const int MaxUrlLength = 2048;

    public static string? Problem(string kind, string title, string? url, string detail) =>
        !Kinds.Contains(kind) ? "Choose a dossier item type."
        : string.IsNullOrWhiteSpace(title) ? "Give the dossier item a title."
        : title.Trim().Length > MaxTitleLength ? $"Keep the title to {MaxTitleLength} characters or fewer."
        : detail.Length > MaxDetailLength ? $"Keep the detail to {MaxDetailLength} characters or fewer."
        : url is { Length: > MaxUrlLength } ? $"Keep the link to {MaxUrlLength} characters or fewer."
        : string.IsNullOrWhiteSpace(url) ? null
        : !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var link) || link.Scheme is not ("http" or "https")
            ? "Use an absolute HTTP or HTTPS link, or leave the link empty."
            : null;
}

// N13: the morgue: one search over the desk's own records (notes, budget, live updates,
// corrections, dossiers, the person's own contacts), beside the story search.
public sealed record MorgueHit(string Kind, string EventId, string Title, string Text, string? Username, DateTimeOffset At);

public static class MorgueSearch
{
    public const int MaxQueryLength = 200;
    public const int MaxResults = 60;
}

// N15: desk metrics.
public sealed record DeskMetrics(
    int OnBudget,
    IReadOnlyList<(string Owner, int Open)> OpenPerPerson,
    TimeSpan? MedianTimeToVerdict,
    int VerdictsSigned,
    int VerdictsHeld,
    int Tips,
    int TipsBecameStories,
    int LiveUpdates,
    int CorrectionsPublished)
{
    public int? VerdictsHeldPercent => VerdictsSigned == 0 ? null : (int)Math.Round(100.0 * VerdictsHeld / VerdictsSigned);
}

public static class DeskMetricsRules
{
    private static int Rank(string level) => level switch { "Confirmed" => 3, "Reported" => 2, "SingleSource" => 1, _ => 0 };

    // A desk verdict "held" when the story's label now is at least as strong as when it was signed
    // (Disputed counts as not holding).
    public static bool Held(string levelAtSignOff, string levelNow) => levelNow != "Disputed" && Rank(levelNow) >= Rank(levelAtSignOff);

    public static TimeSpan? Median(IEnumerable<TimeSpan> spans)
    {
        var list = spans.OrderBy(s => s).ToList();
        if (list.Count == 0) return null;
        var middle = list.Count / 2;
        return list.Count % 2 == 1
            ? list[middle]
            : TimeSpan.FromTicks((list[middle - 1].Ticks + list[middle].Ticks) / 2);
    }
}

// N6: the shift handover note: what moved since a time, what's unconfirmed, what's open.
public static class ShiftHandover
{
    public static string Markdown(DateTimeOffset since, DateTimeOffset now,
        IReadOnlyList<BudgetItem> budget,
        IReadOnlyDictionary<string, StoryHold> holds,
        IReadOnlyList<LabelSnapshot> labelChanges,
        IReadOnlyList<LabelChallenge> openChallenges,
        IReadOnlyList<NewsTip> newTips,
        IReadOnlyList<DeskNote> notes,
        IReadOnlyList<LiveUpdate> liveUpdates,
        Func<string, string?> titleOf,
        Func<DateTimeOffset, string> formatTime,
        IReadOnlyDictionary<string, string>? currentBudgetLabels = null)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Handover, {formatTime(since)} to {formatTime(now)}").AppendLine();

        var moved = budget.Where(item => item.UpdatedAt >= since).OrderByDescending(item => item.UpdatedAt).ToList();
        md.AppendLine("## On the budget").AppendLine();
        if (budget.Count == 0) md.AppendLine("Nothing on the budget.");
        foreach (var item in budget.OrderBy(item => BudgetStatuses.All.ToList().IndexOf(item.Status)).ThenByDescending(item => item.UpdatedAt))
        {
            var hold = holds.GetValueOrDefault(item.EventId);
            md.AppendLine($"- **{item.Title}**: {item.Status}, {item.Owner}{(moved.Contains(item) ? $" (moved {formatTime(item.UpdatedAt)})" : string.Empty)}. Next: {item.NextAction}"
                + (Holds.IsHeld(hold, now) ? $". {Holds.Describe(hold!, formatTime)}" : string.Empty));
        }
        md.AppendLine();

        var labelled = labelChanges.Where(s => s.TakenAt >= since).GroupBy(s => s.EventId).ToList();
        if (labelled.Count > 0)
        {
            md.AppendLine("## Labels that changed").AppendLine();
            foreach (var group in labelled)
            {
                var last = group.OrderBy(s => s.Id).Last();
                md.AppendLine($"- {titleOf(group.Key) ?? group.Key}: now **{StoryTimeline.LevelText(last.Level)}** ({last.Reason}), {formatTime(last.TakenAt)}");
            }
            md.AppendLine();
        }

        var unconfirmed = budget.Where(item =>
            (currentBudgetLabels?.GetValueOrDefault(item.EventId)
                ?? labelChanges.Where(s => s.EventId == item.EventId).OrderBy(s => s.Id).LastOrDefault()?.Level) == "SingleSource").ToList();
        if (unconfirmed.Count > 0)
        {
            md.AppendLine("## Still on one source").AppendLine();
            foreach (var item in unconfirmed) md.AppendLine($"- {item.Title} ({item.Owner})");
            md.AppendLine();
        }

        if (openChallenges.Count > 0)
        {
            md.AppendLine("## Open challenges").AppendLine();
            foreach (var c in openChallenges) md.AppendLine($"- {titleOf(c.EventId) ?? c.EventId}: {c.Note} ({c.Username}, {formatTime(c.CreatedAt)})");
            md.AppendLine();
        }

        if (newTips.Count > 0)
        {
            md.AppendLine("## New tips").AppendLine();
            foreach (var tip in newTips) md.AppendLine($"- {formatTime(tip.CreatedAt)}: {(tip.Message.Length > 160 ? tip.Message[..160] + "…" : tip.Message)}");
            md.AppendLine();
        }

        var recentNotes = notes.Where(n => n.CreatedAt >= since).ToList();
        if (recentNotes.Count > 0)
        {
            md.AppendLine("## Desk notes").AppendLine();
            foreach (var n in recentNotes) md.AppendLine($"- {titleOf(n.EventId) ?? n.EventId}: {n.Text} ({n.Username}, {formatTime(n.CreatedAt)})");
            md.AppendLine();
        }

        var posted = liveUpdates.Where(u => u.CreatedAt >= since || u.CorrectedAt >= since).ToList();
        if (posted.Count > 0)
        {
            md.AppendLine("## Posted live").AppendLine();
            foreach (var u in posted)
                md.AppendLine($"- {titleOf(u.EventId) ?? u.EventId}: [{u.Status}] {u.CorrectedText ?? u.Text} ({u.Username}, {formatTime(u.CreatedAt)}"
                    + (u.CorrectedAt is { } correctedAt ? $"; corrected {formatTime(correctedAt)}" : string.Empty) + ")");
            md.AppendLine();
        }
        return md.ToString().TrimEnd() + "\n";
    }
}
