using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Privacy;

// A PrivacyRequest.SubjectIdentifier is whatever staff typed - could be a Contact's email or an
// AppUser's username, with no way to tell which just from the string. Resolving against both
// anchors and unioning the result (rather than one flat string-equality check against a single
// column, as ExportSubjectDataAsync used to do) means an identifier typed as an email still finds
// a Comment.AuthorEmail row, and one typed as a username still finds a SentinelAiRun.CreatedBy
// row, instead of silently missing whichever form wasn't typed.
public sealed record SubjectResolution(AppUser? User, Contact? Contact)
{
    public bool IsEmpty => User is null && Contact is null;

    // The values downstream text-only tables (Comment.AuthorEmail, SentinelAiRun.CreatedBy,
    // PodcastListenProgress.Username, Booking.AttendeeEmail) should be matched against - both
    // anchors found, deduplicated, never null/empty.
    public IReadOnlyList<string> MatchValues =>
        new[] { User?.Username, Contact?.Email }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

public static class SubjectResolver
{
    public static async Task<SubjectResolution> ResolveAsync(
        IAppDbContext db, string identifier, CancellationToken cancellationToken = default)
    {
        var user = await db.AppUsers.SingleOrDefaultAsync(x => x.Username == identifier, cancellationToken);
        var contact = await db.Contacts.SingleOrDefaultAsync(x => x.Email == identifier, cancellationToken);
        return new SubjectResolution(user, contact);
    }
}
