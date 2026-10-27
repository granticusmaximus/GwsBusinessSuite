using FluentAssertions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Table power (tech-forward T3): row selection, CSV export and the status-only contact update.
public sealed class TablePowerTests
{
    private static readonly string[] Order = ["a", "b", "c", "d", "e"];

    [Fact]
    public void Click_ShouldToggle_AndShiftClickShouldSelectTheRange()
    {
        var selection = new ListSelection<string>();
        selection.Toggle("b", range: false, Order);
        selection.Toggle("d", range: true, Order);
        selection.Items.Should().BeEquivalentTo(["b", "c", "d"]);

        selection.Toggle("c", range: false, Order);
        selection.Items.Should().BeEquivalentTo(["b", "d"], "a plain click toggles one row");
    }

    [Fact]
    public void ShiftClick_ShouldTakeTheAnchorsState_InEitherDirection()
    {
        var selection = new ListSelection<string>();
        selection.SetAll(Order, selected: true);
        selection.Toggle("d", range: false, Order);
        selection.Toggle("b", range: true, Order);
        selection.Items.Should().BeEquivalentTo(["a", "e"], "the anchor was cleared, so the range clears");
    }

    [Fact]
    public void SelectAll_AndRetain_ShouldFollowTheList()
    {
        var selection = new ListSelection<string>();
        selection.SetAll(["a", "b"], selected: true);
        selection.AllSelected(["a", "b"]).Should().BeTrue();
        selection.AllSelected(["a", "b", "c"]).Should().BeFalse();
        selection.AllSelected([]).Should().BeFalse();

        selection.Retain(["b", "c"]);
        selection.Items.Should().Equal("b");
        selection.Clear();
        selection.Count.Should().Be(0);
    }

    [Fact]
    public void Csv_ShouldQuoteWhatNeedsIt_AndNeutraliseFormulas()
    {
        var csv = CsvExport.Build(["Name", "Note"], [
            ["Rivers, Dana", "said \"hi\""],
            ["=HYPERLINK(\"x\")", null],
            ["Plain", "two\nlines"]
        ]);

        csv.Split(Environment.NewLine)[0].Should().Be("Name,Note");
        csv.Should().Contain("\"Rivers, Dana\",\"said \"\"hi\"\"\"");
        csv.Should().Contain("\"'=HYPERLINK(\"\"x\"\")\",");
        csv.Should().Contain("Plain,\"two\nlines\"");
    }

    [Fact]
    public async Task SetContactStatus_ShouldChangeOnlyTheStatus_AndRefuseUnknownStatuses()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var contact = new Contact { FullName = "Dana Rivers", Email = "dana@example.com", Status = ContactStatuses.Lead, CreatedBy = "u" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var crm = new CrmService(db);

        await crm.SetContactStatusAsync(contact.Id, ContactStatuses.Customer);
        var saved = await db.Contacts.AsNoTracking().SingleAsync();
        saved.Status.Should().Be(ContactStatuses.Customer);
        saved.Email.Should().Be("dana@example.com");
        saved.UpdatedAt.Should().NotBeNull();

        await crm.Invoking(c => c.SetContactStatusAsync(contact.Id, "VIP")).Should().ThrowAsync<ArgumentException>();
        await crm.Invoking(c => c.SetContactStatusAsync(Guid.NewGuid(), ContactStatuses.Lead)).Should().ThrowAsync<InvalidOperationException>();
    }
}
