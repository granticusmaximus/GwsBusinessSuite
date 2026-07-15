using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.AppGeneration;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.CmsKnowledge;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Tests;

public sealed class AppGenerationServiceTests
{
    private const string ValidPlanReply = """
        Here's a starting plan for a pricing page.

        ```json
        {"pages":[{"title":"Pricing","slug":"pricing","metaDescription":"See our plans","layout":{"sections":[{"label":"Hero","background":"transparent","padding":"lg","columnLayout":"full","columns":[{"span":12,"widgets":[{"widgetType":"heading","props":{"text":"Pricing"}}]}]}]}}]}
        ```
        """;

    private const string TwoPagePlanReply = """
        Here's a plan with two pages.

        ```json
        {"pages":[{"title":"Pricing","slug":"pricing","metaDescription":"See our plans","layout":{"sections":[{"label":"Hero","background":"transparent","padding":"lg","columnLayout":"full","columns":[{"span":12,"widgets":[{"widgetType":"heading","props":{"text":"Pricing"}}]}]}]}},{"title":"FAQ","slug":"faq","metaDescription":"Answers to common questions","layout":{"sections":[{"label":"Hero","background":"transparent","padding":"lg","columnLayout":"full","columns":[{"span":12,"widgets":[{"widgetType":"heading","props":{"text":"FAQ"}}]}]}]}}]}
        ```
        """;

    private const string ClarifyingOnlyReply = "What tone should the page have - playful or formal?";

    private const string MalformedPlanReply = """
        Here's the plan.

        ```json
        {"pages": THIS IS NOT VALID JSON
        ```
        """;

