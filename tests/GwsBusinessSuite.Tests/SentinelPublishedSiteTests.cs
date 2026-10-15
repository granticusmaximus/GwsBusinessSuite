using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using GwsBusinessSuite.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel item 30: publish a page tree as a mini-site.
public sealed class SentinelPublishedSiteTests
{
    private static SentinelPublishedSite.Node Node(Guid id, Guid? parent, int order, string title, bool trashed = false, bool shared = false) =>
        new(id, parent, order, title, null, trashed, shared);

    [Fact]
    public void Build_ShouldWalkTheTreeInSidebarOrder_AndHideTrashedAndSeparatelySharedPages()
    {
        Guid root = Guid.NewGuid(), guide = Guid.NewGuid(), setup = Guid.NewGuid(), faq = Guid.NewGuid(),
            privateNotes = Guid.NewGuid(), underPrivate = Guid.NewGuid(), old = Guid.NewGuid(), elsewhere = Guid.NewGuid();
        var site = SentinelPublishedSite.Build(root,
        [
            Node(root, null, 0, "Handbook"),
            Node(faq, root, 2, "FAQ"),
            Node(guide, root, 1, "Guide"),
            Node(setup, guide, 0, "Setup"),
            Node(privateNotes, root, 3, "Private notes", shared: true),
            Node(underPrivate, privateNotes, 0, "Salaries"),
            Node(old, root, 4, "Old page", trashed: true),
            Node(elsewhere, null, 0, "Another root")
        ], siteTitle: null)!;

        site.Title.Should().Be("Handbook");
        site.Pages.Select(page => (page.Title, page.Depth)).Should().Equal(("Handbook", 0), ("Guide", 1), ("Setup", 2), ("FAQ", 1));
        site.HiddenTitles.Should().Equal("Private notes");
        site.Contains(underPrivate).Should().BeFalse("everything under a separately shared page stays hidden too");
        site.Contains(elsewhere).Should().BeFalse();

        SentinelPublishedSite.Build(root, [Node(root, null, 0, "Handbook")], "  Team handbook ")!.Title.Should().Be("Team handbook");
        SentinelPublishedSite.Build(root, [Node(root, null, 0, "Handbook", trashed: true)], null).Should().BeNull();
    }

    [Fact]
    public void Build_ShouldSurviveAParentLoop()
    {
        Guid root = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid();
        var site = SentinelPublishedSite.Build(root, [Node(root, b, 0, "Root"), Node(a, root, 0, "A"), Node(b, a, 0, "B")], null)!;
        site.Pages.Select(page => page.Title).Should().Equal("Root", "A", "B");
    }

    [Fact]
    public void RewriteLinks_ShouldPointPublishedPagesAtTheSite_AndUnlinkEverythingElse()
    {
        Guid root = Guid.NewGuid(), child = Guid.NewGuid(), outside = Guid.NewGuid();
        var site = SentinelPublishedSite.Build(root, [Node(root, null, 0, "Home"), Node(child, root, 0, "Child")], null)!;
        var html = $"<p><a href=\"wikilink:{child}\">Child</a>, <a href=\"wikilink:{root}\"><b>Home</b></a>, " +
                   $"<a href=\"wikilink:{outside}\">Secret</a>, <a href=\"https://example.com\" target=\"_blank\">web</a></p>";

        var rewritten = SentinelPublishedSite.RewriteLinks(html, site, "tok");

        rewritten.Should().Contain($"<a href=\"/sentinel/share/tok/{child}\">Child</a>")
            .And.Contain("<a href=\"/sentinel/share/tok\"><b>Home</b></a>")
            .And.Contain("<span class=\"sentinel-public-unlinked\">Secret</span>")
            .And.Contain("<a href=\"https://example.com\" target=\"_blank\">web</a>")
            .And.NotContain("wikilink:");
    }

    [Fact]
    public void Search_ShouldRankTitleMatchesFirst_WithASnippetAroundTheMatch()
    {
        Guid root = Guid.NewGuid(), pricing = Guid.NewGuid(), faq = Guid.NewGuid();
        var site = SentinelPublishedSite.Build(root, [Node(root, null, 0, "Home"), Node(faq, root, 0, "FAQ"), Node(pricing, root, 1, "Pricing")], null)!;
        var texts = new Dictionary<Guid, string>
        {
            [root] = "Welcome.",
            [faq] = new string('x', 100) + " How does pricing work? It depends on the plan you pick and how many seats.",
            [pricing] = "Plans and prices."
        };

        var results = SentinelPublishedSite.Search(site, texts, "pricing");

        results.Select(result => result.Title).Should().Equal("Pricing", "FAQ");
        results[1].Snippet.Should().StartWith("…").And.Contain("How does pricing work?");
        SentinelPublishedSite.Search(site, texts, "p").Should().BeEmpty("one letter matches nearly everything");
    }

