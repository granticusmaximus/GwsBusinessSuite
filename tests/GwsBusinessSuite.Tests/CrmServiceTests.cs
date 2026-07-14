using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class CrmServiceTests
{
    [Fact]
    public async Task SaveContactAsync_ShouldCreateAndUpdateContacts()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));

        var created = await service.SaveContactAsync(new ContactEditorModel
        {
            FullName = "Ava Carter",
            Email = "ava@example.com",
            Company = "Northwind",
            Status = ContactStatuses.Lead
        });

        created.FullName.Should().Be("Ava Carter");
        created.Email.Should().Be("ava@example.com");
        created.Company.Should().Be("Northwind");
        created.Status.Should().Be(ContactStatuses.Lead);
        created.CreatedBy.Should().Be("grantwatson");

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
            Status = ContactStatuses.Customer
        });

        updated.Id.Should().Be(created.Id);
        updated.Email.Should().Be("ava.carter@example.com");
        updated.Company.Should().Be("Northwind Traders");
        updated.Status.Should().Be(ContactStatuses.Customer);
    }

    [Fact]
    public async Task TrashContactAsync_ShouldRemoveFromDefaultList_ButKeepInTrash()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Ben Ito" });

        await service.TrashContactAsync(contact.Id);

        (await service.ListContactsAsync()).Should().BeEmpty();
        (await service.ListContactsAsync(includeTrashed: true)).Should().ContainSingle(c => c.Id == contact.Id);
        (await service.ListTrashedContactsAsync()).Should().ContainSingle(c => c.Id == contact.Id);
    }

    [Fact]
    public async Task GetContactAsync_ShouldReturnNull_ForATrashedContact()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Trashed Lookup" });
        await service.TrashContactAsync(contact.Id);

        (await service.GetContactAsync(contact.Id)).Should().BeNull();
    }

    [Fact]
    public async Task SaveContactAsync_ShouldThrow_WhenEditingAStaleEditorForATrashedContact()
    {
        // Simulates a browser tab that opened the contact editor before the contact was
        // trashed elsewhere - clicking Save should fail loudly, not silently resurrect
        // edits on a trashed record.
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Stale Editor" });
        await service.TrashContactAsync(contact.Id);

        var act = () => service.SaveContactAsync(new ContactEditorModel { ContactId = contact.Id, FullName = "Edited While Trashed" });

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddActivityAsync_ShouldThrow_WhenContactIsTrashed()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Trashed Activity Target" });
        await service.TrashContactAsync(contact.Id);

        var act = () => service.AddActivityAsync(contact.Id, "A note from a stale tab");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RestoreContactAsync_ShouldClearTrashedAt_AndReturnToDefaultList()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Cara Diaz" });
        await service.TrashContactAsync(contact.Id);

        await service.RestoreContactAsync(contact.Id);

        (await service.ListContactsAsync()).Should().ContainSingle(c => c.Id == contact.Id);
        (await service.ListTrashedContactsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteContactPermanentlyAsync_ShouldRemoveEntirely()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Deja Ellis" });
        await service.TrashContactAsync(contact.Id);

        await service.DeleteContactPermanentlyAsync(contact.Id);

        (await service.ListContactsAsync(includeTrashed: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task AddActivityAsync_ShouldPersistNote_AndListActivitiesShouldReturnNewestFirst()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db, new FixedCurrentUserAccessor("grantwatson"));
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Eli Frank" });

        var first = await service.AddActivityAsync(contact.Id, "Sent intro email");
        var second = await service.AddActivityAsync(contact.Id, "Had a call");

        first.CreatedBy.Should().Be("grantwatson");
        var activities = await service.ListActivitiesAsync(contact.Id);
        activities.Should().HaveCount(2);
        activities[0].Id.Should().Be(second.Id);
        activities[1].Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task AddActivityAsync_ShouldThrow_WhenNoteIsBlank()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db);
        var contact = await service.SaveContactAsync(new ContactEditorModel { FullName = "Faye Grant" });

        var act = () => service.AddActivityAsync(contact.Id, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListDueFollowUpsAsync_ShouldOnlyReturnPastOrTodayFollowUps_OrderedByEarliestFirst()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db);
        var now = DateTimeOffset.UtcNow;

        var overdue = await service.SaveContactAsync(new ContactEditorModel { FullName = "Overdue", FollowUpDate = now.AddDays(-2) });
        var dueToday = await service.SaveContactAsync(new ContactEditorModel { FullName = "Due today", FollowUpDate = now.AddMinutes(-1) });
        await service.SaveContactAsync(new ContactEditorModel { FullName = "Future", FollowUpDate = now.AddDays(5) });
        await service.SaveContactAsync(new ContactEditorModel { FullName = "No follow-up" });

        var due = await service.ListDueFollowUpsAsync();

        due.Select(c => c.Id).Should().ContainInOrder(overdue.Id, dueToday.Id);
        due.Should().HaveCount(2);
        (await service.CountDueFollowUpsAsync()).Should().Be(2);
    }

    [Fact]
    public async Task SaveContactAsync_ShouldNormalizeFollowUpDate_ToUtcMidnightOfThePickedCalendarDate()
    {
        // Regression test: Blazor's InputDate binds a plain date string to DateTimeOffset
        // by resolving the missing offset against the server process's local timezone,
        // not the browser user's - SaveContactAsync must re-anchor to UTC midnight of the
        // same calendar date so "due" is deterministic regardless of server TZ config.
        await using var db = await CreateDbAsync();
        var service = new CrmService(db);
        var pickedInAFictionalPlusFiveOffset = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(5));

        var saved = await service.SaveContactAsync(new ContactEditorModel
        {
            FullName = "Normalized Date",
            FollowUpDate = pickedInAFictionalPlusFiveOffset
        });

        saved.FollowUpDate.Should().Be(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ListDueFollowUpsAsync_ShouldExcludeTrashedContacts()
    {
        await using var db = await CreateDbAsync();
        var service = new CrmService(db);
        var contact = await service.SaveContactAsync(new ContactEditorModel
        {
            FullName = "Trashed but due",
            FollowUpDate = DateTimeOffset.UtcNow.AddDays(-1)
        });

        await service.TrashContactAsync(contact.Id);

        (await service.ListDueFollowUpsAsync()).Should().BeEmpty();
        (await service.CountDueFollowUpsAsync()).Should().Be(0);
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
