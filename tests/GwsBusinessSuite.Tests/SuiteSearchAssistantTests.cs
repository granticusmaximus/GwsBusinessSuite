using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.SuiteSearch;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

// SentinelGPT in ⌘K (tech-forward T1b).
public sealed class SuiteSearchAssistantTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ApplicationDbContext _db = null!;
    private SuiteSearchService _search = null!;
    private Guid _danaId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var dana = new Contact { FullName = "Dana Rivers", Status = ContactStatuses.Lead, FollowUpDate = now.AddDays(-2), CreatedBy = "u" };
        var omar = new Contact { FullName = "Omar Hale", Status = ContactStatuses.Customer, CreatedBy = "u" };
        _db.Contacts.AddRange(dana, omar);
        await _db.SaveChangesAsync();
        _danaId = dana.Id;
        _db.Invoices.AddRange(
            new Invoice { ContactId = dana.Id, Title = "October retainer", Status = InvoiceStatuses.Sent, DueDate = now.AddDays(-10), CreatedBy = "u" },
            new Invoice { ContactId = dana.Id, Title = "November retainer", Status = InvoiceStatuses.Sent, DueDate = now.AddDays(20), CreatedBy = "u" },
            new Invoice { ContactId = omar.Id, Title = "Brand refresh", Status = InvoiceStatuses.Sent, DueDate = now.AddDays(-5), CreatedBy = "u" });
        _db.SupportTickets.AddRange(
            new SupportTicket { ContactId = dana.Id, Subject = "Login loop", Status = SupportTicketStatuses.Open, CreatedBy = "u", CreatedAt = now.AddDays(-2) },
            new SupportTicket { ContactId = omar.Id, Subject = "Old question", Status = SupportTicketStatuses.Open, CreatedBy = "u", CreatedAt = now.AddDays(-40) });
        await _db.SaveChangesAsync();
        _search = new SuiteSearchService(_db, new SentinelWorkspaceService(_db, TimeProvider.System));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public void ToolArguments_ShouldBeCheckedAndCapped()
    {
        var request = SuiteSearchRequestRules.FromToolArguments("""
            {"kind":"Invoices","overdue":"true","within_days":900,"contact":"Dana","summary":"Unpaid invoices for Dana"}
            """);
        request.Should().NotBeNull();
        request!.Kind.Should().Be("invoices");
        request.OverdueOnly.Should().BeTrue();
        request.WithinDays.Should().Be(365);
        request.Contact.Should().Be("Dana");
        request.Text.Should().BeEmpty();

        SuiteSearchRequestRules.FromToolArguments("""{"kind":"passwords","summary":"x"}""").Should().BeNull("only known kinds are searched");
        SuiteSearchRequestRules.FromToolArguments("not json").Should().BeNull();
        SuiteSearchRequestRules.FromToolArguments("""{"kind":"tickets"}""")!.Summary.Should().Be("SentinelGPT's search");
    }

    [Fact]
    public void TheRequestsOwnWords_ShouldFillWhatTheModelLeftOut()
    {
        var bare = new SuiteSearchRequest("tickets", "", null, false, 7, null, "Tickets this week");
        var filled = SuiteSearchRequestRules.FillFromQuestion(bare, "open urgent tickets this week");
        filled.Status.Should().Be("Open");
        filled.Priority.Should().Be("Urgent");

        SuiteSearchRequestRules.FillFromQuestion(bare with { Status = "Pending" }, "open tickets").Status.Should().Be("Pending", "the model's own status stands");
        SuiteSearchRequestRules.FillFromQuestion(new("invoices", "", null, false, null, null, "x"), "unpaid invoices for Dana").Status.Should().Be("Sent");

        SuiteSearchRequestRules.OwnerFromQuestion(new("invoices", "", "Sent", false, null, null, "x"), "unpaid invoices for Dana Rivers").Should().Be("Dana Rivers");
        SuiteSearchRequestRules.OwnerFromQuestion(new("invoices", "", null, false, null, "Omar", "x"), "invoices for Dana").Should().BeNull("the model named one");
        SuiteSearchRequestRules.OwnerFromQuestion(new("posts", "", null, false, null, null, "x"), "posts for Dana").Should().BeNull("posts don't belong to a contact");
    }

    [Fact]
    public async Task Ask_ShouldUseANameFromTheRequest_OnlyWhenSuchAContactExists()
    {
        var withoutContact = new ScriptedOllama(new OllamaToolCall(SuiteSearchRequestRules.ToolName, """{"kind":"invoices","status":"Sent","summary":"Unpaid invoices"}"""));
        var assistant = new SuiteSearchAssistantService(withoutContact, new Settings(null), _search, TimeProvider.System, NullLogger<SuiteSearchAssistantService>.Instance);
        (await assistant.AskAsync("unpaid invoices for Omar", "u"))!.Results.Select(r => r.Title).Should().Equal("Brand refresh");
        (await assistant.AskAsync("unpaid invoices for Nobody", "u"))!.Results.Should().HaveCount(3, "no such contact, so all unpaid invoices");
    }

    [Theory]
    [InlineData("invoices", "Unpaid", "Sent")]
    [InlineData("invoices", "paid", "Paid")]
    [InlineData("tickets", "unresolved", "Open")]
    [InlineData("posts", "pending review", "PendingReview")]
    [InlineData("deals", "proposal sent", "ProposalSent")]
    [InlineData("tickets", "Urgent", null)]
    [InlineData("contacts", null, null)]
    public void Status_ShouldBeAKindsRealStatus_OrDropped(string kind, string? status, string? expected) =>
        SuiteSearchRequestRules.NormalizeStatus(kind, status).Should().Be(expected);

    [Theory]
    [InlineData("unpaid invoices for Dana", true)]
    [InlineData("who owes me money?", true)]
    [InlineData("dana", false)]
    [InlineData("dana rivers", false)]
    [InlineData("t:ticket login loop again", false)]
    public void OnlyPlainLanguageRequests_ShouldGoToSentinelGpt(string query, bool expected) =>
        SuiteSearchRequestRules.LooksLikeRequest(query).Should().Be(expected);

    [Fact]
    public async Task Find_ShouldApplyOverdueAndContact()
    {
        var results = await _search.FindAsync(new SuiteSearchRequest("invoices", "", null, true, null, "dana", "Overdue invoices for Dana"));
        results.Select(r => r.Title).Should().Equal("October retainer");
        results.Single().Url.Should().StartWith("/admin/billing?invoice=");

        (await _search.FindAsync(new SuiteSearchRequest("invoices", "", null, false, null, "nobody", "x"))).Should().BeEmpty();

        // "invoices for Dana" with the name in text still finds Dana's invoices.
        (await _search.FindAsync(new SuiteSearchRequest("invoices", "Dana", "Sent", false, null, null, "Unpaid invoices for Dana")))
            .Select(r => r.Title).Should().BeEquivalentTo(["October retainer", "November retainer"]);
    }

    [Fact]
    public async Task Find_ShouldFilterTicketsByPriority()
    {
        _db.SupportTickets.Add(new SupportTicket { ContactId = _danaId, Subject = "Site down", Status = SupportTicketStatuses.Open, Priority = SupportTicketPriorities.Urgent, CreatedBy = "u" });
        await _db.SaveChangesAsync();
        var request = SuiteSearchRequestRules.FromToolArguments("""{"kind":"tickets","priority":"critical","summary":"Urgent tickets"}""");
        request!.Priority.Should().Be("Urgent");
        (await _search.FindAsync(request)).Select(r => r.Title).Should().Equal("Site down");
    }

    [Fact]
    public async Task Find_ShouldApplyStatusRecencyAndFollowUps()
    {
        var recent = await _search.FindAsync(new SuiteSearchRequest("tickets", "", "open", false, 7, null, "Open tickets this week"));
        recent.Select(r => r.Title).Should().Equal("Login loop");

        var followUps = await _search.FindAsync(new SuiteSearchRequest("contacts", "", null, true, null, null, "Follow-ups due"));
        followUps.Should().ContainSingle(r => r.Url == $"/admin/crm/contacts/{_danaId}");

        var customers = await _search.FindAsync(new SuiteSearchRequest("contacts", "", "Customer", false, null, null, "Customers"));
        customers.Select(r => r.Title).Should().Equal("Omar Hale");
    }

    [Fact]
    public async Task Ask_ShouldRunTheModelsToolCall_AsAStructuredSearch()
    {
        var ollama = new ScriptedOllama(new OllamaToolCall(SuiteSearchRequestRules.ToolName,
            """{"kind":"invoices","overdue":true,"contact":"Dana","summary":"Overdue invoices for Dana Rivers"}"""));
        var assistant = new SuiteSearchAssistantService(ollama, new Settings(null), _search, TimeProvider.System, NullLogger<SuiteSearchAssistantService>.Instance);

        var answer = await assistant.AskAsync("which of Dana's invoices are late?", "u");

        answer.Should().NotBeNull();
        answer!.Summary.Should().Be("Overdue invoices for Dana Rivers");
        answer.Results.Select(r => r.Title).Should().Equal("October retainer");
        ollama.LastModel.Should().Be("sentinelgpt");
        ollama.LastTools!.Single().Name.Should().Be(SuiteSearchRequestRules.ToolName);
    }

    [Fact]
    public async Task Ask_ShouldGiveNoAnswer_WhenThereIsNoToolCallOrOllamaFails_OrForAShortLookup()
    {
        var silent = new ScriptedOllama(null);
        var assistant = new SuiteSearchAssistantService(silent, new Settings("llama3.2"), _search, TimeProvider.System, NullLogger<SuiteSearchAssistantService>.Instance);
        (await assistant.AskAsync("tell me a joke about invoices", "u")).Should().BeNull();
        silent.LastModel.Should().Be("llama3.2", "the site's model override is used");

        var failing = new ScriptedOllama(null, fail: true);
        (await new SuiteSearchAssistantService(failing, new Settings(null), _search, TimeProvider.System, NullLogger<SuiteSearchAssistantService>.Instance)
            .AskAsync("unpaid invoices for Dana", "u")).Should().BeNull();

        var untouched = new ScriptedOllama(null);
        (await new SuiteSearchAssistantService(untouched, new Settings(null), _search, TimeProvider.System, NullLogger<SuiteSearchAssistantService>.Instance)
            .AskAsync("dana", "u")).Should().BeNull();
        untouched.LastModel.Should().BeNull("a one-word lookup never goes to the model");
    }

    private sealed class Settings(string? model) : ISiteSettingsService
    {
        public Task<SiteSettingsView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteSettingsView(12, null, null, model, null, null, 8));

        public Task SaveSettingsAsync(SiteSettingsView settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ScriptedOllama(OllamaToolCall? call, bool fail = false) : IOllamaService
    {
        public string? LastModel { get; private set; }
        public IReadOnlyList<OllamaToolDefinition>? LastTools { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<OllamaChatResponse> ChatAsync(string model, IReadOnlyList<OllamaChatMessage> messages,
            IReadOnlyList<OllamaToolDefinition>? tools = null, CancellationToken ct = default)
        {
            LastModel = model;
            LastTools = tools;
            if (fail) throw new HttpRequestException("Simulated Ollama failure.");
            return Task.FromResult(new OllamaChatResponse(string.Empty, call is null ? [] : [call]));
        }
    }
}