    [Fact]
    public async Task StartAsync_ShouldCreateRequest_AndParsePagePlan_WhenOllamaReturnsValidJson()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);

        var result = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        result.Success.Should().BeTrue();
        result.Request.Should().NotBeNull();
        result.Request!.Status.Should().Be(AppGenerationRequestStatuses.Drafting);
        result.Request.GeneratedPages.Should().ContainSingle(p => p.Title == "Pricing" && p.Slug == "pricing");
        result.Request.Messages.Should().HaveCount(2);
        result.Request.Messages[0].Role.Should().Be(AppGenerationMessageRoles.User);
        result.Request.Messages[1].Role.Should().Be(AppGenerationMessageRoles.Assistant);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnFailure_WhenSiteNotFound()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeOllamaService());

        var result = await service.StartAsync(new StartAppGenerationInput(Guid.NewGuid(), "Title", "Prompt"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task GetRequestAsync_ShouldReturnNull_WhenRequestNotFound()
    {
        await using var db = await CreateDbAsync();
        var service = CreateService(db, new FakeOllamaService());

        var result = await service.GetRequestAsync(Guid.NewGuid());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRequestAsync_ShouldReturnFullView_WhenFound()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        var result = await service.GetRequestAsync(started.Request!.Id);

        result.Should().NotBeNull();
        result!.Id.Should().Be(started.Request.Id);
        result.TargetSiteName.Should().Be(site.Name);
        result.GeneratedPages.Should().ContainSingle(p => p.Title == "Pricing");
        result.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task StartAsync_ShouldReturnFailure_ButKeepThePersistedUserMessage_WhenOllamaThrows()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { ShouldThrow = true };
        var service = CreateService(db, ollama);

        var result = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Ollama generation failed");
        result.Request.Should().NotBeNull();
        result.Request!.Status.Should().Be(AppGenerationRequestStatuses.Drafting);
        result.Request.Messages.Should().ContainSingle(m => m.Role == AppGenerationMessageRoles.User && m.Content == "Build a pricing page");
    }

    [Fact]
    public async Task SendMessageAsync_ShouldReturnFailure_WhenOllamaThrowsOnASubsequentTurn()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        ollama.ShouldThrow = true;
        var result = await service.SendMessageAsync(started.Request!.Id, "Make it punchier");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Ollama generation failed");
        // The plan from the earlier successful turn must survive an unrelated failure later.
        result.Request!.GeneratedPages.Should().ContainSingle(p => p.Title == "Pricing");
    }

    [Fact]
    public async Task StartAsync_ShouldLeavePlanEmpty_WhenOllamaOnlyAsksClarifyingQuestions()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ClarifyingOnlyReply };
        var service = CreateService(db, ollama);

        var result = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        result.Success.Should().BeTrue();
        result.Request!.GeneratedPages.Should().BeEmpty();
        result.Request.Status.Should().Be(AppGenerationRequestStatuses.Drafting);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldAppendTurn_AndKeepPreviousPlan_WhenNewReplyIsMalformed()
    {
        // Defensive-parsing regression: a malformed fenced json block must not crash the
        // turn or wipe out a previously-agreed plan (same posture as CJ commission parsing).
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        ollama.GenerateTextResult = MalformedPlanReply;
        var result = await service.SendMessageAsync(started.Request!.Id, "Make it punchier");

        result.Success.Should().BeTrue();
        result.Request!.GeneratedPages.Should().ContainSingle(p => p.Title == "Pricing");
        result.Request.Messages.Should().HaveCount(4);
    }

    [Fact]
    public async Task SendMessageAsync_ShouldFail_WhenRequestIsNoLongerDrafting()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));
        await service.SubmitForApprovalAsync(started.Request!.Id);

        var result = await service.SendMessageAsync(started.Request.Id, "One more change");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no longer in drafting");
    }

    [Fact]
    public async Task SubmitForApprovalAsync_ShouldFail_WhenNoPagesDrafted()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ClarifyingOnlyReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        var result = await service.SubmitForApprovalAsync(started.Request!.Id);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Keep chatting");
    }

    [Fact]
    public async Task SubmitForApprovalAsync_ShouldMovePendingApproval_WhenPlanExists()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        var result = await service.SubmitForApprovalAsync(started.Request!.Id);

        result.Success.Should().BeTrue();
        result.Request!.Status.Should().Be(AppGenerationRequestStatuses.PendingApproval);
        (await service.CountPendingApprovalAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ApproveAsync_ShouldCreateCmsPage_AndMarkApproved()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));
        await service.SubmitForApprovalAsync(started.Request!.Id);

        var result = await service.ApproveAsync(started.Request.Id);

        result.Success.Should().BeTrue();
        result.Request!.Status.Should().Be(AppGenerationRequestStatuses.Approved);
        result.Request.ApprovedAt.Should().NotBeNull();
        var createdPages = db.CmsPages.Where(p => p.SiteId == site.Id).ToList();
        createdPages.Should().ContainSingle(p => p.Title == "Pricing" && p.Status == CmsPageStatuses.Draft);
    }

    [Fact]
    public async Task ApproveAsync_ShouldCreateAllPages_ForAMultiPagePlan_InOneTransaction()
    {
        // Regression coverage for wrapping ApproveAsync's page-creation loop in a DB
        // transaction (see IAppDbContext.BeginTransactionAsync) - a multi-page plan should
        // still commit every page together on the happy path.
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = TwoPagePlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Two pages", "Build two pages"));
        await service.SubmitForApprovalAsync(started.Request!.Id);

        var result = await service.ApproveAsync(started.Request.Id);

        result.Success.Should().BeTrue();
        var createdPages = db.CmsPages.Where(p => p.SiteId == site.Id).ToList();
        createdPages.Should().HaveCount(2);
        createdPages.Should().Contain(p => p.Title == "Pricing");
        createdPages.Should().Contain(p => p.Title == "FAQ");
    }

    [Fact]
    public async Task ApproveAsync_ShouldFail_WhenNotPendingApproval()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));

        var result = await service.ApproveAsync(started.Request!.Id);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("pending approval");
    }

    [Fact]
    public async Task RejectAsync_ShouldMarkRejected_WithReason_AndNotCreatePages()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var started = await service.StartAsync(new StartAppGenerationInput(site.Id, "Pricing page", "Build a pricing page"));
        await service.SubmitForApprovalAsync(started.Request!.Id);

        var result = await service.RejectAsync(started.Request.Id, "Not aligned with brand voice");

        result.Success.Should().BeTrue();
        result.Request!.Status.Should().Be(AppGenerationRequestStatuses.Rejected);
        result.Request.RejectionReason.Should().Be("Not aligned with brand voice");
        db.CmsPages.Where(p => p.SiteId == site.Id).Should().BeEmpty();
    }

    [Fact]
    public async Task ListRequestsAsync_ShouldFilterByStatus()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);
        var drafting = await service.StartAsync(new StartAppGenerationInput(site.Id, "Drafting one", "Build a page"));
        var toSubmit = await service.StartAsync(new StartAppGenerationInput(site.Id, "Submitted one", "Build another page"));
        await service.SubmitForApprovalAsync(toSubmit.Request!.Id);

        var pending = await service.ListRequestsAsync(AppGenerationRequestStatuses.PendingApproval);

        pending.Should().ContainSingle(r => r.Id == toSubmit.Request.Id);
        pending.Should().NotContain(r => r.Id == drafting.Request!.Id);
    }

    [Fact]
    public async Task StartAsync_ShouldIncludeMatchingCmsKnowledgeEntry_InTheSystemPrompt()
    {
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        await CreateKnowledgeEntryAsync(db, "Dynamic content loops", "Pull a live list of posts into a page.");
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);

        await service.StartAsync(new StartAppGenerationInput(site.Id, "Loop page", "Add a dynamic content loop of my latest posts"));

        ollama.LastSystemPrompt.Should().Contain("Dynamic content loops");
    }

    [Fact]
    public async Task StartAsync_ShouldOmitReferenceNotesSection_WhenNothingMatches()
    {
        // A query with no plausible keyword overlap against any CmsKnowledge entry (the
        // real seeded library is present here via EnsureCreatedAsync's HasData, so this
        // can't just use an ordinary phrase like "pricing page" - "page" alone matches
        // several seeded entries).
        await using var db = await CreateDbAsync();
        var site = await CreateSiteAsync(db);
        var ollama = new FakeOllamaService { GenerateTextResult = ValidPlanReply };
        var service = CreateService(db, ollama);

        await service.StartAsync(new StartAppGenerationInput(site.Id, "Zzyzx", "zzyzxqqqrrrstuv"));

        ollama.LastSystemPrompt.Should().NotContain("Reference notes");
    }

    private static async Task CreateKnowledgeEntryAsync(ApplicationDbContext db, string capability, string workflowSummary)
    {
        var source = new CmsKnowledgeSource { Key = "test-source", Name = "Test Source" };
        db.CmsKnowledgeSources.Add(source);
        db.CmsKnowledgeEntries.Add(new CmsKnowledgeEntry
        {
            SourceId = source.Id,
            Capability = capability,
            WorkflowSummary = workflowSummary
        });
        await db.SaveChangesAsync();
    }

    private static AppGenerationService CreateService(ApplicationDbContext db, IOllamaService ollama) =>
        new(db, ollama, new SiteSettingsService(db), new CmsBuilderService(db), new CmsKnowledgeService(db), new FixedCurrentUserAccessor("grantwatson"));

    private static async Task<CmsSite> CreateSiteAsync(ApplicationDbContext db)
    {
        var site = new CmsSite { Name = "Main Site", Slug = Guid.NewGuid().ToString("N") };
        db.CmsSites.Add(site);
        await db.SaveChangesAsync();
        return site;
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

    private sealed class FakeOllamaService : IOllamaService
    {
        public string GenerateTextResult { get; set; } = string.Empty;
        public string? LastSystemPrompt { get; private set; }
        public bool ShouldThrow { get; set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            LastSystemPrompt = systemPrompt;
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Ollama is unreachable.");
            }

            return Task.FromResult(GenerateTextResult);
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
