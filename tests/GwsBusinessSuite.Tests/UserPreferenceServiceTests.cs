using FluentAssertions;
using GwsBusinessSuite.Application.AdminPortal;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class UserPreferenceServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ApplicationDbContext> _factory = null!;
    private UserPreferenceService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using (var db = await _factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        _service = new UserPreferenceService(_factory, TimeProvider.System);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Density_ShouldDefaultToComfortable_ChangePerPerson_AndKeepOneRow()
    {
        (await _service.GetAsync("grant", UserPreferenceKeys.Density)).Should().Be(Densities.Comfortable);

        await _service.SetAsync("grant", UserPreferenceKeys.Density, Densities.Compact);
        (await _service.GetAsync("grant", UserPreferenceKeys.Density)).Should().Be(Densities.Compact);
        (await _service.GetAsync("ana", UserPreferenceKeys.Density)).Should().Be(Densities.Comfortable, "settings are per person");

        await _service.SetAsync("grant", UserPreferenceKeys.Density, Densities.Comfortable);
        (await _service.GetAsync("grant", UserPreferenceKeys.Density)).Should().Be(Densities.Comfortable);
        await using var db = await _factory.CreateDbContextAsync();
        (await db.UserPreferences.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Set_ShouldRefuseUnknownValuesKeysAndSignedOutPeople()
    {
        await _service.Invoking(s => s.SetAsync("grant", UserPreferenceKeys.Density, "tiny")).Should().ThrowAsync<ArgumentException>();
        await _service.Invoking(s => s.SetAsync("grant", "font", "large")).Should().ThrowAsync<ArgumentException>();
        await _service.Invoking(s => s.SetAsync("unknown", UserPreferenceKeys.Density, Densities.Compact)).Should().ThrowAsync<ArgumentException>();
        (await _service.GetAsync("unknown", UserPreferenceKeys.Density)).Should().Be(Densities.Comfortable);
    }

    [Fact]
    public async Task Get_ShouldReadAStoredValueThatIsNoLongerAllowedAsTheDefault()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.UserPreferences.Add(new() { Username = "grant", Key = UserPreferenceKeys.Density, Value = "spacious" });
            await db.SaveChangesAsync();
        }
        (await _service.GetAsync("grant", UserPreferenceKeys.Density)).Should().Be(Densities.Comfortable);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }
}
