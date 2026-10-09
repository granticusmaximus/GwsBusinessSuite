using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Community;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class DeveloperApiStatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSummary_ShouldCountWhatNeedsAttention_AndScopePersonalItemsToTheKeyOwner()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.DockerHealthAlerts.AddRange(
                new DockerHealthAlert { ContainerName = "web", Severity = DockerHealthAlertSeverity.Warning },
                new DockerHealthAlert { ContainerName = "ollama", Severity = DockerHealthAlertSeverity.Error },
                new DockerHealthAlert { ContainerName = "old", Severity = DockerHealthAlertSeverity.Error, IsRead = true });
            var acme = new Contact { FullName = "Acme", FollowUpDate = Now.AddDays(-1) };
            db.Contacts.AddRange(acme, new Contact { FullName = "Later", FollowUpDate = Now.AddDays(3) },
                new Contact { FullName = "Trashed", FollowUpDate = Now.AddDays(-2), TrashedAt = Now });
            db.SupportTickets.AddRange(
                new SupportTicket { ContactId = acme.Id, Subject = "Late", Status = SupportTicketStatuses.Open, FirstResponseDueAt = Now.AddHours(-1) },
                new SupportTicket { ContactId = acme.Id, Subject = "Waiting on client", Status = SupportTicketStatuses.Pending, FirstResponseDueAt = Now.AddHours(-1), ResolutionDueAt = Now.AddDays(1) },
                new SupportTicket { ContactId = acme.Id, Subject = "Done", Status = SupportTicketStatuses.Resolved, ResolutionDueAt = Now.AddDays(-3) });
            var site = new CmsSite { Name = "Site", Slug = "site" };
            db.CmsSites.Add(site);
            var page = new CmsPage { SiteId = site.Id, Title = "Contact", Slug = "contact" };
            db.CmsPages.Add(page);
            db.FormSubmissions.AddRange(new FormSubmission { PageId = page.Id }, new FormSubmission { PageId = page.Id, IsRead = true });
            var article = new Article { Slug = "a", Title = "A" };
            db.Articles.Add(article);
            db.Comments.Add(new Comment { ArticleId = article.Id, Body = "Hi" });
            db.OverwatchAreaAlerts.AddRange(
                new OverwatchAreaAlert { Username = "grant", Title = "t", Message = "m" },
                new OverwatchAreaAlert { Username = "someone-else", Title = "t", Message = "m" });
            var type = new BookingType { Title = "Intro call", Slug = "intro" };
            db.BookingTypes.Add(type);
            db.Bookings.AddRange(
                new Booking { BookingTypeId = type.Id, AttendeeName = "Later", AttendeeEmail = "l@x.com", ManageTokenHash = "1", StartsAt = Now.AddDays(2), EndsAt = Now.AddDays(2).AddMinutes(30) },
                new Booking { BookingTypeId = type.Id, AttendeeName = "Dana", AttendeeEmail = "d@x.com", ManageTokenHash = "2", StartsAt = Now.AddHours(3), EndsAt = Now.AddHours(4) },
                new Booking { BookingTypeId = type.Id, AttendeeName = "Past", AttendeeEmail = "p@x.com", ManageTokenHash = "3", StartsAt = Now.AddHours(-3), EndsAt = Now.AddHours(-2) });
            db.TimeEntries.Add(new TimeEntry { Username = "grant", ContactId = acme.Id, Description = "Audit", StartedAt = Now.AddMinutes(-20) });
            await db.SaveChangesAsync();
        }

        await using var chatDb = new ApplicationDbContext(options);
        var service = new DeveloperApiStatusService(new Factory(options), new ChatService(chatDb), new FixedClock(Now));
        var summary = await service.GetSummaryAsync("grant");

        summary.Health.Should().Be("critical", "an unread error outranks a warning");
        summary.UnreadHealthAlerts.Should().Be(2);
        summary.OpenTickets.Should().Be(2);
        summary.OverdueTickets.Should().Be(1, "a Pending ticket is waiting on the client, not on staff's first response");
        summary.DueFollowUps.Should().Be(1);
        summary.UnreadFormSubmissions.Should().Be(1);
        summary.PendingComments.Should().Be(1);
        summary.UnreadMessages.Should().Be(0, "no chat threads were seeded");
        summary.UnreadAreaAlerts.Should().Be(1, "another user's area alerts aren't this key's");
        summary.NextBooking.Should().Be(new GwsBusinessSuite.Application.DeveloperApi.DeveloperApiStatusBooking("Intro call", "Dana", Now.AddHours(3)));
        summary.RunningTimer!.ContactName.Should().Be("Acme");
        summary.AttentionCount.Should().Be(2 + 1 + 1 + 1 + 1 + 0 + 1);

        (await service.GetSummaryAsync("someone-else")).RunningTimer.Should().BeNull();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }
}
