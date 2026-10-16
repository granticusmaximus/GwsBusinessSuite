using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelWebClipTests
{
    private static readonly Uri Source = new("https://example.com/blog/post");

    private const string ArticleHtml = """
        <html><head><title>How we ship</title><script>alert(1)</script></head><body>
        <nav><a href="/">Home</a> <a href="/about">About</a></nav>
        <article>
          <h1>How we ship</h1>
          <p>We ship <strong>small</strong> changes every day, and we write about it so the whole team can see the reasoning behind every release decision we make.</p>
          <p>Read the <a href="/guide">guide</a> or <a href="javascript:alert(1)">this</a>. Each step keeps the release reversible and the review short, which matters more than speed.</p>
          <h2>Steps</h2>
          <ul><li>Plan<ul><li>Write it down</li></ul></li><li>Build</li></ul>
          <p><img src="/img/chart.png" alt="Chart"></p>
          <pre><code class="language-bash">./scripts/verify-release.sh</code></pre>
          <p>Every change runs the full suite before it merges, so a broken build never reaches the main branch or production.</p>
        </article>
        <footer>Copyright</footer>
        </body></html>
        """;

    [Fact]
    public void Converter_ShouldKeepStructure_ResolveUrls_AndDropUnsafeLinks()
    {
        var markdown = WebClipMarkdownConverter.Convert(ArticleHtml, Source);

        markdown.Should().Contain("# How we ship");
        markdown.Should().Contain("We ship **small** changes");
        markdown.Should().Contain("[guide](https://example.com/guide)");
        markdown.Should().NotContain("javascript:").And.NotContain("alert(1)");
        markdown.Should().Contain("## Steps");
        markdown.Should().Contain("- Plan\n    - Write it down\n- Build");
        markdown.Should().Contain("![Chart](https://example.com/img/chart.png)");
        markdown.Should().Contain("```bash\n./scripts/verify-release.sh\n```");
    }

    [Fact]
    public void Converter_ShouldEscapeMarkdownInText_AndConvertTables()
    {
        var markdown = WebClipMarkdownConverter.Convert(
            "<p>- not a list *nor* [a link]</p><table><tr><th>Name</th><th>Qty</th></tr><tr><td>A|B</td><td>2</td></tr></table>",
            Source);

        var blocks = WikiBlockJson.FromMarkdown(markdown);
        blocks[0].Type.Should().Be(WikiBlockTypes.Paragraph);
        string.Concat(blocks[0].RichText.Select(span => span.Text)).Should().Be("- not a list *nor* [a link]");
        blocks.Should().Contain(block => block.Type == WikiBlockTypes.Table);
    }

    [Fact]
    public async Task Tokens_ShouldBeShownOnce_StoredHashed_AndStopWorkingWhenRevoked()
    {
        var (db, service) = await CreateAsync();

        var created = await service.CreateTokenAsync("grant", "Chrome");

        created.Token.Should().StartWith(SentinelWebClips.TokenPrefix);
        var row = await db.SentinelClipTokens.SingleAsync();
        row.TokenHash.Should().NotContain(created.Token).And.HaveLength(64);
        (await service.ResolveTokenAsync(created.Token)).Should().Be("grant");
        (await db.SentinelClipTokens.AsNoTracking().SingleAsync()).LastUsedAtUnix.Should().NotBeNull();
        (await service.ResolveTokenAsync(created.Token + "x")).Should().BeNull();

        await service.RevokeTokenAsync(created.View.Id, "grant");

        (await service.ResolveTokenAsync(created.Token)).Should().BeNull();
        (await service.ListTokensAsync("grant")).Should().BeEmpty();
    }

    [Fact]
    public async Task Tokens_ShouldBeRevocableOnlyByTheirOwner_AndDieWithTheAccount()
    {
        var (db, service) = await CreateAsync();
        var created = await service.CreateTokenAsync("sam", "Safari");

        await service.Invoking(s => s.RevokeTokenAsync(created.View.Id, "grant")).Should().ThrowAsync<SentinelClipException>();

        var sam = await db.AppUsers.SingleAsync(user => user.Username == "sam");
        sam.IsActive = false;
        await db.SaveChangesAsync();
        (await service.ResolveTokenAsync(created.Token)).Should().BeNull();
    }

    [Fact]
    public async Task ClipArticle_ShouldCreateAPageUnderWebClips_WithSourceNoteAndTags()
    {
        var (db, service) = await CreateAsync();

        var result = await service.ClipAsync("grant", new SentinelClipRequest(
            "article", Source.AbsoluteUri, null, ArticleHtml, "Worth reading", "page", ["research", "#client/acme", "bad tag!"]));

        var page = await db.WikiPages.SingleAsync(p => p.Id == result.PageId);
        var folder = await db.WikiPages.SingleAsync(p => p.SystemKey == SentinelWebClips.FolderSystemKey);
        page.ParentWikiPageId.Should().Be(folder.Id);
        page.Title.Should().Be("How we ship");
        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
        var text = string.Join("\n", blocks.Select(block => string.Concat(block.RichText.Select(span => span.Text))));
        blocks[0].Type.Should().Be(WikiBlockTypes.Quote);
        blocks[0].RichText.Should().Contain(span => span.Link == Source.AbsoluteUri);
        text.Should().Contain("Worth reading").And.Contain("#research #client/acme").And.Contain("We ship small changes");
        text.Should().NotContain("Copyright").And.NotContain("bad");
        blocks.Should().Contain(block => block.Type == WikiBlockTypes.Code);
    }

    [Fact]
    public async Task ClipSelectionAndLink_ShouldUseTheGivenTitle_AndTodayAppendsToTheDailyNote()
    {
        var (db, service) = await CreateAsync();

        var selection = await service.ClipAsync("grant", new SentinelClipRequest(
            "selection", Source.AbsoluteUri, "A quote", "<p>Only <em>this</em> part.</p>", null, "page", null));
        var link = await service.ClipAsync("grant", new SentinelClipRequest(
            "link", Source.AbsoluteUri, "", null, null, "today", null));

        (await db.WikiPages.SingleAsync(p => p.Id == selection.PageId)).BlocksJson.Should().Contain("this");
        link.Target.Should().Be(SentinelWebClips.Targets.Today);
        var day = await db.WikiPages.SingleAsync(p => p.Id == link.PageId);
        day.SystemKey.Should().StartWith("daily:");
        day.BlocksJson.Should().Contain("example.com");
    }

    [Theory]
    [InlineData("article", "javascript:alert(1)", "<p>x</p>")]
    [InlineData("article", "file:///etc/passwd", "<p>x</p>")]
    [InlineData("selection", "https://example.com/", "")]
    [InlineData("article", "https://example.com/", "")]
    [InlineData("screenshot", "https://example.com/", "<p>x</p>")]
    public async Task Clip_ShouldRejectBadInput(string mode, string url, string html)
    {
        var (_, service) = await CreateAsync();

        await service.Invoking(s => s.ClipAsync("grant", new SentinelClipRequest(mode, url, "T", html, null, "page", null)))
            .Should().ThrowAsync<SentinelClipException>();
    }

    [Fact]
    public async Task Clip_ShouldNeedEditOnWebClips_ForANonAdmin()
    {
        var (_, service) = await CreateAsync();
        var request = new SentinelClipRequest("link", Source.AbsoluteUri, "Link", null, null, "page", null);

        // Only an admin creates the folder.
        await service.Invoking(s => s.ClipAsync("sam", request)).Should().ThrowAsync<SentinelClipException>().WithMessage("*admin*");
        var adminClip = await service.ClipAsync("grant", request);
        // Then a member needs Edit on it.
        await service.Invoking(s => s.ClipAsync("sam", request)).Should().ThrowAsync<SentinelClipException>().WithMessage("*edit access*");

        adminClip.PageId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Clip_ShouldWorkForAMemberWithEditOnWebClips()
    {
        var (db, service) = await CreateAsync();
        var request = new SentinelClipRequest("link", Source.AbsoluteUri, "Link", null, null, "page", null);
        await service.ClipAsync("grant", request);
        var folder = await db.WikiPages.SingleAsync(p => p.SystemKey == SentinelWebClips.FolderSystemKey);
        await new SentinelAccessService(db).SetPermissionAsync(folder.Id, false, "sam", SentinelAccessLevels.Edit, "grant");

        var clip = await service.ClipAsync("sam", request);

        (await db.WikiPages.SingleAsync(p => p.Id == clip.PageId)).ParentWikiPageId.Should().Be(folder.Id);
    }

    private static async Task<(ApplicationDbContext Db, SentinelWebClipService Service)> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        await db.SaveChangesAsync();
        var wiki = new WikiService(db);
        return (db, new SentinelWebClipService(db, wiki, new DailyNoteService(db, wiki, TimeProvider.System), new SentinelAccessService(db), TimeProvider.System));
    }
}
