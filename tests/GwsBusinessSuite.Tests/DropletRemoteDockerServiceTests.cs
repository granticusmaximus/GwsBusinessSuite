using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.DigitalOcean;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class DropletRemoteDockerServiceTests
{
    [Theory]
    [InlineData("gwssuite", true)]
    [InlineData("docker.io/searxng/searxng:latest", true)]
    [InlineData("coturn/coturn:4.14.0-r0", true)]
    [InlineData("gwssuite-web_1.0", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("foo; rm -rf /", false)]
    [InlineData("foo && whoami", false)]
    [InlineData("$(id)", false)]
    [InlineData("foo`bar`", false)]
    public void DropletDockerIdentifier_IsValid_ShouldRejectShellMetacharacters(string input, bool expected)
    {
        DropletDockerIdentifier.IsValid(input).Should().Be(expected);
    }

    [Fact]
    public void ParseDockerImagesOutput_ShouldParseTabDelimitedOutput_AndOrderNonDanglingFirst()
    {
        var output = string.Join('\n',
        [
            "<none>\t<none>\tabc123\t120MB\t2026-11-20 10:00:00 +0000 UTC",
            "docker.io/searxng/searxng\tlatest\tdef456\t410MB\t2026-11-19 08:00:00 +0000 UTC",
            "gwssuite-web\trollback\tghi789\t520MB\t2026-11-21 09:00:00 +0000 UTC"
        ]);

        var images = DropletRemoteDockerService.ParseDockerImagesOutput(output);

        images.Should().HaveCount(3);
        images[0].FullName.Should().Be("docker.io/searxng/searxng:latest");
        images[0].IsDangling.Should().BeFalse();
        images[1].FullName.Should().Be("gwssuite-web:rollback");
        images[2].IsDangling.Should().BeTrue();
    }

    [Fact]
    public void ParseDockerSystemDfOutput_ShouldParseAllResourceRows()
    {
        var output = string.Join('\n',
        [
            "Images\t4\t2\t1.85GB\t910MB (49%)",
            "Containers\t2\t2\t12.4MB\t0B (0%)",
            "Local Volumes\t2\t2\t340MB\t0B (0%)",
            "Build Cache\t12\t0\t620MB\t620MB"
        ]);

        var rows = DropletRemoteDockerService.ParseDockerSystemDfOutput(output);

        rows.Should().HaveCount(4);
        rows[0].Type.Should().Be("Images");
        rows[0].TotalCount.Should().Be("4");
        rows[0].ActiveCount.Should().Be("2");
        rows[0].Size.Should().Be("1.85GB");
        rows[0].Reclaimable.Should().Be("910MB (49%)");
    }

    [Fact]
    public void ParseAiRuntimeOutput_ShouldParseInstalledOllamaModelsAndServiceStates()
    {
        var output = string.Join('\n',
        [
            "INSTALLED=1",
            "OLLAMA_SVC=active",
            "WATCHDOG_TMR=active",
            "NAME                       ID              SIZE      MODIFIED",
            "sentinelgpt:latest         1111aaaa2222    2.0 GB    2 days ago",
            "llama3.2:latest            3333bbbb4444    2.0 GB    2 days ago",
            "embeddinggemma:latest      5555cccc6666    621 MB    2 days ago"
        ]);

        var runtime = DropletRemoteDockerService.ParseAiRuntimeOutput(output);

        runtime.Installed.Should().BeTrue();
        runtime.OllamaServiceState.Should().Be("active");
        runtime.WatchdogTimerState.Should().Be("active");
        runtime.Models.Should().HaveCount(3);
        runtime.Models[0].Name.Should().Be("sentinelgpt:latest");
        runtime.Models[0].Size.Should().Be("2.0 GB");
        runtime.Models[2].Name.Should().Be("embeddinggemma:latest");
    }

    [Fact]
    public void ParseAiRuntimeOutput_ShouldHandleNotInstalled()
    {
        var runtime = DropletRemoteDockerService.ParseAiRuntimeOutput("INSTALLED=0\n");

        runtime.Installed.Should().BeFalse();
        runtime.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task InspectDropletAsync_ShouldReportMissingSshKey_WhenNoneSaved()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var droplet = new ActiveDropletView { Id = 102, Name = "gws-ai", PublicIpAddress = "203.0.113.10" };

        var result = await service.InspectDropletAsync(droplet);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No SSH private key saved");
    }

    [Fact]
    public async Task PullImageAsync_ShouldRejectInvalidIdentifier_BeforeConnecting()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var droplet = new ActiveDropletView { Id = 102, Name = "gws-ai", PublicIpAddress = "203.0.113.10" };

        var result = await service.PullImageAsync(droplet, "bad;rm -rf /", "grantwatson");

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Invalid image reference");
    }

    [Fact]
    public async Task TailContainerLogsAsync_ShouldRejectInvalidIdentifier_BeforeConnecting()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db);
        var droplet = new ActiveDropletView { Id = 102, Name = "gws-ai", PublicIpAddress = "203.0.113.10" };

        var result = await service.TailContainerLogsAsync(droplet, "$(whoami)");

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Invalid container name");
    }

    private static DropletRemoteDockerService CreateService(ApplicationDbContext db) =>
        new(db, new FakeSecretProtector(), NullLogger<DropletRemoteDockerService>.Instance);

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

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => string.IsNullOrWhiteSpace(plaintext) ? string.Empty : $"enc::{plaintext}";
        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("enc::", StringComparison.Ordinal) ? protectedValue["enc::".Length..] : protectedValue;
    }
}
