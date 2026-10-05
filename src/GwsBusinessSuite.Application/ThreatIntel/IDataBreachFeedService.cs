namespace GwsBusinessSuite.Application.ThreatIntel;

// Have I Been Pwned's public breach list (/api/v3/breaches needs no key - only searching for
// an account does). Company-level facts only: no personal data is fetched or stored.
public interface IDataBreachFeedService
{
    // Most recently added to HIBP first; fabricated breaches and spam lists are left out.
    Task<IReadOnlyList<DataBreach>> GetRecentBreachesAsync(int limit = 20, CancellationToken cancellationToken = default);
}

public sealed record DataBreach(
    string Name,
    string Title,
    string? Domain,
    DateOnly? BreachDate,
    DateTimeOffset AddedDate,
    long AccountCount,
    IReadOnlyList<string> DataClasses,
    bool IsVerified,
    string ReferenceUrl);
