using FluentAssertions;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class ActivityFeedSaveChangesInterceptorTests
{
    [Fact]
    public async Task PublishingAPage_AndOpeningAndResolvingATicket_PostToTheFeed_Once()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection)
            .AddInterceptors(new ActivityFeedSaveChangesInterceptor()).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var site = new CmsSite { Name = "S", Slug = "s" };
        db.CmsSites.Add(site);
        var page = new CmsPage { SiteId = site.Id, Title = "Pricing", Slug = "pricing", Status = CmsPageStatuses.Draft, CreatedBy = "ana" };
        db.CmsPages.Add(page);
        await db.SaveChangesAsync();
        (await db.ActivityEvents.CountAsync()).Should().Be(0, "a draft isn't news");

        page.Status = CmsPageStatuses.Published;
        page.UpdatedBy = "ana";
        await db.SaveChangesAsync();
        page.Title = "Pricing 2";
        await db.SaveChangesAsync();

        var contact = new Contact { FullName = "Pat Client", Email = "pat@example.com" };
        db.Contacts.Add(contact);
        var ticket = new SupportTicket { Subject = "Login broken", ContactId = contact.Id, CreatedBy = "ben" };
        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync();
        ticket.Status = SupportTicketStatuses.Resolved;
        ticket.UpdatedBy = "cal";
        await db.SaveChangesAsync();

        var events = await db.ActivityEvents.AsNoTracking().ToListAsync();
        events.Select(e => (e.CreatedBy, e.Verb, e.TargetLabel)).Should().BeEquivalentTo(new[]
        {
            ("ana", "published the page", "Pricing"),
            ("ben", "opened the support ticket", "Login broken"),
            ("cal", "resolved the support ticket", "Login broken")
        });
    }
}
