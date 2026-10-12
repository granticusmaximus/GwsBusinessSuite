using System.Globalization;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Briefings;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed record OverwatchBriefingSettings(bool Enabled, int Hour, string Email, string LastSentDate);

public sealed record MorningBriefingSection(string Name, BriefingFacts? Facts, string? Error);

// Overwatch briefings.
//   - "Brief me": this gathers the numbered facts for a view, watched area or planned route; the
//     page then has the local model on the user's own computer (BrowserLocalOllamaService) write
//     the briefing from them, never this server's model.
//   - Morning briefing: once a day at the chosen hour (server local time), a plain summary for
//     each watched area and up to MaxMorningTrips saved trips goes to the bell, and by email when
//     an address is set and SMTP is configured. It isn't model-written: nobody's computer is
//     guaranteed to be awake then, and Overwatch's AI stays local.
public sealed class OverwatchBriefingService(
    IAppDbContextFactory dbContextFactory,
    TrafficIncidentDirectoryService incidents,
    INwsAlertsService weatherAlerts,
    HazardLayerService hazards,
    OverwatchTripService trips,
    RouteWatchService routes,
    OverwatchAreaNotifier notifier,
    IOptions<GrowthReportEmailOptions> smtpOptions,
    TimeProvider timeProvider,
    ILogger<OverwatchBriefingService> logger,
    IMailTransport? mailTransport = null)
{
    public const string AlertCreatedBy = "overwatch-briefing";
    public const int MaxMorningTrips = 5;
    private readonly IMailTransport _mail = mailTransport ?? MailTransport.SmtpOnly;

    // ---- Facts for "Brief me" ----

    public async Task<BriefingFacts> GetViewFactsAsync(string username, string scope, BoundingBox bounds, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var incidentList = await Safely(() => incidents.GetIncidentsInBoundingBoxAsync(bounds, ct), [], "incidents");
        var alertList = await Safely(() => weatherAlerts.GetActiveAlertsAsync(bounds, ct), [], "weather alerts");
        var hazardList = await Safely(async () => (await hazards.GetHazardsAsync(bounds, ct)).Features, [], "hazards");
        var cameras = (await CameraStatesAsync(username, ct)).Where(c => bounds.Contains(c.Latitude, c.Longitude));
        return OverwatchBriefing.ForArea(scope, now, incidentList, alertList, hazardList, cameras);
    }

    public async Task<BriefingFacts> GetRouteFactsAsync(string username, string scope, RouteWatchResult route, CancellationToken ct = default) =>
        OverwatchBriefing.ForRoute(scope, timeProvider.GetUtcNow(), route, await CameraStatesAsync(username, ct));

    private async Task<IReadOnlyList<OverwatchCameraWatchState>> CameraStatesAsync(string username, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.OverwatchCameraWatchStates.AsNoTracking().Where(s => s.Username == username).ToListAsync(ct);
    }

    // One unreachable source shouldn't sink the whole briefing.
    private async Task<IReadOnlyList<T>> Safely<T>(Func<Task<IReadOnlyList<T>>> load, IReadOnlyList<T> fallback, string what)
    {
        try
        {
            return await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Briefing: couldn't load {What}", what);
            return fallback;
        }
    }

    // ---- Morning briefing settings ----

    public async Task<OverwatchBriefingSettings> GetSettingsAsync(string username, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.OverwatchBriefingSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Username == username, ct);
        return row is null ? new OverwatchBriefingSettings(false, 7, string.Empty, string.Empty)
            : new OverwatchBriefingSettings(row.Enabled, row.Hour, row.Email, row.LastSentDate);
    }

    public async Task SaveSettingsAsync(string username, bool enabled, int hour, string? email, CancellationToken ct = default)
    {
        if (hour is < 0 or > 23) throw new ArgumentException("Pick an hour from 00 to 23.");
        email = (email ?? string.Empty).Trim();
        if (email.Length > 0 && (email.Length > 254 || !MailboxAddress.TryParse(email, out _) || !email.Contains('@')))
            throw new ArgumentException("That email address doesn't look right.");
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var row = await db.OverwatchBriefingSubscriptions.FirstOrDefaultAsync(s => s.Username == username, ct);
        if (row is null)
        {
            row = new OverwatchBriefingSubscription { Username = username, CreatedBy = username, CreatedAt = timeProvider.GetUtcNow() };
            await db.OverwatchBriefingSubscriptions.AddAsync(row, ct);
            // Don't fire straight away for today if the hour has already passed.
            var local = timeProvider.GetLocalNow();
            if (local.Hour >= hour) row.LastSentDate = LocalDate(local);
        }
        row.Enabled = enabled;
        row.Hour = hour;
        row.Email = email;
        row.UpdatedAt = timeProvider.GetUtcNow();
        row.UpdatedBy = username;
        await db.SaveChangesAsync(ct);
    }

    public bool CanEmail() => _mail.Describe(smtpOptions.Value).CanSend && MailboxAddress.TryParse(smtpOptions.Value.FromAddress, out _);

    // ---- Morning briefing ----

    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var local = timeProvider.GetLocalNow();
        var today = LocalDate(local);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var due = (await db.OverwatchBriefingSubscriptions.Where(s => s.Enabled && s.Hour <= local.Hour).ToListAsync(ct))
            .Where(s => s.LastSentDate != today).ToList();
        if (due.Count == 0) return 0;
        // Marked first, so a failure part-way never repeats the send.
        due.ForEach(s => s.LastSentDate = today);
        await db.SaveChangesAsync(ct);

        var sent = 0;
        foreach (var subscription in due)
        {
            try
            {
                if (await SendMorningAsync(db, subscription, ct)) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Morning briefing failed for {User}", subscription.Username);
            }
        }
        return sent;
    }

    private async Task<bool> SendMorningAsync(IAppDbContext db, OverwatchBriefingSubscription subscription, CancellationToken ct)
    {
        var sections = await ComposeMorningAsync(subscription.Username, ct);
        if (sections.Count == 0) return false;
        var now = timeProvider.GetUtcNow();
        var alert = new OverwatchAreaAlert
        {
            Username = subscription.Username,
            AreaId = Guid.Empty,
            Title = $"Morning briefing {now.ToLocalGwsDate()}",
            Message = BellText(sections),
            CreatedAt = now,
            CreatedBy = AlertCreatedBy
        };
        alert.LinkUrl = $"/admin/osint?briefing={alert.Id}";
        await db.OverwatchAreaAlerts.AddAsync(alert, ct);
        await db.SaveChangesAsync(ct);
        notifier.Raise(subscription.Username, new OverwatchAreaAlertView(alert.Id, alert.AreaId, alert.Title, alert.Message, false, alert.CreatedAt, alert.LinkUrl));
        if (subscription.Email.Length > 0) await EmailAsync(subscription.Email, alert.Title, EmailText(sections), ct);
        return true;
    }

    public async Task<IReadOnlyList<MorningBriefingSection>> ComposeMorningAsync(string username, CancellationToken ct = default)
    {
        var sections = new List<MorningBriefingSection>();
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            var areas = await db.OverwatchWatchAreas.AsNoTracking().Where(a => a.Username == username).ToListAsync(ct);
            foreach (var area in areas.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var facts = await GetViewFactsAsync(username, area.Name, new BoundingBox(area.North, area.South, area.East, area.West), ct);
                sections.Add(new MorningBriefingSection(area.Name, facts, null));
            }
        }
        foreach (var trip in (await trips.ListAsync(username, ct)).OrderByDescending(t => t.UpdatedAt).Take(MaxMorningTrips))
        {
            try
            {
                var route = await routes.WatchTripAsync(trip.Stops, ct);
                sections.Add(route.Error is null
                    ? new MorningBriefingSection(trip.Name, await GetRouteFactsAsync(username, trip.Name, route, ct), null)
                    : new MorningBriefingSection(trip.Name, null, route.Error));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Morning briefing: couldn't plot trip {Trip}", trip.Id);
                sections.Add(new MorningBriefingSection(trip.Name, null, "Couldn't plot this trip just now."));
            }
        }
        return sections;
    }

    public static string BellText(IReadOnlyList<MorningBriefingSection> sections) =>
        string.Join("\n", sections.Select(s => $"{s.Name}: {(s.Facts is { } facts ? OverwatchBriefing.PlainText(facts) : s.Error)}"));

    public static string EmailText(IReadOnlyList<MorningBriefingSection> sections)
    {
        var text = new StringBuilder();
        foreach (var section in sections)
        {
            text.Append(section.Name.ToUpperInvariant()).Append('\n');
            if (section.Facts is not { } facts)
            {
                text.Append(section.Error).Append("\n\n");
                continue;
            }
            text.Append(OverwatchBriefing.PlainText(facts)).Append('\n');
            foreach (var fact in facts.Facts) text.Append("  - ").Append(fact.Text).Append('\n');
            text.Append('\n');
        }
        text.Append("Open Overwatch for the map and cameras.");
        return text.ToString();
    }

    private async Task EmailAsync(string to, string subject, string body, CancellationToken ct)
    {
        var smtp = smtpOptions.Value;
        if (!_mail.Describe(smtp).CanSend || !MailboxAddress.TryParse(smtp.FromAddress, out var from) || !MailboxAddress.TryParse(to, out var recipient))
            return;
        try
        {
            var message = new MimeMessage();
            message.From.Add(from);
            message.To.Add(recipient);
            message.Subject = $"Overwatch: {subject}";
            message.Body = new TextPart("plain") { Text = body };
            await _mail.SendAsync(message, smtp, "overwatch-briefing", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The bell already has it; a failed email shouldn't undo that.
            logger.LogWarning(ex, "Morning briefing email failed");
        }
    }

    public async Task<OverwatchAreaAlert?> GetMorningAlertAsync(string username, Guid alertId, CancellationToken ct = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.OverwatchAreaAlerts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == alertId && a.Username == username && a.CreatedBy == AlertCreatedBy, ct);
    }

    private static string LocalDate(DateTimeOffset local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed class OverwatchBriefingBackgroundService(IServiceScopeFactory scopeFactory, ILogger<OverwatchBriefingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<OverwatchBriefingService>().SendDueAsync(stoppingToken);
                if (sent > 0) logger.LogInformation("Overwatch: {Count} morning briefing(s) sent", sent);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Overwatch morning briefing run failed");
            }
        }
    }
}
