using FluentAssertions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Deployments;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class DeploymentWorkspaceServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_ShouldSummarizeAppsTargetsAndFallbackReactPath()
    {
        await using var db = await CreateDbAsync();

        db.BusinessApps.AddRange(
            new BusinessApp
            {
                Name = "Public Site",
                AppType = "WebsiteCms",
                Status = "Active",
                Subdomain = "public-site",
                Port = 5173,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "tests"
            },
            new BusinessApp
            {
                Name = "Internal CRM",
                AppType = "LineOfBusiness",
                Status = "Draft",
                Subdomain = "crm",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "tests"
            });

        db.DeploymentTargets.Add(new DeploymentTarget
        {
            Provider = "DigitalOcean",
            Name = "Primary Droplet",
            Host = "droplet-1.example.net",
            Notes = "Main app host",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "tests"
        });

        await db.SaveChangesAsync();

        var repoRoot = CreateRepositoryFixture();

        try
        {
            var service = new DeploymentWorkspaceService(
                db,
                new FakeHostEnvironment
                {
                    ContentRootPath = Path.Combine(repoRoot, "src", "GwsBusinessSuite.Web"),
                    ApplicationName = "Tests",
                    EnvironmentName = Environments.Development,
                    ContentRootFileProvider = new NullFileProvider()
                },
                Options.Create(new CmsBuilderOptions
                {
                    ReactAppRelativePath = "missing/public-site"
                }),
                Options.Create(new ExternalServicesOptions
                {
                    BaseDomain = "gwsapp.net",
                    CloudflareTunnelId = "tunnel-123"
                }));

            var snapshot = await service.GetSnapshotAsync();

            snapshot.ReactAppRelativePath.Should().Be("apps/public-site");
            snapshot.HasDockerfile.Should().BeTrue();
            snapshot.HasDockerComposeFile.Should().BeTrue();
            snapshot.Summary.TotalApps.Should().Be(2);
            snapshot.Summary.ActiveApps.Should().Be(1);
            snapshot.Summary.RouteReadyApps.Should().Be(1);
            snapshot.Summary.AppsMissingPort.Should().Be(1);
            snapshot.Apps.Should().ContainSingle(app => app.Name == "Public Site" && app.ReadinessLabel == "Ready");
            snapshot.Apps.Should().ContainSingle(app => app.Name == "Internal CRM" && app.ReadinessLabel == "Missing Port");
            snapshot.Targets.Should().ContainSingle(target => target.Provider == "DigitalOcean");
        }
        finally
        {
            Directory.Delete(repoRoot, recursive: true);
        }
    }

    private static string CreateRepositoryFixture()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "gws-deployments-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src", "GwsBusinessSuite.Web"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "apps", "public-site"));
        File.WriteAllText(Path.Combine(repoRoot, "Dockerfile"), "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
        File.WriteAllText(Path.Combine(repoRoot, "docker-compose.yml"), "services:\n  web:\n    build: .");
        return repoRoot;
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

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
