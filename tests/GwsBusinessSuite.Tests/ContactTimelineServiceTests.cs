using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Crm;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

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

        // GitHub's Ubuntu runner exposed the former ambient-culture bug as a generic currency
        // marker. Force a non-US culture here so USD display stays deterministic everywhere.
        var previousCulture = CultureInfo.CurrentCulture;
        IReadOnlyList<ContactTimelineEntry> timeline;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            timeline = await new ContactTimelineService(new Factory(options)).GetTimelineAsync(contact.Id);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

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
    public async Task GetTimelineAsync_ShouldIncludeCommentsTimeRemindersAndEmailTickets_LinkingToTheRecord()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var contact = new Contact { FullName = "Dana Client", Email = "Dana@Example.com", CreatedAt = T0 };
        var invoice = new Invoice { ContactId = contact.Id, Title = "Retainer", Status = InvoiceStatuses.Sent, CreatedAt = T0, SentAt = T0.AddDays(1), RemindersSent = 2, LastReminderAt = T0.AddDays(9) };
        invoice.LineItems.Add(new InvoiceLineItem { Description = "Retainer", Quantity = 1, UnitPriceUsd = 900 });
        var emailed = new SupportTicket { ContactId = contact.Id, Subject = "Broken link", TagsCsv = "billing, Email", CreatedAt = T0.AddDays(2) };
        var portal = new SupportTicket { ContactId = contact.Id, Subject = "Question", TagsCsv = "emailed-later", CreatedAt = T0.AddDays(3) };
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Contacts.Add(contact);
            db.Invoices.Add(invoice);
            db.SupportTickets.AddRange(emailed, portal);
            db.SupportTicketMessages.Add(new SupportTicketMessage { TicketId = emailed.Id, AuthorType = SupportTicketAuthorTypes.Staff, AuthorName = "Grant", Body = "Fixed.", CreatedAt = T0.AddDays(4) });
            db.TimeEntries.AddRange(
                new TimeEntry { Username = "grant", ContactId = contact.Id, Description = "Design review", StartedAt = T0.AddDays(5), EndedAt = T0.AddDays(5).AddMinutes(90), Minutes = 90 },
                new TimeEntry { Username = "grant", ContactId = contact.Id, Description = "Call", StartedAt = T0.AddDays(6), Minutes = 45, Billable = false },
                new TimeEntry { Username = "grant", ContactId = contact.Id, StartedAt = T0.AddDays(7), Minutes = 120, InvoiceId = invoice.Id },
                new TimeEntry { Username = "sam", ContactId = contact.Id, Description = "Migration", StartedAt = T0.AddDays(10) },
                new TimeEntry { Username = "grant", ContactId = Guid.NewGuid(), Description = "Someone else's", StartedAt = T0.AddDays(8), Minutes = 30 });
            var article = new Article { Slug = "launch", Title = "Launch notes" };
            db.Articles.Add(article);
            db.Comments.AddRange(
                new Comment { ArticleId = article.Id, AuthorName = "Dana", AuthorEmail = "dana@example.COM", Body = "Great   write-up!", Status = CommentStatuses.Approved, CreatedAt = T0.AddDays(11) },
                new Comment { ArticleId = article.Id, AuthorName = "Dana", AuthorEmail = "dana@example.com", Body = "One more thought", CreatedAt = T0.AddDays(12) },
                new Comment { ArticleId = article.Id, AuthorName = "Bot", AuthorEmail = "dana@example.com", Body = "Cheap pills", Status = CommentStatuses.Spam, CreatedAt = T0.AddDays(13) },
                new Comment { ArticleId = article.Id, AuthorName = "Else", AuthorEmail = "else@example.com", Body = "Not Dana", Status = CommentStatuses.Approved, CreatedAt = T0.AddDays(14) });
            await db.SaveChangesAsync();
        }

        var timeline = await new ContactTimelineService(new Factory(options)).GetTimelineAsync(contact.Id);

        var reminder = timeline.Should().ContainSingle(e => e.Title == "Payment reminder sent: Retainer").Which;
        reminder.At.Should().Be(T0.AddDays(9));
        reminder.Detail.Should().Be("$900.00 · 2 reminders so far");
        timeline.Where(e => e.Kind == ContactTimelineKinds.Invoice).Should().OnlyContain(e => e.Link == $"/admin/billing?invoice={invoice.Id}");

        timeline.Single(e => e.Title == "Ticket opened: Broken link").Detail.Should().Be("by email · Normal priority");
        timeline.Single(e => e.Title == "Ticket opened: Question").Detail.Should().Be("Normal priority", "only an exact \"email\" tag means it came by email");
        timeline.Single(e => e.Title == "Grant replied: Broken link").Link.Should().Be(ContactTimelineLinks.Ticket(emailed.Id));
        timeline.Single(e => e.Title == "Ticket opened: Question").Link.Should().Be($"/admin/support?ticket={portal.Id}");

        timeline.Where(e => e.Kind == ContactTimelineKinds.Time).Select(e => (e.Title, e.Detail)).Should().BeEquivalentTo(new[]
        {
            ("Timer running: Migration", "by sam"),
            ("Logged 2h: time", "invoiced · by grant"),
            ("Logged 45m: Call", "not billable · by grant"),
            ("Logged 1h 30m: Design review", "unbilled · by grant")
        }, o => o.WithStrictOrdering());
        timeline.Where(e => e.Kind == ContactTimelineKinds.Time).Should().OnlyContain(e => e.Link == $"/admin/time?contact={contact.Id}");

        timeline.Where(e => e.Kind == ContactTimelineKinds.Comment).Select(e => (e.Title, e.Detail)).Should().Equal(
            ("Commented on: Launch notes", "awaiting approval · One more thought"),
            ("Commented on: Launch notes", "Great write-up!"));
    }

    [Fact]
    public void Kinds_ShouldEachHaveALabelAndIcon()
    {
        ContactTimelineKinds.All.Should().OnlyHaveUniqueItems();
        ContactTimelineKinds.All.Should().OnlyContain(kind => ContactTimelineKinds.Label(kind) != kind);
        ContactTimelineKinds.All.Where(kind => kind != ContactTimelineKinds.Note)
            .Should().OnlyContain(kind => ContactTimelineKinds.Icon(kind) != ContactTimelineKinds.Icon(ContactTimelineKinds.Note));
        ContactTimelineKinds.All.Select(ContactTimelineKinds.Icon).Should().OnlyHaveUniqueItems();
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
