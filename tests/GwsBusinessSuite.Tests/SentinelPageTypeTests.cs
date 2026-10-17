using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelPageTypeTests
{
    private static readonly SentinelTypeField Stage = new("stage", "Stage", SentinelPageTypes.Kinds.Select, ["Lead", "Won"]);
    private static readonly SentinelTypeField Value = new("value", "Value", SentinelPageTypes.Kinds.Number, []);

    [Theory]
    [InlineData("number", "1,5", null)]
    [InlineData("number", "12.50", "12.5")]
    [InlineData("date", "2026-10-16", "2026-10-16")]
    [InlineData("date", "16/10/2026", null)]
    [InlineData("checkbox", "on", "true")]
    [InlineData("checkbox", "false", "")]
    [InlineData("url", "javascript:alert(1)", null)]
    [InlineData("url", "https://example.com/a b", "https://example.com/a%20b")]
    [InlineData("text", "  hi  ", "hi")]
    public void NormalizeValue_ShouldStoreAFixedForm_OrRefuse(string kind, string raw, string? expected)
    {
        var field = new SentinelTypeField("f", "Field", kind, []);
        if (expected is null)
        {
            FluentActions.Invoking(() => SentinelPageTypes.NormalizeValue(field, raw)).Should().Throw<ArgumentException>();
            return;
        }
        SentinelPageTypes.NormalizeValue(field, raw).Should().Be(expected.Length == 0 ? null : expected);
    }

    [Fact]
    public void NormalizeFields_ShouldRefuseDuplicatesAndUnknownKinds_AndKeepSelectOptionsUnique()
    {
        FluentActions.Invoking(() => SentinelPageTypes.NormalizeFields([new("", "A", "text", []), new("", "a", "text", [])])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => SentinelPageTypes.NormalizeFields([new("", "A", "formula", [])])).Should().Throw<ArgumentException>();

        var fields = SentinelPageTypes.NormalizeFields([new("", "Stage", "select", [" Lead", "lead", "Won", ""])]);

        fields[0].Options.Should().Equal("Lead", "Won");
        fields[0].Id.Should().NotBeNullOrWhiteSpace();
        SentinelPageTypes.NormalizeValue(fields[0], "won").Should().Be("Won");
        FluentActions.Invoking(() => SentinelPageTypes.NormalizeValue(fields[0], "Lost")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Pages_ShouldGainTheTypesFields_AndTheTypeShouldListFilterAndSortThem()
    {
        var (db, wiki, service) = await CreateAsync();
        var type = await service.SaveTypeAsync(new SentinelPageTypeEditor(null, "Client", "🏢", "", [Stage, Value]), "sam");
        var acme = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Acme" }, "grant");
        var globex = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Globex" }, "grant");
        var initech = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Initech" }, "grant");
        foreach (var page in new[] { acme, globex, initech }) await service.AssignAsync(page.Id, type.Id, "grant");
        await service.SetValueAsync(acme.Id, type.Id, "stage", "won", "grant");
        await service.SetValueAsync(acme.Id, type.Id, "value", "900", "grant");
        await service.SetValueAsync(globex.Id, type.Id, "stage", "Lead", "grant");
        await service.SetValueAsync(globex.Id, type.Id, "value", "1200", "grant");

        (await service.GetForPageAsync(acme.Id, "grant")).Single().Values.Should().Contain("stage", "Won");
        var byValue = await service.QueryAsync(type.Id, "grant", [], "value", descending: true);
        byValue.Select(row => row.Title).Should().Equal("Globex", "Acme", "Initech");
        var won = await service.QueryAsync(type.Id, "grant", [new SentinelTypeFilter("stage", SentinelPageTypes.Operators.Is, "Won")], null, false);
        won.Select(row => row.Title).Should().Equal("Acme");
        var big = await service.QueryAsync(type.Id, "grant", [new SentinelTypeFilter("value", SentinelPageTypes.Operators.Greater, "1000")], null, false);
        big.Select(row => row.Title).Should().Equal("Globex");
        var empty = await service.QueryAsync(type.Id, "grant", [new SentinelTypeFilter("stage", SentinelPageTypes.Operators.Empty, null)], null, false);
        empty.Select(row => row.Title).Should().Equal("Initech");

        // A trashed page leaves the list; removing the type from a page does too.
        await wiki.TrashPageAsync(initech.Id, "grant");
        await service.UnassignAsync(globex.Id, type.Id, "grant");
        (await service.QueryAsync(type.Id, "grant", [], null, false)).Select(row => row.Title).Should().Equal("Acme");
        (await service.ListTypesAsync("grant")).Single().PageCount.Should().Be(2);
    }

    [Fact]
    public async Task Access_ShouldNeedEditToChangeAPage_HideUnviewablePages_AndLimitTypeChangesToCreatorOrAdmin()
    {
        var (db, wiki, service) = await CreateAsync();
        var type = await service.SaveTypeAsync(new SentinelPageTypeEditor(null, "Client", "🏢", "", [Stage]), "sam");
        var page = await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Private client" }, "grant");
        await service.AssignAsync(page.Id, type.Id, "grant");

        await service.Invoking(s => s.SetValueAsync(page.Id, type.Id, "stage", "Won", "sam")).Should().ThrowAsync<ArgumentException>().WithMessage("*edit access*");
        (await service.QueryAsync(type.Id, "sam", [], null, false)).Should().BeEmpty();
        (await service.GetForPageAsync(page.Id, "sam")).Should().BeEmpty();

        await new SentinelAccessService(db).SetPermissionAsync(page.Id, false, "sam", SentinelAccessLevels.View, "grant");
        (await service.QueryAsync(type.Id, "sam", [], null, false)).Should().ContainSingle();

        db.AppUsers.Add(new AppUser { Username = "kim", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        await service.Invoking(s => s.SaveTypeAsync(new SentinelPageTypeEditor(type.Id, "Renamed", "🏢", "", [Stage]), "kim")).Should().ThrowAsync<ArgumentException>();
        await service.SaveTypeAsync(new SentinelPageTypeEditor(type.Id, "Customer", "🏢", "", [Stage, Value]), "grant");
        await service.Invoking(s => s.SaveTypeAsync(new SentinelPageTypeEditor(null, "customer", "x", "", []), "kim")).Should().ThrowAsync<ArgumentException>().WithMessage("*already*");

        await service.DeleteTypeAsync(type.Id, "sam");
        (await db.SentinelPageTypeAssignments.CountAsync()).Should().Be(0);
    }

    private static async Task<(ApplicationDbContext Db, WikiService Wiki, SentinelPageTypeService Service)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        return (db, new WikiService(db), new SentinelPageTypeService(db, new SentinelAccessService(db), TimeProvider.System));
    }
}
