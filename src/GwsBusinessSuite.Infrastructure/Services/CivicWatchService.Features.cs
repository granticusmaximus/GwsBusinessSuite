using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.GovernmentIntelligence;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// Civic Watch features (20-11-26): calendar subscription, items followed across meetings, the
// school board desk, the weekly page in Sentinel, representative scorecards and a public comment
// helper. Every source is public and key-less.
public sealed partial class CivicWatchService
{
    private const string SchoolBoardListingUrl = "https://simbli.eboardsolutions.com/SB_Meetings/SB_MeetingListing.aspx?S=4089";
    private const string SchoolBoardMeetingUrl = "https://simbli.eboardsolutions.com/SB_Meetings/ViewMeeting.aspx?S=4089&MID=";
    private const string SchoolBoardCacheKey = "civic-watch:school-board";
    private static readonly TimeSpan CalendarPast = TimeSpan.FromDays(14);
    private static readonly TimeSpan CalendarAhead = TimeSpan.FromDays(120);

    // ---- 1. Calendar subscription ----

    public async Task<string> GetCalendarTokenAsync(string username, bool renew = false, CancellationToken ct = default)
    {
        var person = PersonKey(username) ?? throw new ArgumentException("Sign in to get a calendar link.");
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var state = await StateForAsync(db, person, username, ct);
        if (renew || string.IsNullOrWhiteSpace(state.CalendarToken))
        {
            state.CalendarToken = CivicCalendar.NewToken();
            state.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return state.CalendarToken!;
    }

    public async Task<string?> CalendarForTokenAsync(string token, GovernmentIntelligenceSnapshot? snapshot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 64 || !token.All(Uri.IsHexDigit)) return null;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var username = await db.CivicWatchUserStates.AsNoTracking().Where(s => s.CalendarToken == token).Select(s => s.Username).FirstOrDefaultAsync(ct);
        if (username is null) return null;
        return CivicCalendar.Build(await CalendarItemsAsync(username, snapshot, ct), timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<CivicCalendarItem>> CalendarItemsAsync(string? username, GovernmentIntelligenceSnapshot? snapshot, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        var from = today.AddDays(-(int)CalendarPast.TotalDays);
        var to = today.AddDays((int)CalendarAhead.TotalDays);
        var items = new List<CivicCalendarItem>();
        foreach (var meeting in snapshot?.Community.Meetings ?? [])
            if (meeting.MeetingDate is { } date && date >= from && date <= to)
                items.Add(new CivicCalendarItem($"meeting-{date:yyyyMMdd}-{Math.Abs(StableHash(meeting.Title))}", date, meeting.Title, meeting.Url,
                    string.IsNullOrWhiteSpace(meeting.Location) ? meeting.Source : $"{meeting.Location} · {meeting.Source}"));
        try
        {
            foreach (var election in (await GetElectionsAsync(false, ct)).Upcoming.Where(e => e.ElectionDate >= from && e.ElectionDate <= to))
                items.Add(new CivicCalendarItem($"election-{election.ElectionDate:yyyyMMdd}", election.ElectionDate, election.Name, CountyElectionsUrl,
                    string.IsNullOrWhiteSpace(election.RegistrationDeadline) ? null : $"Registration deadline: {election.RegistrationDeadline}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Civic calendar: elections skipped"); }
        try
        {
            foreach (var doc in (await GetFederalRegisterAsync(username, false, ct)).Items.Where(d => d.CommentsCloseOn is { } close && close >= today && close <= to))
                items.Add(new CivicCalendarItem($"comment-{doc.DocumentNumber}", doc.CommentsCloseOn!.Value, $"Comments close: {doc.Title}", doc.Url, doc.Agencies));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Civic calendar: comment deadlines skipped"); }
        try
        {
            foreach (var meeting in (await GetSchoolBoardAsync(false, ct)).Upcoming)
                items.Add(new CivicCalendarItem($"school-board-{meeting.StartsAt:yyyyMMddHHmm}", DateOnly.FromDateTime(meeting.StartsAt.DateTime),
                    $"School board: {meeting.Title} ({meeting.StartsAt:HH:mm})", meeting.Url, "Houston County Board of Education"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "Civic calendar: school board skipped"); }
        return items.OrderBy(i => i.Date).ToList();
    }

    // A hash that doesn't change between runs (string.GetHashCode does), for calendar ids.
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in text) hash = hash * 31 + c;
            return hash == int.MinValue ? 0 : hash;
        }
    }

    private async Task<CivicWatchUserState> StateForAsync(IAppDbContext db, string person, string username, CancellationToken ct)
    {
        var state = await db.CivicWatchUserStates.Where(s => s.Username == person).OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (state is not null) return state;
        state = new CivicWatchUserState { Username = person, CreatedAt = timeProvider.GetUtcNow(), CreatedBy = username };
        db.CivicWatchUserStates.Add(state);
        return state;
    }

    // ---- 2. Items followed across meetings ----

    public async Task<IReadOnlyList<CivicFollowedItem>> GetFollowedItemsAsync(string username, CancellationToken ct = default)
    {
        var person = PersonKey(username);
        if (person is null) return [];
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var phrases = CivicFollowing.Phrases(await db.CivicWatchUserStates.AsNoTracking().Where(s => s.Username == person).Select(s => s.FollowedItems).FirstOrDefaultAsync(ct));
        if (phrases.Count == 0) return [];
        var documents = await db.CivicMeetingDocuments.AsNoTracking().Where(d => d.Text != null).ToListAsync(ct);
        return phrases.Select(phrase => new CivicFollowedItem(phrase, documents
                .Where(d => CivicMentions.Found(d.Text, [phrase]).Count > 0)
                .Select(d => new CivicItemMention(ParseMeetingDate(d.MeetingDate), d.Kind, d.Title, d.Url, CivicMentions.Snippet(d.Text, phrase) ?? string.Empty))
                .OrderByDescending(m => m.MeetingDate)
                .ToList()))
            .ToList();
    }

    public async Task SetFollowedItemsAsync(string username, string phrases, CancellationToken ct = default)
    {
        var person = PersonKey(username) ?? throw new ArgumentException("Sign in to follow items.");
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var state = await StateForAsync(db, person, username, ct);
        state.FollowedItems = string.Join('\n', CivicFollowing.Phrases(phrases));
        state.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static DateOnly ParseMeetingDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateOnly.MinValue;

    // ---- 3. The school board desk ----

    public async Task<SchoolBoardDesk> GetSchoolBoardAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && cache.TryGetValue(SchoolBoardCacheKey, out SchoolBoardDesk? cached) && cached is not null) return cached;
        var html = await GetStringOrNullAsync(SchoolBoardListingUrl, ct);
        if (html is null) return new([], [], "The school board's meeting list couldn't be reached just now.");
        var meetings = ParseSchoolBoardMeetings(html);
        var now = timeProvider.GetLocalNow();
        var desk = new SchoolBoardDesk(
            meetings.Where(m => m.StartsAt >= now.AddHours(-3)).OrderBy(m => m.StartsAt).Take(4).ToList(),
            meetings.Where(m => m.StartsAt < now.AddHours(-3)).OrderByDescending(m => m.StartsAt).Take(6).ToList(),
            null);
        cache.Set(SchoolBoardCacheKey, desk, TimeSpan.FromHours(6));
        return desk;
    }

    [GeneratedRegex("""title='(?<date>\d{2}/\d{2}/\d{4} - \d{2}:\d{2} [AP]M)'.*?ViewMeeting\("\d+","(?<id>\d+)".*?>(?<title>[^<]+)</a>(?<rest>.*?)</tr>""", RegexOptions.Singleline)]
    private static partial Regex SchoolBoardRowRegex();

    // The district's Simbli listing: date and time, title, meeting id and whether minutes are out.
    public static IReadOnlyList<SchoolBoardMeeting> ParseSchoolBoardMeetings(string html)
    {
        var meetings = new List<SchoolBoardMeeting>();
        foreach (Match row in SchoolBoardRowRegex().Matches(html))
        {
            if (!DateTime.TryParseExact(row.Groups["date"].Value, "MM/dd/yyyy - hh:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) continue;
            // The listing's times are Eastern (the district is in Georgia).
            var startsAt = new DateTimeOffset(local, EasternOffset(local));
            var title = Regex.Replace(WebUtility.HtmlDecode(row.Groups["title"].Value), @"\s+", " ").Trim();
            var minutes = !row.Groups["rest"].Value.Contains("UNPUBLISHED", StringComparison.OrdinalIgnoreCase)
                          && row.Groups["rest"].Value.Contains("ViewMinutes", StringComparison.Ordinal);
            meetings.Add(new SchoolBoardMeeting(startsAt, title, SchoolBoardMeetingUrl + row.Groups["id"].Value, minutes));
        }
        return meetings.DistinctBy(m => m.Url).ToList();
    }

    private static TimeSpan EasternOffset(DateTime local)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York").GetUtcOffset(local);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeSpan.FromHours(-5);
        }
    }

    // ---- 4. The weekly civic page ----

    public async Task<int> RunWeeklyPagesIfDueAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default)
    {
        if (wiki is null) return 0;
        var now = timeProvider.GetUtcNow();
        var nowLocal = timeProvider.GetLocalNow();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var readers = (await db.CivicWatchUserStates.AsNoTracking().ToListAsync(ct))
            .Where(s => s.LastVisitedAt is { } visited && now - visited < TimeSpan.FromDays(30))
            .Where(s => NewsWatchService.ReadingDigestDue(s.WeeklyPageBuiltAt?.ToOffset(nowLocal.Offset), nowLocal))
            .Select(s => s.Username)
            .ToList();
        var built = 0;
        foreach (var reader in readers)
        {
            try
            {
                await BuildWeeklyPageAsync(reader, snapshot, ct);
                built++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Civic Watch: weekly page failed for a reader");
            }
        }
        return built;
    }

