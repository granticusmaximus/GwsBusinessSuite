using FluentAssertions;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class AdminListViewServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AdminListViewService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        IDbContextFactory<ApplicationDbContext> factory = new Factory(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();
        _service = new AdminListViewService(factory);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveAsync_ShouldShareWithinRole_AndUpdateSameNamedView()
    {
        var created = await _service.SaveAsync("Admin", "crm.contacts", "Leads", "{\"status\":\"Lead\"}", ["name", "status"], "grant");
        await _service.SaveAsync("Admin", "crm.contacts", "Leads", "{\"status\":\"Prospect\"}", ["name", "email"], "ana");

        var views = await _service.ListAsync("Admin", "crm.contacts");
        views.Should().ContainSingle();
        views[0].Id.Should().Be(created.Id);
        views[0].FilterJson.Should().Contain("Prospect");
        views[0].Columns.Should().Equal("name", "email");
        (await _service.ListAsync("Author", "crm.contacts")).Should().BeEmpty();
        (await _service.ListAsync("Admin", "billing.invoices")).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldValidateJson_AndDeleteOnlyInsideTheNamedList()
    {
        var invalid = () => _service.SaveAsync("Admin", "crm.contacts", "Bad", "not-json", ["name"], "grant");
        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*filters are invalid*");

        var view = await _service.SaveAsync("Admin", "crm.contacts", "Customers", "{}", ["name", "name", "status"], "grant");
        view.Columns.Should().Equal("name", "status");
        await _service.DeleteAsync(view.Id, "Author", "crm.contacts");
        (await _service.ListAsync("Admin", "crm.contacts")).Should().ContainSingle();
        await _service.DeleteAsync(view.Id, "Admin", "crm.contacts");
        (await _service.ListAsync("Admin", "crm.contacts")).Should().BeEmpty();
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }
}
