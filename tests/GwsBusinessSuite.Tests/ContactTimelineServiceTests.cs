using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class ContactTimelineServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetTimelineAsync_ShouldMergeEveryModule_NewestFirst_AndOnlyForThisContact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var contact = new Contact { FullName = "Dana Client", Email = "Dana@Example.com", CreatedAt = T0, CreatedBy = "grant" };
        var other = new Contact { FullName = "Someone Else", Email = "else@example.com", CreatedAt = T0 };
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Contacts.AddRange(contact, other);
            db.ContactActivities.Add(new ContactActivity { ContactId = contact.Id, Note = "Intro call went well", CreatedAt = T0.AddDays(1), CreatedBy = "grant" });
            db.Deals.Add(new Deal { ContactId = contact.Id, Title = "Website rebuild", ValueUsd = 5000, Stage = DealStages.Won, CreatedAt = T0.AddDays(2), ClosedAt = T0.AddDays(5) });
            var invoice = new Invoice { ContactId = contact.Id, Title = "Rebuild deposit", Status = InvoiceStatuses.Paid, CreatedAt = T0.AddDays(6), SentAt = T0.AddDays(6).AddHours(1), PaidAt = T0.AddDays(8) };
            invoice.LineItems.Add(new InvoiceLineItem { Description = "Deposit", Quantity = 1, UnitPriceUsd = 2500 });
            db.Invoices.Add(invoice);
            var ticket = new SupportTicket { ContactId = contact.Id, Subject = "Login trouble", CreatedAt = T0.AddDays(9), ResolvedAt = T0.AddDays(10) };
            db.SupportTickets.Add(ticket);
            db.SupportTicketMessages.Add(new SupportTicketMessage { TicketId = ticket.Id, AuthorType = SupportTicketAuthorTypes.Contact, AuthorName = "Dana", Body = "I can't  sign\\nin.", CreatedAt = T0.AddDays(9) });
            var type = new BookingType { Title = "Kickoff call", Slug = "kickoff" };
            db.BookingTypes.Add(type);
            db.Bookings.Add(new Booking { BookingTypeId = type.Id, ContactId = contact.Id, AttendeeName = "Dana", AttendeeEmail = "dana@example.com", ManageTokenHash = "x",
                StartsAt = T0.AddDays(12), EndsAt = T0.AddDays(12).AddMinutes(30), CreatedAt = T0.AddDays(11) });
            var site = new CmsSite { Name = "Site", Slug = "site" };
            db.CmsSites.Add(site);
            var page = new CmsPage { SiteId = site.Id, Title = "Contact us", Slug = "contact" };
            db.CmsPages.Add(page);
            // An older submission that only carried the email (different case), and another person's.
            db.FormSubmissions.Add(new FormSubmission { PageId = page.Id, Email = "dana@example.com", CreatedAt = T0.AddDays(-3) });
            db.FormSubmissions.Add(new FormSubmission { PageId = page.Id, Email = "else@example.com", CreatedAt = T0.AddDays(-2) });
            db.ClientPortalLoginTokens.Add(new ClientPortalLoginToken { ContactId = contact.Id, TokenHash = "h", ConsumedAt = T0.AddDays(13), ExpiresAt = T0.AddDays(14) });
            db.ClientPortalLoginTokens.Add(new ClientPortalLoginToken { ContactId = contact.Id, TokenHash = "unused", ExpiresAt = T0.AddDays(14) });
            db.ContactActivities.Add(new ContactActivity { ContactId = other.Id, Note = "Not Dana's", CreatedAt = T0.AddDays(20) });
            await db.SaveChangesAsync();
        }

        var timeline = await new ContactTimelineService(new Factory(options)).GetTimelineAsync(contact.Id);

        timeline.Select(e => e.At).Should().BeInDescendingOrder();
        timeline.Select(e => e.Title).Should().ContainInOrder(
            "Signed in to the client portal",
            "Kickoff call",
            "Booked: Kickoff call",
            "Ticket resolved: Login trouble",
            "Invoice paid: Rebuild deposit",
            "Invoice sent: Rebuild deposit",
            "Deal won: Website rebuild",
            "Deal opened: Website rebuild",
            "Note",
            "Contact created",
            "Submitted a form: Contact us");
        timeline.Should().ContainSingle(e => e.Title == "Wrote in: Login trouble").Which.Detail.Should().Be("I can't sign\\nin.");
        timeline.Single(e => e.Title == "Invoice paid: Rebuild deposit").Detail.Should().Be("$2,500.00");
        timeline.Should().ContainSingle(e => e.Kind == ContactTimelineKinds.Form, "another person's submission isn't Dana's");
        timeline.Should().ContainSingle(e => e.Kind == ContactTimelineKinds.Portal, "an unused sign-in link isn't a sign-in");
        timeline.Should().NotContain(e => e.Detail != null && e.Detail.Contains("Not Dana's"));
    }

    [Fact]
    public async Task GetTimelineAsync_ShouldBeEmpty_ForAnUnknownContact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using (var db = new ApplicationDbContext(options)) await db.Database.EnsureCreatedAsync();

        (await new ContactTimelineService(new Factory(options)).GetTimelineAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
