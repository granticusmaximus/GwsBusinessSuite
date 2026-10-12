using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class WikiPageAliasTests
{
    private static WikiPage Page(string title, params string[] aliases) => new()
    {
        Title = title, Slug = title.ToLowerInvariant().Replace(' ', '-') + Guid.NewGuid().ToString("N")[..6],
        Aliases = WikiPageAliases.Serialize(aliases)
    };

    [Fact]
    public void Normalize_ShouldTidyNames_DropTheTitleRepeatsAndLinkSyntax_AndCap()
    {
        WikiPageAliases.Normalize(["  GW ", "gw", "Grant   Watson", "[[G|X]]", "", null, new string('x', 80)], "Grant Watson")
            .Should().Equal("GW", "GX", new string('x', WikiPageAliases.MaxLength));
        WikiPageAliases.Normalize(Enumerable.Range(1, 15).Select(i => $"n{i}"), "t").Should().HaveCount(WikiPageAliases.MaxAliases);
        WikiPageAliases.SplitInput("GW, Grant; G. Watson\nGWatson").Should().Equal("GW", "Grant", "G. Watson", "GWatson");
    }

    [Fact]
    public void Resolve_ShouldPreferATitle_ThenAUniqueAlias()
    {
        var grant = Page("Grant Watson", "GW", "Boss");
        var team = Page("GW", "Team");
        var other = Page("Other", "Boss");
        IReadOnlyList<WikiPage> pages = [grant, team, other];

        WikiPageAliases.Resolve("gw", pages).Should().BeSameAs(team, "a page titled the name wins over an alias");
        WikiPageAliases.Resolve("team", pages).Should().BeSameAs(team);
        WikiPageAliases.Resolve("Boss", pages).Should().BeNull("two pages share that alias");
        WikiPageAliases.Resolve("nobody", pages).Should().BeNull();
    }

    [Fact]
    public void ResolveWikiLinks_ShouldLinkAnAliasToItsPage()
    {
        var grant = Page("Grant Watson", "GW");

        WikiMarkdownHelper.ResolveWikiLinks("Ask [[GW]] first.", [grant]).Should().Be($"Ask [GW](wikilink:{grant.Id}) first.");
        WikiMarkdownHelper.ResolveWikiLinks("[[Nope]]", [grant]).Should().Be("*Nope* _(wiki page not found)_");
    }

    [Fact]
    public void SearchLinkSuggestions_ShouldMatchAliases_BestMatchFirst()
    {
        var grant = Page("Grant Watson", "GW");
        var gwsSuite = Page("GWS Suite");
        var meeting = Page("Weekly sync", "Standup");

        var results = WikiMarkdownHelper.SearchLinkSuggestions("gw", [gwsSuite, meeting, grant]);

        results.Select(r => (r.Title, r.Alias)).Should().Equal(("Grant Watson", "GW"), ("GWS Suite", null));
        WikiMarkdownHelper.SearchLinkSuggestions("stand", [gwsSuite, meeting, grant]).Should().ContainSingle()
            .Which.Alias.Should().Be("Standup");
        WikiMarkdownHelper.SearchLinkSuggestions("", [gwsSuite, meeting, grant]).Should().HaveCount(3);
    }

    [Fact]
    public async Task Service_ShouldNeedEditAccess_SaveTidiedAliases_AndWarnAboutSharedNames()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        var grant = Page("Grant Watson");
        var team = Page("Team", "Boss");
        db.WikiPages.AddRange(grant, team);
        await db.SaveChangesAsync();
        db.SentinelResourcePermissions.Add(new SentinelResourcePermission { TargetId = grant.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View });
        await db.SaveChangesAsync();
        var service = new SentinelPageAliasService(db, TimeProvider.System, new SentinelAccessService(db));

        await service.Invoking(s => s.SetAsync(grant.Id, ["GW"], "sam")).Should().ThrowAsync<UnauthorizedAccessException>("view access isn't enough");
        var result = await service.SetAsync(grant.Id, [" GW ", "boss", "Grant Watson"], "grant");

        result.Aliases.Should().Equal("GW", "boss");
        result.SharedWith.Should().Equal("boss (also \"Team\")");
        (await service.GetAsync(grant.Id)).Should().Equal("GW", "boss");
        await service.Invoking(s => s.SetAsync(Guid.NewGuid(), ["x"], "grant")).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task WorkspaceSearchAndBacklinks_ShouldUseAliases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin });
        var grant = Page("Grant Watson", "Gizmo");
        var notes = Page("Old notes");
        notes.BlocksJson = WikiBlockJson.Serialize([new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Markdown, 0, [],
            new Dictionary<string, string> { ["content"] = "Talk to [[Gizmo]] about it." })]);
        db.WikiPages.AddRange(grant, notes);
        await db.SaveChangesAsync();
        var workspace = new SentinelWorkspaceService(db, TimeProvider.System, new SentinelAccessService(db));

        (await workspace.SearchAsync("gizmo", "grant")).Select(r => r.Title).Should().Contain("Grant Watson");
        (await workspace.GetBacklinksAsync(grant.Id, "grant")).Should().ContainSingle(b => b.SourcePageId == notes.Id);
    }
}
