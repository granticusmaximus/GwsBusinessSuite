using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DigitalOceanServiceTests
{
    [Fact]
    public async Task GetDropletInfoAsync_ShouldReportUnavailable_WhenNoSettingsSaved()
    {
        await using var db = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => throw new InvalidOperationException("Should not call the API without settings."));
        var service = CreateService(db, handler);

        var result = await service.GetDropletInfoAsync();

        result.Available.Should().BeFalse();
        result.UnavailableReason.Should().Contain("isn't connected");
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldRoundTripApiToken_ThroughSecretProtector()
    {
        await using var db = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var service = CreateService(db, handler);

        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "12345" });
        var settings = await service.GetSettingsAsync();

        // GetSettingsAsync never exposes the decrypted token to callers (it round-trips
        // to the browser otherwise) - only whether one is saved and readable.
        settings.Should().NotBeNull();
        settings!.HasApiToken.Should().BeTrue();
        settings.DropletId.Should().Be("12345");
        settings.ApiTokenUnreadable.Should().BeFalse();
        db.DigitalOceanSettings.Single().UpdatedBy.Should().Be("grantwatson");
        db.DigitalOceanSettings.Single().ApiToken.Should().Be("enc::dop_v1_secret");
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldLeaveExistingToken_WhenNewTokenIsBlank()
    {
        await using var db = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var service = CreateService(db, handler);

        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "12345" });
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = null, DropletId = "99999" });

        var row = db.DigitalOceanSettings.Single();
        row.ApiToken.Should().Be("enc::dop_v1_secret");
        row.DropletId.Should().Be("99999");
    }

    [Fact]
    public async Task SaveSettingsAsync_ShouldClearToken_WhenClearApiTokenIsSet()
    {
        await using var db = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var service = CreateService(db, handler);

        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "12345" });
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { ClearApiToken = true, DropletId = "12345" });

        var settings = await service.GetSettingsAsync();
        settings!.HasApiToken.Should().BeFalse();
    }

    [Fact]
    public async Task GetDropletInfoAsync_ShouldSendBearerAuthAndParseDropletFields()
    {
        await using var db = await CreateDbAsync();
        var observedRequests = new List<HttpRequestMessage>();
        using var handler = new RecordingHandler(request =>
        {
            observedRequests.Add(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"droplet":{"name":"gwssuite-droplet","status":"active","size_slug":"s-2vcpu-4gb","vcpus":2,
                      "region":{"name":"NYC1","slug":"nyc1"},
                      "size":{"memory":4096,"disk":80},
                      "networks":{"v4":[{"ip_address":"203.0.113.5","type":"public"}]}}}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(db, handler);
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "999" });

        var result = await service.GetDropletInfoAsync();

        result.Available.Should().BeTrue();
        result.Droplet!.Name.Should().Be("gwssuite-droplet");
        result.Droplet.PublicIpAddress.Should().Be("203.0.113.5");
        result.Droplet.Region.Should().Be("NYC1");
        observedRequests.Should().ContainSingle();
        observedRequests[0].Headers.Authorization!.Parameter.Should().Be("dop_v1_secret");
        observedRequests[0].RequestUri!.PathAndQuery.Should().Contain("droplets/999");
    }

    [Fact]
    public async Task RebootDropletAsync_ShouldPostRebootAction_AndRecordAuditLog()
    {
        await using var db = await CreateDbAsync();
        HttpRequestMessage? observedRequest = null;
        string? observedBody = null;
        using var handler = new RecordingHandler(request =>
        {
            observedRequest = request;
            observedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"action":{"id":555,"status":"in-progress","type":"reboot","started_at":"2026-01-01T00:00:00Z"}}""", Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(db, handler);
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "999" });

        var result = await service.RebootDropletAsync("grantwatson");

        result.Succeeded.Should().BeTrue();
        observedRequest!.Method.Should().Be(HttpMethod.Post);
        observedBody.Should().Contain("\"type\":\"reboot\"");

        var log = db.DockerActionLogs.Single();
        log.ContainerName.Should().Be("droplet");
        log.Action.Should().Be("Reboot");
        log.Succeeded.Should().BeTrue();
        log.PerformedBy.Should().Be("grantwatson");
    }

    [Fact]
    public async Task ResizeDropletAsync_ShouldIncludeSizeAndDiskFields_InRequestBody()
    {
        await using var db = await CreateDbAsync();
        string? observedBody = null;
        using var handler = new RecordingHandler(request =>
        {
            observedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"action":{"id":556,"status":"in-progress","type":"resize","started_at":"2026-01-01T00:00:00Z"}}""", Encoding.UTF8, "application/json")
            };
        });
        var service = CreateService(db, handler);
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "999" });

        await service.ResizeDropletAsync("s-4vcpu-8gb", resizeDisk: true, "grantwatson");

        observedBody.Should().Contain("\"type\":\"resize\"");
        observedBody.Should().Contain("s-4vcpu-8gb");
        observedBody.Should().Contain("\"disk\":true");
    }

    [Fact]
    public async Task RebootDropletAsync_ShouldReturnFailure_AndRecordAuditLog_WhenApiReturnsError()
    {
        await using var db = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"message\":\"Unable to authenticate\"}")
        });
        var service = CreateService(db, handler);
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "bad-token", DropletId = "999" });

        var result = await service.RebootDropletAsync("grantwatson");

        result.Succeeded.Should().BeFalse();
        var log = db.DockerActionLogs.Single();
        log.Succeeded.Should().BeFalse();
    }

    private static DigitalOceanService CreateService(ApplicationDbContext db, HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.digitalocean.com/v2/") };
        return new DigitalOceanService(
            client,
            db,
            new FakeSecretProtector(),
            NullLogger<DigitalOceanService>.Instance,
            new FixedCurrentUserAccessor("grantwatson"));
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => string.IsNullOrWhiteSpace(plaintext) ? string.Empty : $"enc::{plaintext}";

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("enc::", StringComparison.Ordinal) ? protectedValue["enc::".Length..] : protectedValue;
    }
}
