using System.Net;
using System.Text;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Application.DockerHealth;
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

    [Fact]
    public async Task ListActiveDropletsWithImagesAsync_ShouldFilterToActiveDroplets_AndIncludeBaseAndLocalImages()
    {
        await using var db = await CreateDbAsync();
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"droplets":[
                  {"id":101,"name":"gws-web","status":"active","size_slug":"s-2vcpu-4gb","vcpus":2,
                   "region":{"name":"NYC1","slug":"nyc1"},
                   "size":{"memory":4096,"disk":80},
                   "image":{"id":1,"name":"24.04 (LTS) x64","distribution":"Ubuntu","slug":"ubuntu-24-04-x64","type":"base"},
                   "networks":{"v4":[{"ip_address":"203.0.113.5","type":"public"},{"ip_address":"10.10.0.2","type":"private"}]}},
                  {"id":102,"name":"gws-ai","status":"active","size_slug":"s-4vcpu-8gb","vcpus":4,
                   "region":{"name":"NYC1","slug":"nyc1"},
                   "size":{"memory":8192,"disk":160},
                   "image":{"id":2,"name":"ai-snapshot-1","distribution":"Ubuntu","slug":null,"type":"snapshot"},
                   "networks":{"v4":[{"ip_address":"10.10.0.3","type":"private"}]}},
                  {"id":103,"name":"gws-old","status":"off","size_slug":"s-1vcpu-1gb","vcpus":1,
                   "region":{"name":"NYC1","slug":"nyc1"},
                   "size":{"memory":1024,"disk":25},
                   "image":{"id":3,"name":"22.04","distribution":"Ubuntu","slug":"ubuntu-22-04-x64","type":"base"},
                   "networks":{"v4":[]}}
                ]}
                """, Encoding.UTF8, "application/json")
        });

        var dockerHealth = new StubDockerHealthService(new ContainerListResult(true,
        [
            new ContainerHealthView { Name = "gwssuite", Image = "gwssuite-web:latest", State = "running", Status = "Up 2 hours" },
            new ContainerHealthView { Name = "stopped-job", Image = "busybox:latest", State = "exited", Status = "Exited (0)" }
        ]));

        var service = CreateService(db, handler, dockerHealth);
        await service.SaveSettingsAsync(new DigitalOceanApiSettingsInput { NewApiToken = "dop_v1_secret", DropletId = "101" });

        var result = await service.ListActiveDropletsWithImagesAsync();

        result.Available.Should().BeTrue();
        result.Droplets.Should().HaveCount(2);

        var ai = result.Droplets[0];
        ai.Name.Should().Be("gws-ai");
        ai.IsCurrentHost.Should().BeFalse();
        ai.BaseImageName.Should().Be("Ubuntu ai-snapshot-1");
        ai.BaseImageType.Should().Be("snapshot");
        ai.PrivateIpAddress.Should().Be("10.10.0.3");
        ai.ContainerProbeStatus.Should().Contain("SSH private key");

        var web = result.Droplets[1];
        web.Name.Should().Be("gws-web");
        web.IsCurrentHost.Should().BeTrue();
        web.BaseImageName.Should().Be("Ubuntu 24.04 (LTS) x64");
        web.BaseImageSlug.Should().Be("ubuntu-24-04-x64");
        web.ActiveContainerImages.Should().ContainSingle();
        web.ActiveContainerImages[0].ContainerName.Should().Be("gwssuite");
        web.ActiveContainerImages[0].Image.Should().Be("gwssuite-web:latest");
    }

    [Fact]
    public void ParseDockerPsOutput_ShouldParseTabDelimitedLines_AndSortByName()
    {
        var output = "searxng\tsearxng/searxng:latest\trunning\tUp 3 days\r\ngwssuite\tgwssuite-web:local\trunning\tUp 1 hour\n\n";

        var parsed = DigitalOceanService.ParseDockerPsOutput(output);

        parsed.Should().HaveCount(2);
        parsed[0].ContainerName.Should().Be("gwssuite");
        parsed[0].Image.Should().Be("gwssuite-web:local");
        parsed[1].ContainerName.Should().Be("searxng");
        parsed[1].Image.Should().Be("searxng/searxng:latest");
    }

    private static DigitalOceanService CreateService(
        ApplicationDbContext db,
        HttpMessageHandler handler,
        IDockerHealthService? dockerHealth = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.digitalocean.com/v2/") };
        return new DigitalOceanService(
            client,
            db,
            new FakeSecretProtector(),
            NullLogger<DigitalOceanService>.Instance,
            new FixedCurrentUserAccessor("grantwatson"),
            dockerHealth);
    }

    private sealed class StubDockerHealthService(ContainerListResult listResult) : IDockerHealthService
    {
        public Task<ContainerListResult> ListContainersAsync(CancellationToken cancellationToken = default) => Task.FromResult(listResult);
        public Task<ContainerDetailView?> GetContainerDetailsAsync(string containerName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DockerHealthAlertView>> ListAlertsAsync(bool unreadOnly, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task MarkAlertReadAsync(Guid alertId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task MarkAllAlertsReadAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<int>> CountAlertsByDayAsync(DayRange range, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CountUnreadAlertsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> StartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> StopContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> RestartContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> RemoveContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> PullImageAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> RecreateContainerAsync(string containerName, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DockerActionResult> ExecCommandAsync(string containerName, string command, string performedBy, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DockerActionLogView>> ListActionLogsAsync(string? containerName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
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
