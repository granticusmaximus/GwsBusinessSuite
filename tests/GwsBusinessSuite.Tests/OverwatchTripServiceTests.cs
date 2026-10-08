using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class OverwatchTripServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private OverwatchTripService _service = null!;

    private async Task InitAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();
        _service = new OverwatchTripService(new Factory(options), TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Save_ShouldKeepStopsInOrder_ReplaceBySameName_AndStayPrivate()
    {
        await InitAsync();

        await _service.SaveAsync("grant", "Atlanta run", ["Kathleen, GA", "", "Macon, GA", "Atlanta, GA"]);
        await _service.SaveAsync("grant", "atlanta RUN", ["Kathleen, GA", "32.84, -83.63", "Atlanta, GA"]);
        await _service.SaveAsync("other", "Theirs", ["A", "B"]);

        var mine = await _service.ListAsync("grant");
        mine.Should().ContainSingle("saving under the same name (any case) replaces the trip");
        mine[0].Stops.Should().Equal("Kathleen, GA", "32.84, -83.63", "Atlanta, GA");
        (await _service.ListAsync("other")).Should().ContainSingle(t => t.Name == "Theirs");

        await _service.DeleteAsync("other", mine[0].Id);
        (await _service.ListAsync("grant")).Should().ContainSingle("someone else can't delete my trip");
        await _service.DeleteAsync("grant", mine[0].Id);
        (await _service.ListAsync("grant")).Should().BeEmpty();
    }

    [Fact]
    public async Task Save_ShouldRequireANameAndTwoStops()
    {
        await InitAsync();

        await FluentActions.Awaiting(() => _service.SaveAsync("grant", "", ["A", "B"])).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => _service.SaveAsync("grant", "One stop", ["A", " "])).Should().ThrowAsync<ArgumentException>()
            .WithMessage("*start and a destination*");
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
