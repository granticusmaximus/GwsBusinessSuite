using FluentAssertions;
using GwsBusinessSuite.Application.AppRegistry;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class AppRegistryServiceTests
{
    [Fact]
    public async Task SaveAppAsync_ShouldCreateUpdateAndDeleteApps()
    {
        await using var db = await CreateDbAsync();
        var service = new AppRegistryService(db);

        var created = await service.SaveAppAsync(new AppRegistryEditorModel
        {
            Name = "Public Site",
            AppType = "WebsiteCms",
            Subdomain = "public-site",
            Port = 5173,
            Status = "Active"
        });

        created.Name.Should().Be("Public Site");
        created.Status.Should().Be("Active");
        created.Port.Should().Be(5173);

        var loaded = await service.GetAppAsync(created.Id);
        loaded.Should().NotBeNull();
        loaded!.Subdomain.Should().Be("public-site");

        var updated = await service.SaveAppAsync(new AppRegistryEditorModel
        {
            AppId = created.Id,
            Name = "Public Site",
            AppType = "WebsiteCms",
            Subdomain = "public-site-live",
            Port = 5174,
            Status = "Paused"
        });

        updated.Id.Should().Be(created.Id);
        updated.Subdomain.Should().Be("public-site-live");
        updated.Status.Should().Be("Paused");

        await service.DeleteAppAsync(created.Id);

        (await service.ListAppsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAppAsync_ShouldNormalizeSubdomain()
    {
        await using var db = await CreateDbAsync();
        var service = new AppRegistryService(db);

        var created = await service.SaveAppAsync(new AppRegistryEditorModel
        {
            Name = "CRM",
            AppType = "InternalTool",
            Subdomain = "  CRM Portal.Main  "
        });

        created.Subdomain.Should().Be("crm-portal-main");
    }

    [Fact]
    public async Task SaveAppAsync_ShouldRejectDuplicateSubdomain_CaseInsensitive()
    {
        await using var db = await CreateDbAsync();
        var service = new AppRegistryService(db);

        await service.SaveAppAsync(new AppRegistryEditorModel
        {
            Name = "App One",
            AppType = "WebsiteCms",
            Subdomain = "public-site"
        });

        var action = async () => await service.SaveAppAsync(new AppRegistryEditorModel
        {
            Name = "App Two",
            AppType = "WebsiteCms",
            Subdomain = "Public-Site"
        });

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already in use*");
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
}
