using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SupportTicketCannedResponseServiceTests
{
    [Fact]
    public async Task CreateAsync_ShouldPersistAndListAlphabeticallyByTitle()
    {
        await using var fixture = await Fixture.CreateAsync();

        await fixture.Service.CreateAsync("Welcome", "Thanks for reaching out!", "staff");
        await fixture.Service.CreateAsync("Apology", "Sorry for the trouble.", "staff");

        var responses = await fixture.Service.ListAsync();

        responses.Select(r => r.Title).Should().Equal("Apology", "Welcome");
    }

    [Fact]
    public async Task CreateAsync_ShouldReject_AnEmptyTitleOrBody()
    {
        await using var fixture = await Fixture.CreateAsync();

        var emptyTitle = () => fixture.Service.CreateAsync("  ", "Body", "staff");
        var emptyBody = () => fixture.Service.CreateAsync("Title", "  ", "staff");

        await emptyTitle.Should().ThrowAsync<ArgumentException>();
        await emptyBody.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpdateAsync_ShouldChangeTitleAndBody()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync("Welcome", "Hi there", "staff");

        var updated = await fixture.Service.UpdateAsync(created.Id, "Greeting", "Hello!", "staff");

        updated.Title.Should().Be("Greeting");
        updated.Body.Should().Be("Hello!");
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveIt_AndBeANoOpWhenAlreadyGone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync("Welcome", "Hi there", "staff");

        await fixture.Service.DeleteAsync(created.Id);
        var act = async () => await fixture.Service.DeleteAsync(created.Id);

        (await fixture.Service.ListAsync()).Should().BeEmpty();
        await act.Should().NotThrowAsync();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db)
        {
            _connection = connection;
            Db = db;
            Service = new SupportTicketCannedResponseService(db, TimeProvider.System);
        }

        public ApplicationDbContext Db { get; }
        public SupportTicketCannedResponseService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
