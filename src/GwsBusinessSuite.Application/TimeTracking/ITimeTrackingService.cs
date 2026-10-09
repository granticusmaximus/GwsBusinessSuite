using GwsBusinessSuite.Application.Billing;

namespace GwsBusinessSuite.Application.TimeTracking;

// Minutes is live for a running timer (time so far). AmountUsd is what the entry bills for.
public sealed record TimeEntryView(
    Guid Id,
    string Username,
    Guid ContactId,
    string ContactName,
    Guid? DealId,
    string? DealTitle,
    Guid? TicketId,
    string? TicketSubject,
    string Description,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Minutes,
    bool Billable,
    decimal HourlyRateUsd,
    Guid? InvoiceId)
{
    public bool IsRunning => EndedAt is null;
    public decimal Hours => Math.Round(Minutes / 60m, 2);
    public decimal AmountUsd => Billable ? TimeTrackingMath.Amount(Minutes, HourlyRateUsd) : 0m;
}

public sealed class TimeEntryInput
{
    public Guid ContactId { get; set; }
    public Guid? DealId { get; set; }
    public Guid? TicketId { get; set; }
    public string Description { get; set; } = string.Empty;
    // For a manual entry: when the work happened and how long it took.
    public DateTimeOffset StartedAt { get; set; }
    public int Minutes { get; set; }
    public bool Billable { get; set; } = true;
    public decimal HourlyRateUsd { get; set; }
}

public sealed record UnbilledTimeSummary(Guid ContactId, string ContactName, int EntryCount, int Minutes, decimal AmountUsd);

public interface ITimeTrackingService
{
    Task<TimeEntryView?> GetRunningAsync(string username, CancellationToken cancellationToken = default);

    // Starts a timer for the user, stopping (and keeping) any timer they already had running.
    Task<TimeEntryView> StartAsync(string username, TimeEntryInput input, CancellationToken cancellationToken = default);

    Task<TimeEntryView?> StopAsync(string username, CancellationToken cancellationToken = default);

    Task<TimeEntryView> AddManualAsync(string username, TimeEntryInput input, CancellationToken cancellationToken = default);

    // Entries already on an invoice can't be changed or deleted - edit the invoice instead.
    Task DeleteAsync(Guid entryId, CancellationToken cancellationToken = default);

    // Every user's entries that started in [fromUtc, toUtc), or one user's when username is set.
    Task<IReadOnlyList<TimeEntryView>> ListAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, string? username = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TimeEntryView>> ListForContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UnbilledTimeSummary>> ListUnbilledAsync(CancellationToken cancellationToken = default);

    // The rate this user used last, to prefill the next entry; 0 when they have none.
    Task<decimal> GetLastRateAsync(string username, CancellationToken cancellationToken = default);

    // Puts the contact's unbilled, billable, stopped entries on a new Draft invoice (one line
    // each) and marks them billed. Throws InvalidOperationException when there's nothing to bill.
    Task<InvoiceView> BillUnbilledAsync(Guid contactId, string actor, CancellationToken cancellationToken = default);
}

public static class TimeTrackingMath
{
    // Hours x rate, to the cent. Minutes are kept exact; rounding happens once, on the amount.
    public static decimal Amount(int minutes, decimal hourlyRate) => Math.Round(minutes * hourlyRate / 60m, 2, MidpointRounding.AwayFromZero);

    // A stopped timer counts at least one minute, so a quick start/stop isn't silently zero.
    public static int ElapsedMinutes(DateTimeOffset startedAt, DateTimeOffset endedAt) =>
        Math.Max(1, (int)Math.Round((endedAt - startedAt).TotalMinutes, MidpointRounding.AwayFromZero));
}
