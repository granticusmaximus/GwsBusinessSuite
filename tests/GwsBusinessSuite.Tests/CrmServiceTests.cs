using FluentAssertions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CrmServiceTests
{
    [Fact]
    public async Task SaveContactAsync_ShouldCreateUpdateAndDeleteContacts()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db);

        var created = await service.SaveContactAsync(new ContactEditorModel
        {
            FullName = "Ava Carter",
            Email = "ava@example.com",
            Company = "Northwind",
            Status = "Lead"
        });

        created.FullName.Should().Be("Ava Carter");
        created.Email.Should().Be("ava@example.com");
        created.Company.Should().Be("Northwind");
        created.Status.Should().Be("Lead");

        var listed = await service.ListContactsAsync();
        listed.Should().HaveCount(1);

        var loaded = await service.GetContactAsync(created.Id);
        loaded.Should().NotBeNull();
        loaded!.FullName.Should().Be("Ava Carter");

        var updated = await service.SaveContactAsync(new ContactEditorModel
        {
            ContactId = created.Id,
            FullName = "Ava Carter",
            Email = "ava.carter@example.com",
            Company = "Northwind Traders",
            Status = "Customer"
        });

        updated.Id.Should().Be(created.Id);
        updated.Email.Should().Be("ava.carter@example.com");
        updated.Company.Should().Be("Northwind Traders");
        updated.Status.Should().Be("Customer");

        await service.DeleteContactAsync(created.Id);

        (await service.ListContactsAsync()).Should().BeEmpty();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