    private static async Task<ApplicationDbContext> DbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static WikiPage Page(ApplicationDbContext db, string title, Guid? parent, string blocksJson = "[]")
    {
        var page = new WikiPage { Title = title, Slug = $"{title.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}", ParentWikiPageId = parent, BlocksJson = blocksJson, CreatedBy = "grant" };
        db.WikiPages.Add(page);
        return page;
    }

    [Fact]
    public async Task Service_ShouldPublishTheTreeForASiteShare_AndOnlyThePageOtherwise()
    {
        await using var db = await DbAsync();
        var access = new SentinelAccessService(db);
        var service = new SentinelPublishedSiteService(db);
        var root = Page(db, "Handbook", null);
        var live = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Business, 0, [],
            new Dictionary<string, string> { ["kind"] = SentinelLiveBlockKinds.Contact, ["recordLabel"] = "Sam Secret" });
        var text = new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, [new WikiRichTextSpan("Onboarding checklist for Sam")], new Dictionary<string, string>());
        var child = Page(db, "Onboarding", root.Id, WikiBlockJson.Serialize([text, live]));
        var restricted = Page(db, "Pay bands", root.Id);
        await db.SaveChangesAsync();
        await access.SetPermissionAsync(restricted.Id, false, "pat", SentinelAccessLevels.View, "grant");

        var siteShare = await access.CreatePublicShareAsync(root.Id, false, null, false, null, "grant", includeSubpages: true, siteTitle: "Team handbook");
        var resolved = (await access.ResolvePublicShareAsync(siteShare.PublicToken!))!;
        resolved.IncludeSubpages.Should().BeTrue();
        resolved.SiteTitle.Should().Be("Team handbook");

        var site = (await service.GetSiteAsync(resolved))!;
        site.Title.Should().Be("Team handbook");
        site.Pages.Select(page => page.Title).Should().Equal("Handbook", "Onboarding");
        site.HiddenTitles.Should().Equal("Pay bands");

        (await service.SearchAsync(site, "checklist")).Should().ContainSingle().Which.Id.Should().Be(child.Id);
        (await service.SearchAsync(site, "secret")).Should().BeEmpty("live business block labels are never published");

        var pageShare = await access.CreatePublicShareAsync(root.Id, false, null, false, null, "grant", siteTitle: "ignored");
        var single = (await service.GetSiteAsync((await access.ResolvePublicShareAsync(pageShare.PublicToken!))!))!;
        single.Pages.Should().ContainSingle().Which.Id.Should().Be(root.Id);
        single.Title.Should().Be("Handbook");

        var databaseShare = await access.CreatePublicShareAsync(Guid.NewGuid(), true, null, false, null, "grant", includeSubpages: true);
        databaseShare.IncludeSubpages.Should().BeFalse("a database share is a single view");
    }

    [Fact]
    public void UnlockCookie_ShouldOnlyUnlockTheShareItWasIssuedFor()
    {
        var protection = new EphemeralDataProtectionProvider();
        var shareId = Guid.NewGuid();
        var response = new DefaultHttpContext();
        response.Request.IsHttps = true;

        SentinelShareUnlock.Grant(response, protection, "tok", shareId);

        var setCookie = response.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain("path=/sentinel/share/tok").And.Contain("httponly").And.Contain("secure");
        var value = setCookie.Split(';')[0].Split('=', 2)[1];
        var next = new DefaultHttpContext();
        next.Request.Headers.Cookie = $"{SentinelShareUnlock.CookieName(shareId)}={value}";
        SentinelShareUnlock.IsUnlocked(next, protection, shareId).Should().BeTrue();

        var other = Guid.NewGuid();
        var forged = new DefaultHttpContext();
        forged.Request.Headers.Cookie = $"{SentinelShareUnlock.CookieName(other)}={value}";
        SentinelShareUnlock.IsUnlocked(forged, protection, other).Should().BeFalse("a cookie for one share doesn't open another");
        var tampered = new DefaultHttpContext();
        tampered.Request.Headers.Cookie = $"{SentinelShareUnlock.CookieName(shareId)}=not-a-real-value";
        SentinelShareUnlock.IsUnlocked(tampered, protection, shareId).Should().BeFalse();
        SentinelShareUnlock.IsUnlocked(new DefaultHttpContext(), protection, shareId).Should().BeFalse();
    }
}
