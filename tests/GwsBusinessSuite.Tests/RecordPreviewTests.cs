using FluentAssertions;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

// Hover previews and who sees which ⌘K results (tech-forward T2).
public sealed class RecordPreviewTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<ApplicationDbContext> _factory = null!;
    private RecordPreviewService _previews = null!;
    private Guid _danaId, _ticketId, _invoiceId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new Factory(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var dana = new Contact { FullName = "Dana Rivers", Company = "Rivers & Co", Email = "dana@example.com", Status = ContactStatuses.Lead, FollowUpDate = Now.AddDays(-1), CreatedBy = "u" };
        db.Contacts.Add(dana);
        await db.SaveChangesAsync();
        var ticket = new SupportTicket { ContactId = dana.Id, Subject = "Login loop", Status = SupportTicketStatuses.Open, Priority = SupportTicketPriorities.Urgent, ResolutionDueAt = Now.AddHours(-3), CreatedBy = "u", CreatedAt = Now.AddDays(-1) };
        var invoice = new Invoice { ContactId = dana.Id, Title = "October retainer", Status = InvoiceStatuses.Sent, Currency = "USD", DueDate = Now.AddDays(-5), SentAt = Now.AddDays(-20), CreatedBy = "u" };
        db.SupportTickets.Add(ticket);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        db.InvoiceLineItems.AddRange(
            new InvoiceLineItem { InvoiceId = invoice.Id, Description = "Design", Quantity = 2, UnitPriceUsd = 500m, CreatedBy = "u" },
            new InvoiceLineItem { InvoiceId = invoice.Id, Description = "Hosting", Quantity = 1, UnitPriceUsd = 250m, CreatedBy = "u" });
        await db.SaveChangesAsync();
        (_danaId, _ticketId, _invoiceId) = (dana.Id, ticket.Id, invoice.Id);
        _previews = new RecordPreviewService(_factory, new Clock(Now));
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Contact_ShouldShowStatusFollowUpAndWhatsOpen()
    {
        var preview = await _previews.GetAsync("contact", _danaId);
        preview!.Title.Should().Be("Dana Rivers");
        preview.Status.Should().Be("Lead");
        preview.Tone.Should().Be("info");
        preview.Facts.Should().Contain(new RecordPreviewFact("Follow-up", $"due {Now.AddDays(-1).ToLocalTime():dd-MM-yy}"));
        preview.Facts.Should().Contain(new RecordPreviewFact("Open", "1 ticket · 1 unpaid invoice"));
        preview.Actions.Select(a => a.Url).Should().Equal($"/admin/crm/contacts/{_danaId}", "mailto:dana@example.com");
    }

    [Fact]
    public async Task Ticket_ShouldSayWhenItsOverdue()
    {
        var preview = await _previews.GetAsync("ticket", _ticketId);
        preview!.Tone.Should().Be("info");
        preview.Facts.Should().Contain(new RecordPreviewFact("Priority", "Urgent"));
        preview.Facts.Single(f => f.Label == "Due").Value.Should().StartWith("overdue since");
        preview.Url.Should().Be($"/admin/support?ticket={_ticketId}");
    }

    [Fact]
    public async Task Invoice_ShouldTotalItsLines_AndFlagOverdue()
    {
        var preview = await _previews.GetAsync("invoice", _invoiceId);
        preview!.Facts.Should().Contain(new RecordPreviewFact("Client", "Dana Rivers"));
        preview.Facts.Single(f => f.Label == "Total").Value.Should().Contain("1,250.00");
        preview.Facts.Single(f => f.Label == "Due").Value.Should().StartWith("overdue since");
        preview.Facts.Should().Contain(f => f.Label == "Sent");
    }

    [Fact]
    public async Task UnknownKindsAndRecords_ShouldHaveNoPreview()
    {
        (await _previews.GetAsync("password", _danaId)).Should().BeNull();
        (await _previews.GetAsync("contact", Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public void NonAdmins_ShouldOnlySeeResultsTheyCanOpen()
    {
        SuiteSearchResult Result(string category) => new("x", "", category, "/", "");
        var all = new[]
        {
            Result(SuiteSearchCategories.CrmContact), Result(SuiteSearchCategories.BillingInvoice), Result(SuiteSearchCategories.SupportTicket),
            Result(SuiteSearchCategories.Automation), Result(SuiteSearchCategories.AffiliateOffer), Result(SuiteSearchCategories.CrmDeal),
            Result(SuiteSearchCategories.SentinelPage), Result(SuiteSearchCategories.CmsPage), Result(SuiteSearchCategories.Article)
        };

        SuiteSearchVisibility.For(all, isAdmin: true).Should().HaveCount(9);
        SuiteSearchVisibility.For(all, isAdmin: false).Select(r => r.Category)
            .Should().Equal(SuiteSearchCategories.SentinelPage, SuiteSearchCategories.CmsPage, SuiteSearchCategories.Article);
    }

    private sealed class Factory(DbContextOptions<ApplicationDbContext> options) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
