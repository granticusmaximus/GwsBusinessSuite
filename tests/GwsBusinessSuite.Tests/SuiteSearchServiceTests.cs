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

        // Each opens the record itself in its list's inspector (tech-forward T1).
        var ticket = await db.SupportTickets.SingleAsync();
        var invoice = await db.Invoices.SingleAsync();
        results.Should().Contain(item => item.Category == "Support ticket" && item.Title == "Acme Rendering export is broken" && item.Url == $"/admin/support?ticket={ticket.Id}");
        results.Should().Contain(item => item.Category == "Billing invoice" && item.Title == "Acme Rendering Q3 invoice" && item.Url == $"/admin/billing?invoice={invoice.Id}");
        results.Should().Contain(item => item.Category == "CRM contact" && item.Url == $"/admin/crm/contacts/{contact.Id}");
    }

    [Fact]
    public async Task TypeFilter_ShouldNarrowToOneKind_AndListItsNewestWithoutText()
    {
        await using var db = await CreateDbAsync();
        var contact = new Contact { FullName = "Acme Rendering Corp Contact", CreatedBy = "u" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.SupportTickets.Add(new SupportTicket { ContactId = contact.Id, Subject = "Acme older ticket", CreatedBy = "u", CreatedAt = DateTimeOffset.UtcNow.AddDays(-2) });
        db.SupportTickets.Add(new SupportTicket { ContactId = contact.Id, Subject = "Acme newer ticket", CreatedBy = "u", CreatedAt = DateTimeOffset.UtcNow });
        db.Invoices.Add(new Invoice { ContactId = contact.Id, Title = "Acme invoice", CreatedBy = "u" });
        await db.SaveChangesAsync();
        var service = new SuiteSearchService(db, new SentinelWorkspaceService(db, TimeProvider.System));

        var filtered = await service.SearchAsync("t:ticket acme", "u");
        filtered.Should().OnlyContain(item => item.Category == "Support ticket");
        filtered.Should().HaveCount(2);

        var listed = await service.SearchAsync("t:tickets", "u");
        listed.Select(item => item.Title).Should().Equal("Acme newer ticket", "Acme older ticket");

        (await service.SearchAsync("type:invoice", "u")).Should().ContainSingle(item => item.Title == "Acme invoice");
    }

    [Fact]
    public async Task ABusySemanticSearch_ShouldNotHoldUpKeywordResults()
    {
        await using var db = await CreateDbAsync();
        db.Contacts.Add(new Contact { FullName = "Dana Rivers", CreatedBy = "u" });
        await db.SaveChangesAsync();
        var service = new SuiteSearchService(db, new SentinelWorkspaceService(db, TimeProvider.System), new StalledHybridSearch())
        {
            SemanticBudget = TimeSpan.FromMilliseconds(100)
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var results = await service.SearchAsync("dana", "u");

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        results.Should().ContainSingle(item => item.Title == "Dana Rivers");
    }

    // Stands in for an embedding stuck behind a busy local Ollama.
    private sealed class StalledHybridSearch : GwsBusinessSuite.Application.SemanticSearch.IHybridSearchService
    {
        public async Task<IReadOnlyList<GwsBusinessSuite.Application.SemanticSearch.SemanticSearchHit>> SearchAsync(
            string query, IReadOnlyCollection<string>? sourceTypes = null, int take = 20, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return [];
        }

        public Task<GwsBusinessSuite.Application.SemanticSearch.SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RebuildAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("t:ticket acme", "acme", "Support ticket")]
    [InlineData("acme TYPE:Invoices", "acme", "Billing invoice")]
    [InlineData("t:post", "", "Article")]
    [InlineData("t:foo acme", "t:foo acme", null)]
    [InlineData("  plain   words ", "plain words", null)]
    public void Parse_ShouldSplitTypeFiltersFromText(string raw, string text, string? category)
    {
        var parsed = SuiteSearchQuery.Parse(raw);
        parsed.Text.Should().Be(text);
        if (category is null) parsed.IsFiltered.Should().BeFalse();
        else parsed.Categories.Should().Equal(category);
    }

    [Fact]
    public void Parse_SentinelFilter_ShouldCoverPagesAndDatabases() =>
        SuiteSearchQuery.Parse("t:sentinel").Categories.Should().BeEquivalentTo(["Sentinel page", "Sentinel database"]);

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