    public async Task<Guid> BuildWeeklyPageAsync(string username, GovernmentIntelligenceSnapshot? snapshot, CancellationToken ct = default)
    {
        if (wiki is null) throw new InvalidOperationException("Sentinel isn't available here.");
        var person = PersonKey(username) ?? throw new ArgumentException("Sign in to build your page.");
        var nowLocal = timeProvider.GetLocalNow();
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var coming = (await CalendarItemsAsync(username, snapshot, ct)).Where(i => i.Date >= today && i.Date <= today.AddDays(14)).ToList();
        var input = new CivicWeeklyPage.Input(nowLocal, (await GetChangesAsync(TimeSpan.FromDays(7), ct)).Items, coming,
            await GetScorecardsAsync(username, ct), await GetFollowedItemsAsync(username, ct),
            (await ListWatchedBillsAsync(ct)).Where(b => string.Equals(b.AddedBy, username, StringComparison.OrdinalIgnoreCase)).ToList());
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var id = await SentinelPersonalPages.WriteAsync(db, wiki, CivicWeeklyPage.PageSystemKey, person, CivicWeeklyPage.PageTitle, "🏛️",
            CivicWeeklyPage.Markdown(input), ct);
        var state = await StateForAsync(db, person, username, ct);
        state.WeeklyPageBuiltAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return id;
    }

