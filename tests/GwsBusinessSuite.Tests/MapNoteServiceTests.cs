using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class MapNoteServiceTests : IAsyncDisposable
{
    private static readonly MapNotePoint Spot = new(33.75, -84.39);
    private static readonly MapNotePoint[] Triangle = [new(33.7, -84.4), new(33.8, -84.4), new(33.75, -84.3)];
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private async Task<MapNoteService> CreateAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();
        return new MapNoteService(new Factory(options), TimeProvider.System);
    }

    [Fact]
    public async Task Save_ShouldKeepPinsAndAreas_PerUser_AndEditInPlace()
    {
        var service = await CreateAsync();

        var pin = await service.SaveAsync("grant", null, MapNoteKinds.Pin, "  Staging  ", "Meet here", "#FF4D4D", [Spot]);
        await service.SaveAsync("grant", null, MapNoteKinds.Area, "Blind spot", null, "not-a-colour", Triangle);
        await service.SaveAsync("someone-else", null, MapNoteKinds.Pin, "Theirs", null, null, [Spot]);
        var edited = await service.SaveAsync("grant", pin.Id, MapNoteKinds.Pin, "Staging area", "Moved", "#7dffb0", [new(33.76, -84.38)]);

        var mine = await service.ListAsync("grant");
        mine.Select(n => n.Title).Should().BeEquivalentTo("Staging area", "Blind spot");
        edited.Id.Should().Be(pin.Id);
        edited.Points.Should().Equal(new MapNotePoint(33.76, -84.38));
        mine.Single(n => n.Title == "Blind spot").Color.Should().Be(MapNoteService.Colors[0], "a bad colour falls back");
        pin.Color.Should().Be("#ff4d4d");
        await service.Invoking(s => s.SaveAsync("someone-else", pin.Id, MapNoteKinds.Pin, "Hijack", null, null, [Spot]))
            .Should().ThrowAsync<ArgumentException>("only the owner can edit");
    }

    [Theory]
    [InlineData("pin", "", 1)]
    [InlineData("pin", "Two spots", 2)]
    [InlineData("area", "Too few", 2)]
    [InlineData("circle", "Bad kind", 1)]
    public async Task Save_ShouldRejectBadNotes(string kind, string title, int points)
    {
        var service = await CreateAsync();
        await service.Invoking(s => s.SaveAsync("grant", null, kind, title, null, null, Enumerable.Repeat(Spot, points).ToList()))
            .Should().ThrowAsync<ArgumentException>();
        await service.Invoking(s => s.SaveAsync("grant", null, MapNoteKinds.Pin, "Off the map", null, null, [new(95, 0)]))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Sharing_ShouldOpenTheNoteForOthersWithTheKey_UntilUnshared()
    {
        var service = await CreateAsync();
        var note = await service.SaveAsync("grant", null, MapNoteKinds.Pin, "Staging", null, null, [Spot]);

        (await service.OpenAsync("teammate", note.Id, null)).Should().BeNull("not shared yet");
        var key = await service.ShareAsync("grant", note.Id);
        (await service.ShareAsync("grant", note.Id)).Should().Be(key, "sharing again keeps the same link");
        (await service.ShareAsync("teammate", note.Id)).Should().BeNull("only the owner can share");

        (await service.OpenAsync("teammate", note.Id, key))!.Owner.Should().Be("grant");
        (await service.OpenAsync("teammate", note.Id, "wrong-key")).Should().BeNull();
        (await service.OpenAsync("grant", note.Id, null)).Should().NotBeNull("the owner needs no key");
        MapNoteService.ShareLink(note.Id, key!).Should().Be($"/admin/osint?note={note.Id}&key={key}");

        await service.UnshareAsync("grant", note.Id);
        (await service.OpenAsync("teammate", note.Id, key)).Should().BeNull("unsharing kills the old link");
        (await service.ListAsync("grant")).Single().IsShared.Should().BeFalse();

        await service.DeleteAsync("teammate", note.Id);
        (await service.ListAsync("grant")).Should().ContainSingle("only the owner can delete");
        await service.DeleteAsync("grant", note.Id);
        (await service.ListAsync("grant")).Should().BeEmpty();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
