using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Briefings;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Geocoding;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.RouteWatch;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchBriefingTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 6, 30, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<ApplicationDbContext> _options = null!;
    private readonly Clock _clock = new(Now);
    private readonly RecordingMail _mail = new();
    private readonly OverwatchAreaNotifier _notifier = new();

    private static TrafficIncident Incident(string id, string category, string severity, string road = "I 75") =>
        new(id, road, "Two lanes blocked", category, severity, 33.75, -84.39, "GDOT", "https://511ga.org", Category: category, SeverityLevel: severity);

    [Fact]
    public void ForArea_ShouldNumberTheFacts_MostSeriousFirst_AndSkipRoutineAndStaleItems()
    {
        var facts = OverwatchBriefing.ForArea("Atlanta", Now,
            [Incident("r", IncidentCategories.Roadwork, IncidentSeverityLevels.Major), Incident("m", IncidentCategories.Crash, IncidentSeverityLevels.Minor),
             Incident("c", IncidentCategories.Closure, IncidentSeverityLevels.Major, "GA 400")],
            [new WeatherAlert("w1", "Flood Watch", "Moderate", "Fulton; DeKalb", "", [])],
            [new HazardFeature("q", "earthquake", "M 2.6 earthquake", "", 33.7, -84.4, IncidentSeverityLevels.Minor, "", Now)],
            [Camera("fresh", Now.AddMinutes(-20)), Camera("stale", Now.AddHours(-3))]);

        facts.Facts.Select(f => (f.Number, f.Kind)).Should().Equal((1, "incident"), (2, "incident"), (3, "weather"), (4, "camera"));
        facts.Facts[0].Text.Should().Be("Closure on GA 400 (major): Two lanes blocked");
        facts.Facts[2].Text.Should().Be("NWS Flood Watch (moderate) for Fulton; DeKalb");
        facts.Facts[3].Text.Should().StartWith("Camera fresh looked bad: traffic stopped");
        OverwatchBriefing.UserPrompt(facts).Should().Contain("[2] Crash on I 75: Two lanes blocked");
    }

    [Fact]
    public void Clean_ShouldDropCitationsToFactsThatDontExist_AndFallBackWhenEmpty()
    {
        var facts = OverwatchBriefing.ForArea("Atlanta", Now, [Incident("c", IncidentCategories.Crash, IncidentSeverityLevels.Major)], [], [], []);

        var cleaned = OverwatchBriefing.Clean("A major crash on I 75 [1, 7]. Also a parade [4].  ", facts);

        cleaned.Text.Should().Be("A major crash on I 75 [1]. Also a parade.");
        cleaned.Cited.Should().Equal(1);
        OverwatchBriefing.Clean("   ", facts).Text.Should().Be("1 crash or closure. Crash on I 75 (major): Two lanes blocked.");
        OverwatchBriefing.PlainText(new BriefingFacts("x", Now, [])).Should().StartWith("All quiet");
    }

    [Fact]
    public void ForRoute_ShouldOrderIncidentsAlongTheDrive_AndKeepOnlyCamerasNearIt()
    {
        var route = new RouteWatchResult("Macon, GA", "Atlanta, GA", 84, 95, [new RoutePoint(32.84, -83.63), new RoutePoint(33.75, -84.39)], [],
            [new RouteIncident(Incident("late", IncidentCategories.Crash, IncidentSeverityLevels.Major), 70, 0.1),
             new RouteIncident(Incident("early", IncidentCategories.Closure, IncidentSeverityLevels.Moderate), 12, 0.1)], []);

        var facts = OverwatchBriefing.ForRoute("Macon to Atlanta", Now, route, [Camera("near", Now), Camera("far", Now, 40, -100)]);

        facts.Facts[0].Text.Should().Be("Route Macon, GA to Atlanta, GA: 84 miles, about 1 h 35 min without delays");
        facts.Facts.Where(f => f.Kind == "incident").Select(f => f.Text).Should().Equal(
            "Closure on I 75 (moderate): Two lanes blocked, mile 12 of the drive", "Crash on I 75 (major): Two lanes blocked, mile 70 of the drive");
        facts.Facts.Where(f => f.Kind == "camera").Should().ContainSingle().Which.Text.Should().StartWith("Camera near");
    }

    [Fact]
    public async Task Morning_ShouldSendOnceADayAtTheHour_ToTheBellAndEmail()
    {
        var service = await CreateAsync();
        await service.SaveSettingsAsync("grant", true, 7, "grant@example.com");
        var raised = new List<OverwatchAreaAlertView>();
        _notifier.OnAlert += (_, alert) => raised.Add(alert);

        (await service.SendDueAsync()).Should().Be(0, "it's 06:30, before the hour");
        _clock.Now = Now.AddHours(1);
        (await service.SendDueAsync()).Should().Be(1);
        (await service.SendDueAsync()).Should().Be(0, "already sent today");

        raised.Should().ContainSingle();
        await using var db = new ApplicationDbContext(_options);
        var alert = await db.OverwatchAreaAlerts.SingleAsync();
        alert.Title.Should().Be("Morning briefing 12-10-26");
        alert.Message.Should().Be("North: 1 crash or closure. Crash on I 75 (major): Two lanes blocked.");
        alert.LinkUrl.Should().Be($"/admin/osint?briefing={alert.Id}");
        (await service.GetMorningAlertAsync("grant", alert.Id)).Should().NotBeNull();
        (await service.GetMorningAlertAsync("someone-else", alert.Id)).Should().BeNull();
        _mail.Sent.Should().ContainSingle().Which.Subject.Should().Be("Overwatch: Morning briefing 12-10-26");
        ((TextPart)_mail.Sent[0].Body).Text.Should().Contain("NORTH\n1 crash or closure.").And.Contain("  - Crash on I 75 (major)");
    }

    [Fact]
    public async Task Settings_ShouldRejectBadInput_AndNotFireTodayWhenTheHourHasPassed()
    {
        var service = await CreateAsync();
        await service.Invoking(s => s.SaveSettingsAsync("grant", true, 24, null)).Should().ThrowAsync<ArgumentException>();
        await service.Invoking(s => s.SaveSettingsAsync("grant", true, 7, "not an email")).Should().ThrowAsync<ArgumentException>();

        await service.SaveSettingsAsync("grant", true, 5, "");

        (await service.SendDueAsync()).Should().Be(0, "05:00 had already passed when it was turned on");
        (await service.GetSettingsAsync("grant")).Should().Be(new OverwatchBriefingSettings(true, 5, "", "2026-10-12"));
        _clock.Now = Now.AddDays(1);
        (await service.SendDueAsync()).Should().Be(1);
        _mail.Sent.Should().BeEmpty("no address was given");
    }

    private static OverwatchCameraWatchState Camera(string name, DateTimeOffset checkedAt, double lat = 33.75, double lon = -84.39) => new()
    {
        Username = "grant", CameraId = name, CameraName = name, LastLevel = "bad", LastSummary = "traffic stopped",
        Latitude = lat, Longitude = lon, LastCheckedUnix = checkedAt.ToUnixTimeSeconds()
    };

    private async Task<OverwatchBriefingService> CreateAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        await using (var db = new ApplicationDbContext(_options))
        {
            await db.Database.EnsureCreatedAsync();
            db.OverwatchWatchAreas.Add(new OverwatchWatchArea { Username = "grant", Name = "North", North = 34, South = 33.5, East = -84, West = -84.7 });
            await db.SaveChangesAsync();
        }
        var factory = new Factory(_options);
        var incidents = new TrafficIncidentDirectoryService([new Incidents([Incident("c1", IncidentCategories.Crash, IncidentSeverityLevels.Major)])],
            NullLogger<TrafficIncidentDirectoryService>.Instance);
        var http = new HttpClient(new Unavailable());
        return new OverwatchBriefingService(factory, incidents, new NoAlerts(),
            new HazardLayerService(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<HazardLayerService>.Instance),
            new OverwatchTripService(factory, _clock),
            new RouteWatchService(http, new NoGeocoding(), new CameraDirectoryService([], NullLogger<CameraDirectoryService>.Instance), incidents, new NoAlerts(), NullLogger<RouteWatchService>.Instance),
            _notifier,
            Options.Create(new GrowthReportEmailOptions { Host = "smtp.example.com", FromAddress = "noreply@example.com" }),
            _clock, NullLogger<OverwatchBriefingService>.Instance, _mail);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class Incidents(IReadOnlyList<TrafficIncident> incidents) : ITrafficIncidentProvider
    {
        public string SourceName => "GDOT";
        public Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) => Task.FromResult(incidents);
    }

    private sealed class NoAlerts : INwsAlertsService
    {
        public Task<IReadOnlyList<WeatherAlert>> GetActiveAlertsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WeatherAlert>>([]);
    }

    private sealed class NoGeocoding : IGeocodingService
    {
        public Task<GeocodeResult?> GeocodeAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult<GeocodeResult?>(null);
        public Task<PlaceInfo?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default) => Task.FromResult<PlaceInfo?>(null);
    }

    private sealed class Unavailable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class RecordingMail : IMailTransport
    {
        public List<MimeMessage> Sent { get; } = [];
        public MailRoute Describe(ISmtpTransportOptions options) => new(MailRouteKind.Smtp, "test");
        public Task SendAsync(MimeMessage message, ISmtpTransportOptions options, string filePrefix, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public Task RefreshGmailStatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