    // ---- 5. Representative scorecards ----

    public async Task RecordMemberVotesAsync(GovernmentIntelligenceSnapshot snapshot, CancellationToken ct = default)
    {
        var rows = new List<CivicMemberVote>();
        foreach (var vote in snapshot.Federal.HouseVotes.Concat(snapshot.Federal.SenateVotes))
        {
            var key = $"{vote.Chamber}:{vote.RollCallNumber}:{vote.VotedAt?.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
            var majorities = vote.Votes.Where(v => !string.IsNullOrWhiteSpace(v.Party)).GroupBy(v => v.Party)
                .ToDictionary(g => g.Key, g => CivicScorecards.PartyMajority(g.Select(v => v.Vote)));
            foreach (var member in vote.Votes.Where(v => !string.IsNullOrWhiteSpace(v.MemberId) && string.Equals(v.State, "GA", StringComparison.OrdinalIgnoreCase)))
                rows.Add(new CivicMemberVote
                {
                    Level = "federal", Chamber = vote.Chamber, MemberId = member.MemberId!, Name = member.Name, Party = member.Party,
                    RollCallKey = key, Measure = Truncate(vote.Measure, 200), Vote = Truncate(member.Vote, 40),
                    PartyMajority = majorities.GetValueOrDefault(member.Party), VotedAt = vote.VotedAt,
                });
        }
        foreach (var vote in snapshot.State.HouseVotes.Concat(snapshot.State.SenateVotes))
        {
            var key = $"GA {vote.Chamber}:{vote.RollCallNumber}:{vote.VotedAt?.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
            foreach (var member in vote.Votes.Where(v => v.MemberId is not null))
                rows.Add(new CivicMemberVote
                {
                    Level = "state", Chamber = vote.Chamber, MemberId = member.MemberId!.Value.ToString(CultureInfo.InvariantCulture), Name = member.Name,
                    RollCallKey = key, Measure = Truncate(vote.Measure, 200), Vote = Truncate(member.Vote, 40), VotedAt = vote.VotedAt,
                });
        }
        if (rows.Count == 0) return;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var keys = rows.Select(r => r.RollCallKey).Distinct().ToList();
        var known = (await db.CivicMemberVotes.AsNoTracking().Where(v => keys.Contains(v.RollCallKey)).Select(v => new { v.MemberId, v.RollCallKey }).ToListAsync(ct))
            .Select(v => (v.MemberId, v.RollCallKey)).ToHashSet();
        var now = timeProvider.GetUtcNow();
        foreach (var row in rows.DistinctBy(r => (r.MemberId, r.RollCallKey)).Where(r => !known.Contains((r.MemberId, r.RollCallKey))))
        {
            row.CreatedAt = now;
            db.CivicMemberVotes.Add(row);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CivicScorecard>> GetScorecardsAsync(string? username, CancellationToken ct = default)
    {
        var representatives = (await GetRepresentativesAsync(username, ct)).Members;
        if (representatives.Count == 0) return [];
        var ids = representatives.Select(r => r.MemberId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var facts = (await db.CivicMemberVotes.AsNoTracking().Where(v => ids.Contains(v.MemberId)).ToListAsync(ct))
            .Select(v => new CivicMemberVoteFact(v.MemberId, v.RollCallKey, v.Vote, v.PartyMajority, v.VotedAt)).ToList();
        return representatives.Where(r => !string.IsNullOrWhiteSpace(r.MemberId))
            .Select(r => CivicScorecards.For(r.MemberId, r.Name, r.Chamber, facts))
            .ToList();
    }

    // ---- 6. A public comment, drafted ----

    public async Task<(string? Text, string? Problem)> DraftPublicCommentAsync(FederalRegisterDocument document, string position, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(position) || position.Trim().Length < 10) return (null, "Say in a sentence or two what you think and why.");
        var trimmed = position.Trim();
        if (trimmed.Length > 2000) trimmed = trimmed[..2000];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SummaryTimeout);
            var raw = await ollama.GenerateAsync(studioOptions.Value.Model, PublicComment.SystemPrompt, PublicComment.Prompt(document, trimmed), timeout.Token);
            var text = Regex.Replace(raw ?? string.Empty, "<think>.*?</think>", string.Empty, RegexOptions.Singleline).Trim();
            return text.Length < 40 ? (null, "The AI model's draft came back empty. Try again.") : (text, null);
        }
        catch (ServerAiUnavailableException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Civic Watch: public comment draft failed");
            return (null, "The AI model didn't answer just now. Try again in a minute.");
        }
    }
}
