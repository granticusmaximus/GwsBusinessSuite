using FluentAssertions;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Sentinel item 3: related notes + unlinked mentions.
public sealed class SentinelConnectionsTests
{
    private static WikiBlock Line(params WikiRichTextSpan[] spans) =>
        new(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0, spans, new Dictionary<string, string>());

    [Fact]
    public void Mentions_ShouldMatchWholeUnlinkedWords_AndLinkTheFirstKeepingFormatting()
    {
        var names = SentinelMentions.Names("Grant Watson", "GW\nG");
        names.Should().Equal("Grant Watson", "GW");
        var target = Guid.NewGuid();

        SentinelMentions.FindUnlinked(Line(new WikiRichTextSpan("Granted, the GWS suite")), names).Should().BeNull("whole words only");
        SentinelMentions.FindUnlinked(Line(new WikiRichTextSpan("Grant Watson", Link: "wikilink:x")), names).Should().BeNull("already a link");
        SentinelMentions.FindUnlinked(Line(new WikiRichTextSpan("ask gw first")), names).Should().Be("GW");

        var linked = SentinelMentions.LinkFirst(Line(new WikiRichTextSpan("Call Grant Watson today", Bold: true)), names, target)!;
        linked.RichText.Should().Equal(
            new WikiRichTextSpan("Call ", Bold: true),
            new WikiRichTextSpan("Grant Watson", Bold: true, Link: $"wikilink:{target}"),
            new WikiRichTextSpan(" today", Bold: true));
        SentinelMentions.LinkFirst(linked, names, target).Should().BeNull("nothing unlinked is left");
        SentinelMentions.Snippet(new string('a', 200) + " Grant Watson " + new string('b', 200), "Grant Watson").Should().Contain("Grant Watson").And.StartWith("…");
    }

    [Fact]
    public async Task Service_ShouldListViewableMentionsAndRelatedPages_AndLinkThroughTheSavePath()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.AddRange(new AppUser { Username = "grant", Role = AppRoles.Admin }, new AppUser { Username = "sam", Role = AppRoles.Author });
        var person = new WikiPage { Title = "Grant Watson", Slug = "grant", Aliases = "GW" };
        var notes = new WikiPage { Title = "Notes", Slug = "notes" };
        var hidden = new WikiPage { Title = "Hidden", Slug = "hidden" };
        var mentionLine = Line(new WikiRichTextSpan("Ask GW about pricing."));
        notes.BlocksJson = WikiBlockJson.Serialize([mentionLine]);
        hidden.BlocksJson = WikiBlockJson.Serialize([Line(new WikiRichTextSpan("Grant Watson's salary"))]);
        db.WikiPages.AddRange(person, notes, hidden);
        await db.SaveChangesAsync();
        db.SentinelResourcePermissions.AddRange(
            new SentinelResourcePermission { TargetId = person.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View },
            new SentinelResourcePermission { TargetId = notes.Id, IsDatabase = false, Username = "sam", AccessLevel = SentinelAccessLevels.View },
            new SentinelResourcePermission { TargetId = hidden.Id, IsDatabase = false, Username = "grant", AccessLevel = SentinelAccessLevels.FullAccess });
        await db.SaveChangesAsync();
        var access = new SentinelAccessService(db);
        var service = new SentinelConnectionsService(db, new WikiService(db), access, new FakeSearch([notes.Id, hidden.Id, person.Id]));

        (await service.GetUnlinkedMentionsAsync(person.Id, "sam")).Should().ContainSingle()
            .Which.Should().Match<SentinelUnlinkedMention>(m => m.PageId == notes.Id && m.Name == "GW" && m.BlockId == mentionLine.Id);
        (await service.GetUnlinkedMentionsAsync(person.Id, "grant")).Should().HaveCount(2);
        (await service.GetRelatedAsync(person.Id, "sam")).Select(r => r.Title).Should().Equal("Notes");

        await service.Invoking(s => s.LinkMentionAsync(notes.Id, mentionLine.Id, person.Id, "sam"))
            .Should().ThrowAsync<UnauthorizedAccessException>("sam can only view Notes");
        (await service.LinkMentionAsync(notes.Id, mentionLine.Id, person.Id, "grant")).Should().BeTrue();
        var saved = WikiBlockJson.ParseBlocks((await db.WikiPages.AsNoTracking().SingleAsync(p => p.Id == notes.Id)).BlocksJson).Single();
        saved.RichText.Should().Contain(s => s.Text == "GW" && s.Link == $"wikilink:{person.Id}");
        (await service.GetUnlinkedMentionsAsync(person.Id, "sam")).Should().BeEmpty();
        (await service.LinkMentionAsync(notes.Id, mentionLine.Id, person.Id, "grant")).Should().BeFalse("already linked");
    }

    private sealed class FakeSearch(IReadOnlyList<Guid> ids) : IHybridSearchService
    {
        public Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(string query, IReadOnlyCollection<string>? sourceTypes = null, int take = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SemanticSearchHit>>(ids.Select(id => new SemanticSearchHit(Guid.NewGuid(), SemanticSourceTypes.WikiPage, id, null, "", "preview", 0.8, 0, 0.8)).ToList());
        public Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RebuildAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
