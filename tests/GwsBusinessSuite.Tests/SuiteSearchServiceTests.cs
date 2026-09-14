using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class SuiteSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_ShouldFindMatchesAcrossCrmAutomationAndSentinel()
    {
        await using var db = await CreateDbAsync();

        var contact = new Contact { FullName = "Acme Rendering Corp Contact", CreatedBy = "u" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.Deals.Add(new Deal { ContactId = contact.Id, Title = "Acme Rendering Renewal", CreatedBy = "u" });
        await db.SaveChangesAsync();

        var registry = new AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new AutomationWorkflowService(db, registry, TimeProvider.System);
        await workflowService.CreateAsync("Acme Rendering Alert", "Notify when Acme renders");

        var wiki = new WikiService(db);
        await wiki.SavePageAsync(new WikiPageEditorModel { Title = "Acme Rendering Runbook" }, "u");

        var sentinelWorkspace = new SentinelWorkspaceService(db, TimeProvider.System);
        var service = new SuiteSearchService(db, sentinelWorkspace);

        var results = await service.SearchAsync("Acme Rendering", "u");

        results.Should().Contain(item => item.Category == "CRM deal" && item.Title == "Acme Rendering Renewal");
        results.Should().Contain(item => item.Category == "Automation" && item.Title == "Acme Rendering Alert");
        results.Should().Contain(item => item.Category == "Sentinel page" && item.Title == "Acme Rendering Runbook");
    }

    [Fact]
    public async Task SearchAsync_ShouldFindMatchesAcrossSupportTicketsAndBillingInvoices()
    {
        // Regression guard for a real gap: Support and Billing were entirely absent from ⌘K
        // search, despite being two of the areas most likely to need "find this specific record
        // right now" rather than browsing a list.
        await using var db = await CreateDbAsync();
        var contact = new Contact { FullName = "Acme Rendering Corp Contact", CreatedBy = "u" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.SupportTickets.Add(new SupportTicket { ContactId = contact.Id, Subject = "Acme Rendering export is broken", CreatedBy = "u" });
        db.Invoices.Add(new Invoice { ContactId = contact.Id, Title = "Acme Rendering Q3 invoice", CreatedBy = "u" });
        await db.SaveChangesAsync();

        var sentinelWorkspace = new SentinelWorkspaceService(db, TimeProvider.System);
        var service = new SuiteSearchService(db, sentinelWorkspace);

        var results = await service.SearchAsync("Acme Rendering", "u");

        results.Should().Contain(item => item.Category == "Support ticket" && item.Title == "Acme Rendering export is broken" && item.Url == "/admin/support");
        results.Should().Contain(item => item.Category == "Billing invoice" && item.Title == "Acme Rendering Q3 invoice" && item.Url == "/admin/billing");
    }

    [Fact]
    public async Task SearchAsync_ShouldReturnNothingForAQueryShorterThanTwoCharacters()
    {
        await using var db = await CreateDbAsync();
        var sentinelWorkspace = new SentinelWorkspaceService(db, TimeProvider.System);
        var service = new SuiteSearchService(db, sentinelWorkspace);

        (await service.SearchAsync("a", "u")).Should().BeEmpty();
    }

    private sealed class FakeHttpClient : IAutomationHttpClient
    {
        public Task<AutomationHttpResponse> SendAsync(AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static async Task<ApplicationDbContext> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
