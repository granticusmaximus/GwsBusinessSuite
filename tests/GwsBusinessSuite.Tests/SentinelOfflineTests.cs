using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelOfflineTests
{
    [Fact]
    public async Task KeptPages_ShouldComeBackAsRenderedCopies_OnlyWhileStillViewable()
    {
        var (db, wiki, service) = await CreateAsync();
        var block = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Heading2, 0, [new WikiRichTextSpan("Runbook <steps>")], new Dictionary<string, string>());
        var runbook = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Runbook", Icon = "📘", BlocksJson = WikiBlockJson.Serialize([block]) }, "grant");
        var secret = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Secret" }, "grant");
        var access = new SentinelAccessService(db);
        await access.SetPermissionAsync(runbook.Id, false, "sam", SentinelAccessLevels.View, "grant");

        await service.Invoking(s => s.SetKeptOfflineAsync(secret.Id, "sam", true)).Should().ThrowAsync<ArgumentException>();
        await service.SetKeptOfflineAsync(runbook.Id, "sam", true);
        await service.SetKeptOfflineAsync(runbook.Id, "sam", true);

        (await service.IsKeptOfflineAsync(runbook.Id, "sam")).Should().BeTrue();
        var copy = (await service.GetCopiesAsync("sam")).Single();
        copy.Title.Should().Be("Runbook");
        copy.Icon.Should().Be("📘");
        copy.Html.Should().Contain("Runbook &lt;steps&gt;").And.NotContain("<steps>");

        // Sharing removed, or the page trashed: the copy goes on the next sync.
        var permission = (await access.GetAccessAsync(runbook.Id, false)).Permissions.Single(p => p.Username == "sam");
        await access.RemovePermissionAsync(permission.Id, "grant");
        (await service.GetCopiesAsync("sam")).Should().BeEmpty();
        await service.SetKeptOfflineAsync(secret.Id, "grant", true);
        await wiki.TrashPageAsync(secret.Id, "grant");
        (await service.GetCopiesAsync("grant")).Should().BeEmpty();

        await service.SetKeptOfflineAsync(runbook.Id, "sam", false);
        (await service.IsKeptOfflineAsync(runbook.Id, "sam")).Should().BeFalse();
    }

    private static async Task<(ApplicationDbContext Db, WikiService Wiki, SentinelOfflineService Service)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        return (db, new WikiService(db), new SentinelOfflineService(db, new SentinelAccessService(db)));
    }
}
