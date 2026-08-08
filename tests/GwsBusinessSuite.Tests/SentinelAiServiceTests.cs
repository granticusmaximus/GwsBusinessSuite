using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelAiServiceTests
{
    [Fact]
    public async Task StreamAsync_ShouldYieldFragmentsThenAPersistedRunCitingMatchedPages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var wiki = new WikiService(db);
        var page = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Launch runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Flip the blue switch before liftoff.")], new Dictionary<string, string>())])
        }, "u");

        var ollama = new FakeStreamingOllamaService(["The ", "blue switch ", "starts the sequence."]);
        var factory = new FakeAppDbContextFactory(options);
        var service = new SentinelAiService(
            factory, ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        // SentinelWorkspaceService.SearchAsync requires every instruction token to appear in
        // the candidate's title/content (an AND match, not a relevance-ranked OR), so the
        // instruction has to actually match the seeded page's text for a citation to occur.
        var chunks = new List<SentinelAiStreamChunk>();
        await foreach (var chunk in service.StreamAsync(null, SentinelAiActions.Ask, "blue switch", "grant"))
        {
            chunks.Add(chunk);
        }

        chunks.Where(chunk => chunk.CompletedRun is null).Select(chunk => chunk.Delta)
            .Should().Equal("The ", "blue switch ", "starts the sequence.");

        var completed = chunks.Should().ContainSingle(chunk => chunk.CompletedRun != null).Subject.CompletedRun!;
        completed.ConversationId.Should().NotBeEmpty();
        completed.Output.Should().Be("The blue switch starts the sequence.");
        completed.Citations.Should().ContainSingle(citation => citation.TargetId == page.Id && citation.Title == "Launch runbook");

        var persisted = await db.SentinelAiRuns.AsNoTracking().SingleAsync();
        persisted.Output.Should().Be("The blue switch starts the sequence.");
        persisted.ConversationId.Should().Be(completed.ConversationId);
        persisted.CitationsJson.Should().Contain(page.Id.ToString());

        var listed = await service.ListRunsAsync(null);
        listed.Should().ContainSingle(run => run.Id == completed.Id && run.Citations.Count == 1);

        var conversations = await service.ListConversationsAsync("grant");
        conversations.Should().ContainSingle(conversation =>
            conversation.Id == completed.ConversationId
            && conversation.ExchangeCount == 1
            && conversation.Title == "blue switch");

        var conversationRuns = await service.ListConversationRunsAsync(completed.ConversationId, "grant");
        conversationRuns.Should().ContainSingle(run => run.Id == completed.Id);
    }

    [Fact]
    public async Task StreamAsync_ShouldSaveAFailedRun_WhenOllamaTimesOutMidStream()
    {
        // Regression guard: a real timeout (the linked timeoutCts's CancelAfter firing after
        // OllamaTimeoutMinutesOverride/DefaultTimeoutMinutes) had zero test coverage - only the
        // success-path stream was ever exercised. TimingOutOllamaService throws the exact
        // OperationCanceledException shape a real CancelAfter produces, so this exercises the
        // "degrade to a saved Failed run instead of an unhandled exception" path deterministically,
        // without a test actually waiting out a real multi-minute timeout window.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new TimingOutOllamaService(["Partial answer before it hangs. "]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        var act = async () =>
        {
            await foreach (var _ in service.StreamAsync(null, SentinelAiActions.Ask, "status update", "grant"))
            {
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        var persisted = await db.SentinelAiRuns.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(SentinelAiRunStatuses.Failed);
        persisted.Output.Should().Be("Generation failed before producing a reviewable response.");
    }

    [Fact]
    public async Task StreamAsync_ShouldRejectAnUnknownActionBeforeCallingOllama()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["should not be called"]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        var act = async () =>
        {
            await foreach (var _ in service.StreamAsync(null, "not-a-real-action", "hello", "grant")) { }
        };

        await act.Should().ThrowAsync<ArgumentException>();
        ollama.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task StreamAsync_ShouldRejectAnOversizedPromptBeforeCallingOllama()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["should not be called"]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());
        var oversizedPrompt = new string('x', SentinelGptDefaults.MaxInstructionLength + 1);

        var act = async () =>
        {
            await foreach (var _ in service.StreamAsync(null, SentinelAiActions.Ask, oversizedPrompt, "grant")) { }
        };

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*32,000 characters*");
        ollama.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ReviewAsync_ShouldApproveOrRejectAPersistedRun()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["A short answer."]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        SentinelAiRunView? completed = null;
        await foreach (var chunk in service.StreamAsync(null, SentinelAiActions.Ask, "anything", "grant"))
        {
            if (chunk.CompletedRun is not null) completed = chunk.CompletedRun;
        }

        await service.ReviewAsync(completed!.Id, approved: true, "reviewer");

        var run = await db.SentinelAiRuns.AsNoTracking().SingleAsync();
        run.Status.Should().Be("approved");
        run.ReviewedBy.Should().Be("reviewer");
        run.ReviewedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task StreamAgentConversationAsync_ShouldGroundOnSuiteDataAndOptionalWebResearch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Contacts.Add(new GwsBusinessSuite.Domain.Entities.Contact
        {
            FullName = "Ada Lovelace",
            Company = "Analytical Engines",
            Email = "private@example.test",
            Status = "Customer",
            CreatedBy = "grant"
        });
        db.AutomationCredentials.Add(new GwsBusinessSuite.Domain.Entities.AutomationCredential
        {
            Name = "Production API",
            TypeKey = "httpHeader",
            ProtectedData = "must-never-enter-the-prompt",
            Description = "Secret production credential",
            CreatedBy = "grant"
        });
        db.SentinelAiRuns.Add(new GwsBusinessSuite.Domain.Entities.SentinelAiRun
        {
            ConversationId = Guid.NewGuid(),
            Action = SentinelAiActions.Ask,
            Instruction = "ASP.NET framework guidance",
            Output = "Prefer the repository target framework and verify version-sensitive APIs.",
            Status = GwsBusinessSuite.Domain.Entities.SentinelAiRunStatuses.Approved,
            Model = "sentinelgpt",
            ReviewedAt = DateTimeOffset.UtcNow,
            ReviewedBy = "grant",
            CreatedBy = "grant"
        });
        await db.SaveChangesAsync();

        var ollama = new FakeStreamingOllamaService(["A grounded answer."]);
        var web = new FakeWebSearchService([
            new OllamaWebSearchResult(
                "General current framework discussion",
                "https://example.test/framework",
                "Untrusted current framework discussion.")
        ], [
            new OllamaWebSearchResult(
                "Official ASP.NET Core guidance",
                "https://learn.microsoft.com/aspnet/core/",
                "Current framework guidance."),
            new OllamaWebSearchResult(
                "Impersonated documentation",
                "https://not-microsoft.example/docs",
                "This result must not enter the official documentation section.")
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System),
            CreateCache(),
            web);

        SentinelAiRunView? completed = null;
        await foreach (var chunk in service.StreamAgentConversationAsync(
            Guid.NewGuid(), null, "Tell me about Ada and current ASP.NET guidance", "grant",
            includeInternet: true, useDeepAnalysis: true))
        {
            completed ??= chunk.CompletedRun;
        }

        ollama.LastUserPrompt.Should().Contain("GWS BUSINESS SUITE LIVE OVERVIEW");
        ollama.LastUserPrompt.Should().Contain("Ada Lovelace");
        ollama.LastUserPrompt.Should().Contain("Current framework guidance");
        ollama.LastUserPrompt.Should().Contain("HUMAN-APPROVED SENTINELGPT MEMORY");
        ollama.LastUserPrompt.Should().Contain("verify version-sensitive APIs");
        ollama.LastSystemPrompt.Should().Contain("truth rather than agreement");
        ollama.LastSystemPrompt.Should().Contain("official Microsoft Learn");
        ollama.RequestedModels.Should().Contain("qwen2.5-coder");
        ollama.RequestedModels.Should().Contain("deepseek-r1");
        ollama.LastUserPrompt.Should().NotContain("private@example.test");
        ollama.LastUserPrompt.Should().NotContain("must-never-enter-the-prompt");
        web.Queries.Should().Contain(query => query.StartsWith("site:learn.microsoft.com"));
        completed!.Citations.Should().Contain(item => item.SourceType == "gws" && item.Title == "Ada Lovelace");
        completed.Citations.Should().Contain(item => item.SourceType == "microsoft-docs"
            && item.Url == "https://learn.microsoft.com/aspnet/core/");
        completed.Citations.Should().NotContain(item => item.SourceType == "microsoft-docs"
            && item.Url == "https://not-microsoft.example/docs");
    }

    [Fact]
    public async Task StreamAgentConversationAsync_ShouldNotLoadTeacherModelsForOrdinaryLongText()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["A concise summary."]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System),
            CreateCache());
        var longOrdinaryText = string.Join(' ', Enumerable.Repeat("meeting notes and customer correspondence", 30));

        await foreach (var _ in service.StreamAgentConversationAsync(
            Guid.NewGuid(), null, longOrdinaryText, "grant",
            includeInternet: false, useDeepAnalysis: false))
        {
        }

        ollama.RequestedModels.Should().Equal(SentinelGptDefaults.Model);
    }

    [Fact]
    public async Task StreamAgentConversationAsync_FastModeShouldSkipTeachersForTechnicalPrompt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["A fast answer."]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System),
            CreateCache());

        await foreach (var _ in service.StreamAgentConversationAsync(
            Guid.NewGuid(), null, "Improve SentinelGPT performance and security", "grant",
            includeInternet: false, useDeepAnalysis: false,
            maxOutputTokens: SentinelGptResponseBudgets.Concise))
        {
        }

        ollama.RequestedModels.Should().Equal(SentinelGptDefaults.Model);
        ollama.LastMaxOutputTokens.Should().Be(SentinelGptResponseBudgets.Concise);
    }

    [Fact]
    public async Task StreamAgentConversationAsync_ShouldCacheSanitizedSuiteContextPerSearchTerms()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ada = new GwsBusinessSuite.Domain.Entities.Contact
        {
            FullName = "Ada Lovelace",
            Company = "Analytical Engines",
            Email = "ada-private@example.test",
            Status = "Customer",
            CreatedBy = "grant"
        };
        db.Contacts.AddRange(
            ada,
            new GwsBusinessSuite.Domain.Entities.Contact
            {
                FullName = "Grace Hopper",
                Company = "Navy",
                Email = "grace-private@example.test",
                Status = "Lead",
                CreatedBy = "grant"
            });
        await db.SaveChangesAsync();

        var cache = CreateCache();
        var ollama = new FakeStreamingOllamaService(["Answer."]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System),
            cache);

        await DrainAgentResponseAsync(service, "Ada");
        ollama.LastUserPrompt.Should().Contain("Ada Lovelace");
        ollama.LastUserPrompt.Should().Contain("Analytical Engines");
        ollama.LastUserPrompt.Should().NotContain("ada-private@example.test");

        ada.Company = "Updated Company";
        await db.SaveChangesAsync();
        await DrainAgentResponseAsync(service, "Ada");
        ollama.LastUserPrompt.Should().Contain("Analytical Engines");
        ollama.LastUserPrompt.Should().NotContain("Updated Company");

        await DrainAgentResponseAsync(service, "Grace");
        ollama.LastUserPrompt.Should().Contain("Grace Hopper");
        ollama.LastUserPrompt.Should().NotContain("Ada Lovelace");
        ollama.LastUserPrompt.Should().NotContain("grace-private@example.test");
    }

    [Fact]
    public async Task ExecuteModelCommandAsync_ShouldRequireConfirmationBeforeUpdatingAModel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["unused"], ["llama3.2"]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System),
            CreateCache());
        var conversationId = Guid.NewGuid();

        var pending = await service.ExecuteModelCommandAsync(
            conversationId, "/model update llama3.2", "grant", confirmed: false);

        pending.Handled.Should().BeTrue();
        pending.RequiresConfirmation.Should().BeTrue();
        ollama.PulledModels.Should().BeEmpty();
        (await db.SentinelAiRuns.CountAsync()).Should().Be(0);

        var completed = await service.ExecuteModelCommandAsync(
            conversationId, "/model update llama3.2", "grant", confirmed: true);

        completed.CompletedRun!.Output.Should().Contain("refreshed");
        ollama.PulledModels.Should().ContainSingle().Which.Should().Be("llama3.2");
        (await db.SentinelAiRuns.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteModelCommandAsync_ShouldSwitchOnlyToAnInstalledModel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["unused"], ["llama3.2", "qwen3:8b"]);
        var settings = new SiteSettingsService(db);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            settings,
            new SentinelWorkspaceService(db, TimeProvider.System),
            CreateCache());

        var result = await service.ExecuteModelCommandAsync(
            Guid.NewGuid(), "switch to model qwen3:8b", "grant", confirmed: false);

        result.CompletedRun!.Output.Should().Contain("qwen3:8b");
        (await settings.GetSettingsAsync()).OllamaModelOverride.Should().Be("qwen3:8b");
    }

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private static MemoryCache CreateCache() => new(new MemoryCacheOptions());

    private static async Task DrainAgentResponseAsync(SentinelAiService service, string instruction)
    {
        await foreach (var _ in service.StreamAgentConversationAsync(
            Guid.NewGuid(), null, instruction, "grant",
            includeInternet: false, useDeepAnalysis: false))
        {
        }
    }

    private sealed class FakeStreamingOllamaService(
        IReadOnlyList<string> fragments,
        IReadOnlyCollection<string>? installedModels = null) : GwsBusinessSuite.Application.Abstractions.IOllamaService
    {
        public bool WasCalled { get; private set; }
        public string LastUserPrompt { get; private set; } = string.Empty;
        public string LastSystemPrompt { get; private set; } = string.Empty;
        public List<string> PulledModels { get; } = [];
        public List<string> RequestedModels { get; } = [];
        public int? LastMaxOutputTokens { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            RequestedModels.Add(model);
            LastUserPrompt = userPrompt;
            return Task.FromResult(string.Join(string.Empty, fragments));
        }

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            WasCalled = true;
            RequestedModels.Add(model);
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            foreach (var fragment in fragments)
            {
                await Task.Yield();
                yield return fragment;
            }
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            int maxOutputTokens,
            CancellationToken ct = default)
        {
            LastMaxOutputTokens = maxOutputTokens;
            return GenerateStreamAsync(model, systemPrompt, userPrompt, ct);
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(installedModels ?? (IReadOnlyCollection<string>)Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default)
        {
            PulledModels.Add(model);
            return Task.CompletedTask;
        }

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }

    // Simulates a real timeout (the linked timeoutCts's CancelAfter firing) without a test
    // having to actually wait one out: yields whatever streamed before the hang, then throws
    // the same OperationCanceledException shape a real CancelAfter would produce.
    private sealed class TimingOutOllamaService(IReadOnlyList<string> fragmentsBeforeTimeout) : GwsBusinessSuite.Application.Abstractions.IOllamaService
    {
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var fragment in fragmentsBeforeTimeout)
            {
                await Task.Yield();
                yield return fragment;
            }
            throw new OperationCanceledException("Simulated Ollama timeout.");
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(
            string model, string systemPrompt, string userPrompt, int maxOutputTokens, CancellationToken ct = default) =>
            GenerateStreamAsync(model, systemPrompt, userPrompt, ct);

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyCollection<string>)Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class FakeWebSearchService(
        IReadOnlyList<OllamaWebSearchResult> results,
        IReadOnlyList<OllamaWebSearchResult>? microsoftResults = null) : IOllamaWebSearchService
    {
        public bool IsConfigured => true;
        public List<string> Queries { get; } = [];

        public Task<IReadOnlyList<OllamaWebSearchResult>> SearchAsync(
            string query,
            int? maxResults = null,
            CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(
                query.StartsWith("site:learn.microsoft.com", StringComparison.OrdinalIgnoreCase)
                    ? microsoftResults ?? results
                    : results);
        }

        public Task<OllamaWebSearchResult> FetchAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(results[0]);
    }
}
