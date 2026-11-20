using System.Globalization;
using System.Text;

namespace GwsBusinessSuite.Application.GovernmentIntelligence;

// Civic Watch features (20-11-26, Grant: "Build ... 6 features"): a calendar subscription, items
// followed across meetings, the school board desk, a weekly page in Sentinel, representative
// scorecards and a public comment helper.

/// <summary>1. One date on the civic calendar.</summary>
public sealed record CivicCalendarItem(string Uid, DateOnly Date, string Title, string? Url, string? Description);

public static class CivicCalendar
{
    // An all-day iCalendar (RFC 5545) feed: lines folded at 75 octets, text escaped, CRLF endings.
    public static string Build(IEnumerable<CivicCalendarItem> items, DateTimeOffset now)
    {
        var ics = new StringBuilder();
        void Line(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= 75) { ics.Append(text).Append("\r\n"); return; }
            // Fold on character boundaries, at most 75 octets per physical line.
            var current = new StringBuilder();
            var size = 0;
            var first = true;
            foreach (var rune in text.EnumerateRunes())
            {
                var length = rune.Utf8SequenceLength;
                var limit = first ? 75 : 74;
                if (size + length > limit)
                {
                    ics.Append(first ? string.Empty : " ").Append(current).Append("\r\n");
                    current.Clear();
                    size = 0;
                    first = false;
                }
                current.Append(rune.ToString());
                size += length;
            }
            ics.Append(first ? string.Empty : " ").Append(current).Append("\r\n");
        }

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//GWS Business Suite//Civic Watch//EN");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:Civic Watch");
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var item in items.GroupBy(i => i.Uid).Select(g => g.First()).OrderBy(i => i.Date))
        {
            Line("BEGIN:VEVENT");
            Line($"UID:{Escape(item.Uid)}@civic-watch.gws");
            Line($"DTSTAMP:{stamp}");
            Line($"DTSTART;VALUE=DATE:{item.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
            Line($"DTEND;VALUE=DATE:{item.Date.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
            Line($"SUMMARY:{Escape(item.Title)}");
            if (!string.IsNullOrWhiteSpace(item.Description)) Line($"DESCRIPTION:{Escape(item.Description!)}");
            if (Uri.TryCreate(item.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") Line($"URL:{url.AbsoluteUri}");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return ics.ToString();
    }

    public static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", string.Empty);

    // Unguessable, URL-safe: the calendar link is the only key a calendar app can carry.
    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

/// <summary>2. A phrase followed across the county's meeting papers.</summary>
public sealed record CivicFollowedItem(string Phrase, IReadOnlyList<CivicItemMention> Mentions);

public sealed record CivicItemMention(DateOnly MeetingDate, string Kind, string Title, string Url, string Snippet);

public static class CivicFollowing
{
    public const int MaxPhrases = 10;

    public static IReadOnlyList<string> Phrases(string? text) => CivicMentions.Split(text).Take(MaxPhrases).ToList();
}

/// <summary>3. A Houston County Board of Education meeting from its public listing.</summary>
public sealed record SchoolBoardMeeting(DateTimeOffset StartsAt, string Title, string Url, bool MinutesPublished);

public sealed record SchoolBoardDesk(IReadOnlyList<SchoolBoardMeeting> Upcoming, IReadOnlyList<SchoolBoardMeeting> Recent, string? Error);

/// <summary>5. How a representative votes, from the roll calls Civic Watch has kept.</summary>
public sealed record CivicScorecard(string MemberId, string Name, string Chamber, int RollCalls, int Missed, int? WithParty, int? PartyLineVotes,
    DateTimeOffset? Since)
{
    public double? MissedShare => RollCalls == 0 ? null : (double)Missed / RollCalls;
    public double? WithPartyShare => PartyLineVotes is > 0 && WithParty is { } with ? (double)with / PartyLineVotes.Value : null;
}

public sealed record CivicMemberVoteFact(string MemberId, string RollCallKey, string Vote, string? PartyMajority, DateTimeOffset? VotedAt);

public static class CivicScorecards
{
    public static bool IsMissed(string vote) =>
        vote.Contains("not voting", StringComparison.OrdinalIgnoreCase) || vote.Contains("excused", StringComparison.OrdinalIgnoreCase)
        || vote.Equals("N/V", StringComparison.OrdinalIgnoreCase) || vote.Equals("E", StringComparison.OrdinalIgnoreCase)
        || vote.Equals("NV", StringComparison.OrdinalIgnoreCase) || vote.Equals("Absent", StringComparison.OrdinalIgnoreCase);

    // Yea/Aye/Y and Nay/No/N, else null (present, not voting).
    public static string? Side(string vote) => vote.Trim().ToLowerInvariant() switch
    {
        "yea" or "aye" or "yes" or "y" => "yea",
        "nay" or "no" or "n" => "nay",
        _ => null,
    };

    // The side most of a party's members took on one roll call, or null when tied or none voted.
    public static string? PartyMajority(IEnumerable<string> partyVotes)
    {
        var sides = partyVotes.Select(Side).OfType<string>().GroupBy(s => s).Select(g => (Side: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        if (sides.Count == 0 || (sides.Count > 1 && sides[0].Count == sides[1].Count)) return null;
        return sides[0].Side;
    }

    public static CivicScorecard For(string memberId, string name, string chamber, IEnumerable<CivicMemberVoteFact> facts)
    {
        var votes = facts.Where(f => f.MemberId == memberId).GroupBy(f => f.RollCallKey).Select(g => g.First()).ToList();
        var missed = votes.Count(v => IsMissed(v.Vote));
        var partyLine = votes.Where(v => v.PartyMajority is not null && Side(v.Vote) is not null).ToList();
        return new CivicScorecard(memberId, name, chamber, votes.Count, missed,
            partyLine.Count == 0 && votes.All(v => v.PartyMajority is null) ? null : partyLine.Count(v => Side(v.Vote) == v.PartyMajority),
            partyLine.Count == 0 && votes.All(v => v.PartyMajority is null) ? null : partyLine.Count,
            votes.Select(v => v.VotedAt).Where(d => d is not null).Min());
    }
}

/// <summary>4. The weekly civic page in each person's Sentinel.</summary>
public static class CivicWeeklyPage
{
    public const string PageSystemKey = "civic-watch-weekly";
    public const string PageTitle = "Civic Watch this week";

    public sealed record Input(DateTimeOffset NowLocal, IReadOnlyList<CivicChangedItem> Changes, IReadOnlyList<CivicCalendarItem> Coming,
        IReadOnlyList<CivicScorecard> Scorecards, IReadOnlyList<CivicFollowedItem> Followed, IReadOnlyList<WatchedBillView> Bills);

    public static string Markdown(Input input)
    {
        var md = new StringBuilder();
        md.AppendLine($"Your civic week to {input.NowLocal.ToString("dd-MM-yy", CultureInfo.InvariantCulture)}: what changed, what's coming, how your representatives voted and the items you follow. Rebuilt every Monday.");
        md.AppendLine();
        md.AppendLine("## What changed this week").AppendLine();
        if (input.Changes.Count == 0) md.AppendLine("Nothing new on the desks this week.");
        foreach (var change in input.Changes.Take(15))
            md.AppendLine($"- {change.FirstSeenAt.ToLocalTime().ToString("dd-MM-yy", CultureInfo.InvariantCulture)} · {Escape(change.Kind)}: [{Escape(change.Title)}]({change.Url})");
        md.AppendLine();
        md.AppendLine("## Coming up").AppendLine();
        if (input.Coming.Count == 0) md.AppendLine("No meetings, elections or comment deadlines in the next two weeks.");
        foreach (var item in input.Coming.OrderBy(c => c.Date).Take(15))
            md.AppendLine(item.Url is null
                ? $"- {item.Date.ToString("dd-MM-yy", CultureInfo.InvariantCulture)} · {Escape(item.Title)}"
                : $"- {item.Date.ToString("dd-MM-yy", CultureInfo.InvariantCulture)} · [{Escape(item.Title)}]({item.Url})");
        md.AppendLine();
        if (input.Scorecards.Count > 0)
        {
            md.AppendLine("## Your representatives").AppendLine();
            foreach (var card in input.Scorecards)
            {
                var parts = new List<string> { $"{card.RollCalls} roll calls kept" };
                if (card.MissedShare is { } missed) parts.Add($"missed {GwsBusinessSuite.Application.Abstractions.GwsMoney.Percent(missed)}");
                if (card.WithPartyShare is { } party) parts.Add($"with their party {GwsBusinessSuite.Application.Abstractions.GwsMoney.Percent(party)}");
                md.AppendLine($"- **{Escape(card.Name)}** ({Escape(card.Chamber)}): {string.Join(", ", parts)}");
            }
            md.AppendLine();
        }
        if (input.Bills.Count > 0)
        {
            md.AppendLine("## Bills you follow").AppendLine();
            foreach (var bill in input.Bills)
                md.AppendLine($"- [{Escape(bill.Label)}]({bill.OfficialUrl}): {Escape(bill.LatestStatus)}");
            md.AppendLine();
        }
        if (input.Followed.Count > 0)
        {
            md.AppendLine("## Items you follow").AppendLine();
            foreach (var item in input.Followed)
            {
                var latest = item.Mentions.FirstOrDefault();
                md.AppendLine(latest is null
                    ? $"- **{Escape(item.Phrase)}**: not in the meeting papers yet."
                    : $"- **{Escape(item.Phrase)}**: last in the {latest.Kind.ToLowerInvariant()} of {latest.MeetingDate.ToString("dd-MM-yy", CultureInfo.InvariantCulture)} ([PDF]({latest.Url})), {item.Mentions.Count} papers in all.");
            }
            md.AppendLine();
        }
        return md.ToString();
    }

    private static string Escape(string text) => text.Replace("[", "\\[").Replace("]", "\\]").Replace("\n", " ");
}

/// <summary>6. Drafting a public comment on a Federal Register document.</summary>
public static class PublicComment
{
    public const string SystemPrompt =
        "You help a resident write a public comment on a U.S. federal rule or notice. Write a clear, respectful comment " +
        "of 150 to 300 words in the first person, from the resident's position below: say who they are only if they did, " +
        "what they support or oppose and why, and one concrete request. Use only the document's details given; invent no " +
        "statistics, studies or personal facts. Plain text, no headings, no placeholders like [Name].";

    public static string Prompt(FederalRegisterDocument document, string position) =>
        $"Document: {document.Title}\nType: {document.Type}\nAgencies: {document.Agencies}\nNumber: {document.DocumentNumber}\n" +
        (document.CommentsCloseOn is { } close ? $"Comments close: {close.ToString("dd-MM-yy", CultureInfo.InvariantCulture)}\n" : string.Empty) +
        $"Summary: {document.Abstract ?? "(none given)"}\n\nThe resident's position: {position.Trim()}";
}
