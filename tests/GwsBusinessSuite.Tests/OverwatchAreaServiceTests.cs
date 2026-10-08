using System.Net;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CameraIntel;
using GwsBusinessSuite.Application.Hazards;
using GwsBusinessSuite.Application.TrafficIncidents;
using GwsBusinessSuite.Application.Weather;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchAreaServiceTests
{
    private static readonly BoundingBox Macon = new(33.0, 32.5, -83.4, -83.9);

    private static TrafficIncident Incident(string id, string eventType, string severity = "unknown") =>
        new(id, "I-75", "", eventType, severity, 32.8, -83.6, "GDOT", "https://511ga.org");

    [Fact]
    public async Task FirstCheck_ShouldBeABaseline_ThenOnlyNewCrashesAndClosuresAlert()
    {
        var incidents = new List<TrafficIncident> { Incident("old-crash", "accidentsAndIncidents") };
        await using var f = await Fixture.CreateAsync(incidents);
        var notified = new List<OverwatchAreaAlertView>();
        f.Notifier.OnAlert += (_, alert) => notified.Add(alert);
        var area = await f.Service.AddAreaAsync("grant", "Macon", Macon, true, false, false, "moderate");

        (await f.Service.CheckAllAsync()).Should().Be(0, "the first check only learns what's already there");

        incidents.Add(Incident("new-crash", "accidentsAndIncidents"));
        incidents.Add(Incident("paving", "roadwork", "major"));
        incidents.Add(Incident("minor-closure", "closures", "minor"));
        (await f.Service.CheckAllAsync()).Should().Be(1);

        var alerts = await f.Service.ListAlertsAsync("grant");
        alerts.Should().ContainSingle();
        alerts[0].Title.Should().Be("Overwatch: Macon");
        alerts[0].Message.Should().Be("1 new: Crash on I-75",
            "roadwork never alerts, and a minor closure is below this area's moderate threshold");
        alerts[0].AreaId.Should().Be(area.Id);
        notified.Should().ContainSingle(a => a.Id == alerts[0].Id);
        (await f.Service.CountUnreadAlertsAsync("grant")).Should().Be(1);

        (await f.Service.CheckAllAsync()).Should().Be(0, "an item is only reported once");

        await f.Service.MarkAlertReadAsync("grant", alerts[0].Id);
        (await f.Service.CountUnreadAlertsAsync("grant")).Should().Be(0);
        (await f.Service.CountUnreadAlertsAsync("someone-else")).Should().Be(0);
    }

    [Fact]
    public async Task AddArea_ShouldRefuseAViewTooLargeToWatch()
    {
        await using var f = await Fixture.CreateAsync([]);

        await FluentActions.Awaiting(() => f.Service.AddAreaAsync("grant", "World", new BoundingBox(85, -85, 180, -180), true, true, true, "any"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*too large*");
    }

    [Fact]
    public void WeatherItems_ShouldMapNwsSeverity()
    {
        var alerts = new[]
        {
            new WeatherAlert("w1", "Tornado Warning", "Extreme", "Bibb", "", []),
            new WeatherAlert("w2", "Heat Advisory", "Moderate", "Bibb", "", []),
            new WeatherAlert("w3", "Special Weather Statement", "Minor", "Bibb", "", [])
        };

        OverwatchAreaService.WeatherItems(alerts, "major").Select(i => i.Text).Should().Equal("Tornado Warning");
        OverwatchAreaService.WeatherItems(alerts, "moderate").Should().HaveCount(2);
        OverwatchAreaService.WeatherItems(alerts, "any").Should().HaveCount(3);
    }

    [Fact]
    public void Summary_ShouldPutMajorItemsFirst_AndCountTheRest()
    {
        var items = Enumerable.Range(1, 6).Select(i => new OverwatchAreaItem($"k{i}", $"item {i}", i == 6 ? "major" : "minor")).ToList();

        OverwatchAreaService.Summary(items).Should().Be("6 new: item 6; item 1; item 2; item 3; and 2 more");
    }

    [Fact]
    public void Summary_ShouldMergeIdenticalLines()
    {
        var items = new[] { new OverwatchAreaItem("a", "Closure on SR 54", "major"), new OverwatchAreaItem("b", "Closure on SR 54", "major"),
            new OverwatchAreaItem("c", "Crash on I-75", "unknown") };

        OverwatchAreaService.Summary(items).Should().Be("3 new: Closure on SR 54 (\u00d72); Crash on I-75");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection _connection = null!;
        public OverwatchAreaService Service { get; private set; } = null!;
        public OverwatchAreaNotifier Notifier { get; } = new();

        public static async Task<Fixture> CreateAsync(List<TrafficIncident> incidents)
        {
            var f = new Fixture { _connection = new SqliteConnection("Data Source=:memory:") };
            await f._connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(f._connection).Options;
            await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();
            var directory = new TrafficIncidentDirectoryService([new ListProvider(incidents)], NullLogger<TrafficIncidentDirectoryService>.Instance);
            var hazards = new HazardLayerService(new HttpClient(new EmptyHandler()), new MemoryCache(new MemoryCacheOptions()), NullLogger<HazardLayerService>.Instance);
            f.Service = new OverwatchAreaService(new Factory(options), directory, new NoAlerts(), hazards, f.Notifier, TimeProvider.System,
                NullLogger<OverwatchAreaService>.Instance);
            return f;
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    private sealed class ListProvider(List<TrafficIncident> incidents) : ITrafficIncidentProvider
    {
        public string SourceName => "Test";
        public Task<IReadOnlyList<TrafficIncident>> GetIncidentsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrafficIncident>>(incidents.ToList());
    }

    private sealed class NoAlerts : INwsAlertsService
    {
        public Task<IReadOnlyList<WeatherAlert>> GetActiveAlertsAsync(BoundingBox bbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WeatherAlert>>([]);
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
