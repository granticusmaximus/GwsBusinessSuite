using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelPagePreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.Zero);

    private static WikiBlock Block(string type, string text, int indent = 0, Dictionary<string, string>? props = null) =>
        new(Guid.NewGuid(), type, indent, text.Length == 0 ? [] : [new WikiRichTextSpan(text)], props ?? new Dictionary<string, string>());

    private static WikiPage Page(string title = "Plans", long? verifiedUntil = null) => new()
    {
        Title = title, Slug = title.ToLowerInvariant(), Icon = "🗺️", VerifiedUntilUnix = verifiedUntil,
        CreatedAt = Now.AddDays(-2), UpdatedAt = Now.AddHours(-1)
    };

    [Fact]
    public void Build_ShouldTurnTheFirstBlocksIntoPlainLines()
    {
        var blocks = new[]
        {
            Block(WikiBlockTypes.Heading1, "Launch   plan"),
            Block(WikiBlockTypes.Paragraph, ""),
            Block(WikiBlockTypes.BulletedListItem, "Book the venue", 1),
            Block(WikiBlockTypes.ToDo, "Send invites", props: new() { ["checked"] = "true" }),
            Block(WikiBlockTypes.Image, "", props: new() { ["caption"] = "Floor plan" }),
            Block(WikiBlockTypes.Divider, ""),
            Block(WikiBlockTypes.Quote, "<img src=x onerror=alert(1)>"),
            Block(WikiBlockTypes.Paragraph, "Not shown: past the line limit")
        };

        var preview = SentinelPagePreviewBuilder.Build(Page(verifiedUntil: Now.AddDays(5).ToUnixTimeSeconds()), blocks, Now);

        preview.Title.Should().Be("Plans");
        preview.Verified.Should().BeTrue();
        preview.Lines.Should().Equal(
            new SentinelPreviewLine("heading", "Launch plan"),
            new SentinelPreviewLine("bullet", "Book the venue", 1),
            new SentinelPreviewLine("todo", "Send invites", 0, true),
            new SentinelPreviewLine("media", "Image: Floor plan"),
            new SentinelPreviewLine("divider", ""),
            new SentinelPreviewLine("quote", "<img src=x onerror=alert(1)>"));
        preview.MoreBlocks.Should().Be(1);
        preview.UpdatedAt.Should().Be(Now.AddHours(-1));
    }

    [Fact]
    public void Build_ShouldCapLongTextAndReadLegacyMarkdown()
    {
        var longText = new string('a', 400);
        var preview = SentinelPagePreviewBuilder.Build(Page(verifiedUntil: Now.AddDays(-1).ToUnixTimeSeconds()),
            [Block(WikiBlockTypes.Markdown, "\n\n## Old notes\nMore"), Block(WikiBlockTypes.Paragraph, longText)], Now);

        preview.Verified.Should().BeFalse("its verification lapsed");
        preview.Lines[0].Should().Be(new SentinelPreviewLine("heading", "Old notes"));
        preview.Lines[1].Text.Should().HaveLength(SentinelPagePreviewBuilder.MaxLineChars).And.EndWith("…");
        SentinelPagePreviewBuilder.Build(Page(""), [], Now).Should().Match<SentinelPagePreview>(p => p.Title == "Untitled" && p.Lines.Count == 0);
    }

    [Fact]
    public async Task GetAsync_ShouldOnlyPreviewPagesTheUserCanView_AndNotTrashedOnes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        var open = Page("Open");
        open.BlocksJson = WikiBlockJson.Serialize([Block(WikiBlockTypes.Paragraph, "Hello")]);
        var secret = Page("Secret");
        var trashed = Page("Old");
        trashed.TrashedAt = Now;
        db.WikiPages.AddRange(open, secret, trashed);
        await db.SaveChangesAsync();
        // Access is deny-by-default for anyone who isn't an owner or admin: sam can see "Open" only.
        db.SentinelResourcePermissions.AddRange(
            new SentinelResourcePermission { TargetId = open.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View },
            new SentinelResourcePermission { TargetId = secret.Id, IsDatabase = false, Username = "grant", AccessLevel = SentinelAccessLevels.FullAccess });
        await db.SaveChangesAsync();
        var service = new SentinelPagePreviewService(db, new Clock(), new SentinelAccessService(db));

        (await service.GetAsync(open.Id, "sam"))!.Lines.Should().ContainSingle().Which.Text.Should().Be("Hello");
        (await service.GetAsync(secret.Id, "sam")).Should().BeNull("sam has no access to it");
        (await service.GetAsync(secret.Id, "grant")).Should().NotBeNull();
        (await service.GetAsync(trashed.Id, "grant")).Should().BeNull();
        (await service.GetAsync(Guid.NewGuid(), "grant")).Should().BeNull();
        (await service.GetAsync(open.Id, "")).Should().BeNull();
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
