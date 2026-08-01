using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SecurityAudit;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SecurityAuditService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    ICurrentUserAccessor currentUserAccessor,
    ISecretProtector secretProtector,
    TimeProvider timeProvider) : ISecurityAuditService
{
    private static readonly SemaphoreSlim WriteGate = new(1, 1);
    private static readonly string[] ForbiddenDetailKeyFragments =
        ["password", "secret", "token", "key", "cookie", "content", "prompt", "body", "message"];

    public async Task<Guid> RecordAsync(SecurityAuditInput input, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        var actor = Normalize(input.ActorUsername ?? await currentUserAccessor.GetCurrentUsernameAsync(cancellationToken), 100, "unknown");
        var detailsJson = SerializeSafeDetails(input.Details);
        var occurredAt = timeProvider.GetUtcNow();

        await WriteGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var previous = await db.SecurityAuditEvents
                .AsNoTracking()
                .OrderByDescending(item => item.ChainSequence)
                .Select(item => new { item.ChainSequence, item.EventHash })
                .FirstOrDefaultAsync(cancellationToken);

            var row = new SecurityAuditEvent
            {
                CreatedAt = occurredAt,
                ChainSequence = (previous?.ChainSequence ?? 0) + 1,
                OccurredAtUnixSeconds = occurredAt.ToUnixTimeSeconds(),
                CreatedBy = actor,
                ActorUsername = actor,
                Category = Normalize(input.Category, 64),
                Action = Normalize(input.Action, 120),
                Outcome = Normalize(input.Outcome, 32),
                Severity = Normalize(input.Severity, 32),
                TargetType = NormalizeNullable(input.TargetType, 80),
                TargetId = NormalizeNullable(input.TargetId, 200),
                CorrelationId = Normalize(input.CorrelationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N"), 160),
                DetailsJson = detailsJson,
                NetworkAddressProtected = string.IsNullOrWhiteSpace(input.NetworkAddress)
                    ? null
                    : secretProtector.Protect(Normalize(input.NetworkAddress, 80)),
                PreviousEventHash = previous?.EventHash ?? string.Empty
            };
            row.EventHash = ComputeHash(row);
            db.SecurityAuditEvents.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            return row.Id;
        }
        finally
        {
            WriteGate.Release();
        }
    }

    public async Task<SecurityAuditPage> QueryAsync(SecurityAuditQuery query, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = db.SecurityAuditEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Category)) rows = rows.Where(item => item.Category == query.Category);
        if (!string.IsNullOrWhiteSpace(query.Outcome)) rows = rows.Where(item => item.Outcome == query.Outcome);
        if (!string.IsNullOrWhiteSpace(query.Actor)) rows = rows.Where(item => item.ActorUsername == query.Actor);
        if (query.From is { } from) rows = rows.Where(item => item.OccurredAtUnixSeconds >= from.ToUnixTimeSeconds());
        if (query.To is { } to) rows = rows.Where(item => item.OccurredAtUnixSeconds <= to.ToUnixTimeSeconds());
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            rows = rows.Where(item => item.Action.Contains(search)
                                      || (item.TargetId != null && item.TargetId.Contains(search))
                                      || item.CorrelationId.Contains(search));
        }

        var total = await rows.CountAsync(cancellationToken);
        var selected = await rows.OrderByDescending(item => item.ChainSequence)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new SecurityAuditPage(selected.Select(ToView).ToList(), total, page, pageSize);
    }

    public async Task<SecurityAuditIntegrityResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SecurityAuditEvents.AsNoTracking()
            .OrderBy(item => item.ChainSequence).ToListAsync(cancellationToken);
        string? priorHash = null;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (index > 0 && !FixedEquals(row.PreviousEventHash, priorHash!))
                return new SecurityAuditIntegrityResult(false, index + 1, row.Id, "The previous-event link does not match.");
            if (!FixedEquals(row.EventHash, ComputeHash(row)))
                return new SecurityAuditIntegrityResult(false, index + 1, row.Id, "The event hash does not match its stored fields.");
            priorHash = row.EventHash;
        }
        return new SecurityAuditIntegrityResult(true, rows.Count);
    }

    private static SecurityAuditEventView ToView(SecurityAuditEvent row) => new(
        row.Id, row.CreatedAt, row.Category, row.Action, row.Outcome, row.Severity,
        row.ActorUsername, row.TargetType, row.TargetId, row.CorrelationId,
        JsonSerializer.Deserialize<Dictionary<string, string?>>(row.DetailsJson) ?? [],
        !string.IsNullOrWhiteSpace(row.NetworkAddressProtected));

    private static void ValidateInput(SecurityAuditInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Category) || string.IsNullOrWhiteSpace(input.Action)
            || string.IsNullOrWhiteSpace(input.Outcome) || string.IsNullOrWhiteSpace(input.Severity))
            throw new ArgumentException("Audit category, action, outcome, and severity are required.");
    }

    private static string SerializeSafeDetails(IReadOnlyDictionary<string, string?>? details)
    {
        if (details is null || details.Count == 0) return "{}";
        if (details.Count > 20) throw new ArgumentException("Audit metadata is limited to 20 fields.");
        var safe = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in details)
        {
            var key = Normalize(pair.Key, 50);
            if (ForbiddenDetailKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Audit metadata key '{key}' may contain sensitive data and is not allowed.");
            safe[key] = NormalizeNullable(pair.Value, 250);
        }
        return JsonSerializer.Serialize(safe);
    }

    private static string ComputeHash(SecurityAuditEvent row)
    {
        var canonical = string.Join('\n', row.Id.ToString("N"), row.ChainSequence, row.OccurredAtUnixSeconds,
            row.CreatedAt.ToUniversalTime().ToString("O"),
            row.ActorUsername, row.Category, row.Action, row.Outcome, row.Severity,
            row.TargetType ?? string.Empty, row.TargetId ?? string.Empty, row.CorrelationId,
            row.DetailsJson, row.NetworkAddressProtected ?? string.Empty, row.PreviousEventHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool FixedEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
        catch (FormatException) { return false; }
    }

    private static string Normalize(string? value, int maxLength, string fallback = "")
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) normalized = fallback;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? NormalizeNullable(string? value, int maxLength)
    {
        var normalized = Normalize(value, maxLength);
        return normalized.Length == 0 ? null : normalized;
    }
}
