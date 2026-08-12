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
    public async Task DeleteConversationAsync_ShouldRemoveEveryRunInThatConversationButLeaveOthersUntouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var factory = new FakeAppDbContextFactory(options);
        var service = new SentinelAiService(
            factory, new FakeStreamingOllamaService(["Answer one."]), new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        Guid? conversationToDelete = null;
        await foreach (var chunk in service.StreamAsync(null, SentinelAiActions.Ask, "first question", "grant"))
        {
            if (chunk.CompletedRun is { } run) conversationToDelete = run.ConversationId;
        }

        Guid? otherConversation = null;
        await foreach (var chunk in service.StreamAsync(null, SentinelAiActions.Ask, "second question", "grant"))
        {
            if (chunk.CompletedRun is { } run) otherConversation = run.ConversationId;
        }

        await service.DeleteConversationAsync(conversationToDelete!.Value, "grant");

        (await db.SentinelAiRuns.AsNoTracking().Where(run => run.ConversationId == conversationToDelete).ToListAsync())
            .Should().BeEmpty();
        (await db.SentinelAiRuns.AsNoTracking().Where(run => run.ConversationId == otherConversation).ToListAsync())
            .Should().ContainSingle();
        (await service.ListConversationsAsync("grant")).Should().ContainSingle(conversation => conversation.Id == otherConversation);
    }

    [Fact]
    public async Task DeleteConversationAsync_ShouldNotDeleteAnotherUsersConversation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var factory = new FakeAppDbContextFactory(options);
        var service = new SentinelAiService(
            factory, new FakeStreamingOllamaService(["Answer."]), new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        Guid? conversationId = null;
        await foreach (var chunk in service.StreamAsync(null, SentinelAiActions.Ask, "a question", "grant"))
        {
            if (chunk.CompletedRun is { } run) conversationId = run.ConversationId;
        }

        await service.DeleteConversationAsync(conversationId!.Value, "someone-else");

        (await db.SentinelAiRuns.AsNoTracking().Where(run => run.ConversationId == conversationId).ToListAsync())
            .Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteConversationAsync_ForAnUnknownConversation_ShouldNotThrow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var factory = new FakeAppDbContextFactory(options);
        var service = new SentinelAiService(
            factory, new FakeStreamingOllamaService(["Answer."]), new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        var act = () => service.DeleteConversationAsync(Guid.NewGuid(), "grant");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StreamAsync_ShouldNotGroundOnDeniedSearchResultsOrADeniedPinnedPage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var wiki = new WikiService(db);
        var allowed = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Allowed runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("The blue switch is approved context.")], new Dictionary<string, string>())])
        }, "u");
        var denied = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Denied runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("SECRET-PIN blue switch must stay hidden.")], new Dictionary<string, string>())])
        }, "u");
        var access = new SentinelAccessService(db);
        await access.SetPermissionAsync(allowed.Id, false, "member", SentinelAccessLevels.View, "owner");
        var ollama = new FakeStreamingOllamaService(["Grounded answer."]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options),
            ollama,
            new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System, access),
            CreateCache(),
            accessService: access);

        SentinelAiRunView? completed = null;
        await foreach (var chunk in service.StreamAsync(
            denied.Id, SentinelAiActions.Ask, "blue switch", "member"))
        {
            completed ??= chunk.CompletedRun;
        }

        ollama.LastUserPrompt.Should().Contain("approved context");
        ollama.LastUserPrompt.Should().NotContain("SECRET-PIN");
        completed!.Citations.Should().ContainSingle(citation => citation.TargetId == allowed.Id);
        completed.Citations.Should().NotContain(citation => citation.TargetId == denied.Id);
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
    public async Task StreamAgentConversationAsync_ShouldGroundQuestionsAboutASpecificDealInItsStageAndNotes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var contact = new GwsBusinessSuite.Domain.Entities.Contact { FullName = "Ada Lovelace", CreatedBy = "grant" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        db.Deals.Add(new GwsBusinessSuite.Domain.Entities.Deal
        {
            ContactId = contact.Id,
            Title = "Acme Renewal",
            Stage = GwsBusinessSuite.Domain.Entities.DealStages.Negotiation,
            ValueUsd = 15000,
            Notes = "Waiting on legal sign-off before they'll move forward.",
            CreatedBy = "grant"
        });
        await db.SaveChangesAsync();

        var ollama = new FakeStreamingOllamaService(["Answer."]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        await DrainAgentResponseAsync(service, "Why did Acme Renewal stall?");

        ollama.LastUserPrompt.Should().Contain("Acme Renewal");
        ollama.LastUserPrompt.Should().Contain(GwsBusinessSuite.Domain.Entities.DealStages.Negotiation);
        ollama.LastUserPrompt.Should().Contain("legal sign-off");
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

    [Fact]
    public async Task SuggestDatabaseRowValuesAsync_ShouldResolveOptionLabelsAndTypedValuesFromModelJson()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var databases = new WikiDatabaseService(db);
        var database = await databases.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await databases.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status", Type = WikiDatabasePropertyTypes.Select,
            Options = [new("todo", "To do", "#ccc"), new("done", "Done", "#0f0")]
        }, "u");
        var priorityProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Priority", Type = WikiDatabasePropertyTypes.Number }, "u");
        var urgentProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Urgent", Type = WikiDatabasePropertyTypes.Checkbox }, "u");
        var notesProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Notes", Type = WikiDatabasePropertyTypes.Text }, "u");

        var titledRow = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(titledRow, titleProperty.Id, "Fix the deploy pipeline");
        var row = await databases.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = titledRow.ToDictionary(kv => kv.Key, kv => kv.Value) }, "u");

        var modelJson = $$"""
            Here you go:
            {
              "Status": "Done",
              "Priority": 3,
              "Urgent": true,
              "Notes": "Blocks the release",
              "Unknown property": "ignored"
            }
            """;
        var ollama = new FakeStreamingOllamaService([modelJson]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            wikiDatabaseService: databases);

        var result = await service.SuggestDatabaseRowValuesAsync(database.Id, row.Id, "grant");

        result.Suggestions.Should().HaveCount(4);
        result.Suggestions.Single(s => s.PropertyId == statusProperty.Id).Value.Should().Be("done");
        result.Suggestions.Single(s => s.PropertyId == priorityProperty.Id).Value.Should().Be("3");
        result.Suggestions.Single(s => s.PropertyId == urgentProperty.Id).Value.Should().Be("true");
        result.Suggestions.Single(s => s.PropertyId == notesProperty.Id).Value.Should().Be("Blocks the release");
    }

    [Fact]
    public async Task SuggestDatabaseRowValuesAsync_ShouldWarnAndSkipAnUnrecognizedSelectOption()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var databases = new WikiDatabaseService(db);
        var database = await databases.CreateDatabaseAsync("Tasks", null, "u");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await databases.SavePropertyAsync(database.Id, new WikiDatabasePropertyEditor
        {
            Name = "Status", Type = WikiDatabasePropertyTypes.Select,
            Options = [new("todo", "To do", "#ccc"), new("done", "Done", "#0f0")]
        }, "u");
        var titledRow = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(titledRow, titleProperty.Id, "Untitled task");
        var row = await databases.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = titledRow.ToDictionary(kv => kv.Key, kv => kv.Value) }, "u");

        var ollama = new FakeStreamingOllamaService(["""{"Status": "Somewhere in between"}"""]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            wikiDatabaseService: databases);

        var result = await service.SuggestDatabaseRowValuesAsync(database.Id, row.Id, "grant");

        result.Suggestions.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(warning => warning.Contains(statusProperty.Name));
    }

    [Fact]
    public async Task StreamToolCallingConversationAsync_ShouldCallSearchWikiThenAnswerUsingItsResult()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var wiki = new WikiService(db);
        await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = "Deploy runbook",
            BlocksJson = WikiBlockJson.Serialize([
                new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                    [new WikiRichTextSpan("Flip the blue switch before liftoff.")], new Dictionary<string, string>())])
        }, "u");

        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty, [new OllamaToolCall("search_wiki", """{"query":"deploy"}""")]),
            new("The deploy runbook says to flip the blue switch.", [])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        var chunks = new List<SentinelAiStreamChunk>();
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "How do I deploy?", "grant"))
        {
            chunks.Add(chunk);
        }

        chunks.Should().Contain(chunk => chunk.Activity != null && chunk.Activity.Contains("deploy"));
        ollama.Requests.Should().HaveCount(2);
        // The second request must carry the tool's own result back as a "tool" role message,
        // otherwise the model has no way to know what search_wiki actually found.
        var toolMessage = ollama.Requests[1].Single(message => message.Role == "tool");
        toolMessage.Content.Should().Contain("Deploy runbook", because: $"actual tool content was: {toolMessage.Content}");
        chunks[^1].CompletedRun!.Output.Should().Contain("blue switch");
        chunks[^1].CompletedRun!.Citations.Should().Contain(citation => citation.Title == "Deploy runbook");
    }

    [Fact]
    public async Task StreamToolCallingConversationAsync_ShouldFailAfterExceedingTheRoundLimitWithoutAFinalAnswer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Always responds with another tool call, never a final answer - the loop must give up
        // rather than call Ollama forever.
        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty, [new OllamaToolCall("search_wiki", """{"query":"anything"}""")])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache());

        var act = async () =>
        {
            await foreach (var _ in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Loop forever", "grant")) { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*round*");
        (await db.SentinelAiRuns.CountAsync()).Should().Be(1, "the failed attempt should still be persisted for review");
    }

    [Fact]
    public async Task StreamToolCallingConversationAsync_ProposeWrite_ShouldPersistAPendingRunWithoutWritingYet()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true });
        await db.SaveChangesAsync();

        var databases = new WikiDatabaseService(db);
        var database = await databases.CreateDatabaseAsync("Tasks", null, "grant");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "grant");
        var titledRow = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(titledRow, titleProperty.Id, "Ship the release");
        var row = await databases.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = titledRow.ToDictionary(kv => kv.Key, kv => kv.Value) }, "grant");

        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_set_database_row_property",
                    $$"""{"wikiDatabaseId":"{{database.Id}}","rowId":"{{row.Id}}","propertyId":"{{statusProperty.Id}}","value":"Done"}""")
            ])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            accessService: new SentinelAccessService(db), wikiDatabaseService: databases);

        var chunks = new List<SentinelAiStreamChunk>();
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Mark it done", "grant"))
        {
            chunks.Add(chunk);
        }

        chunks[^1].CompletedRun!.Status.Should().Be(SentinelAiRunStatuses.Pending);
        chunks[^1].CompletedRun!.Output.Should().Contain("Status").And.Contain("Done");
        ollama.Requests.Should().HaveCount(1, "the loop must stop after proposing, not keep calling the model");
        var reloadedRow = (await databases.GetDatabaseAsync(database.Id))!.Rows.Single(r => r.Id == row.Id);
        WikiPropertyValues.GetDisplayText(statusProperty, WikiPropertyValues.ParseObject(reloadedRow.PropertyValuesJson), reloadedRow.CreatedAt, reloadedRow.UpdatedAt, reloadedRow.CreatedBy, reloadedRow.UpdatedBy)
            .Should().NotBe("Done");
    }

    [Fact]
    public async Task ResolvePendingToolActionAsync_Approved_ShouldExecuteTheWriteAndMarkCompleted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true });
        await db.SaveChangesAsync();

        var databases = new WikiDatabaseService(db);
        var database = await databases.CreateDatabaseAsync("Tasks", null, "grant");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "grant");
        var titledRow = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(titledRow, titleProperty.Id, "Ship the release");
        var row = await databases.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = titledRow.ToDictionary(kv => kv.Key, kv => kv.Value) }, "grant");

        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_set_database_row_property",
                    $$"""{"wikiDatabaseId":"{{database.Id}}","rowId":"{{row.Id}}","propertyId":"{{statusProperty.Id}}","value":"Done"}""")
            ])
        ]);
        var factory = new FakeAppDbContextFactory(options);
        var service = new SentinelAiService(
            factory, ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            accessService: new SentinelAccessService(db), wikiDatabaseService: databases);

        SentinelAiRunView? pending = null;
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Mark it done", "grant"))
        {
            if (chunk.CompletedRun is not null) pending = chunk.CompletedRun;
        }

        var resolved = await service.ResolvePendingToolActionAsync(pending!.Id, approved: true, "grant");

        resolved.Status.Should().Be(SentinelAiRunStatuses.Completed);
        var reloadedRow = (await databases.GetDatabaseAsync(database.Id))!.Rows.Single(r => r.Id == row.Id);
        WikiPropertyValues.GetDisplayText(statusProperty, WikiPropertyValues.ParseObject(reloadedRow.PropertyValuesJson), reloadedRow.CreatedAt, reloadedRow.UpdatedAt, reloadedRow.CreatedBy, reloadedRow.UpdatedBy)
            .Should().Be("Done");
    }

    [Fact]
    public async Task ResolvePendingToolActionAsync_Declined_ShouldLeaveDataUntouchedAndMarkCancelled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true });
        await db.SaveChangesAsync();

        var databases = new WikiDatabaseService(db);
        var database = await databases.CreateDatabaseAsync("Tasks", null, "grant");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "grant");
        var titledRow = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(titledRow, titleProperty.Id, "Ship the release");
        var row = await databases.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = titledRow.ToDictionary(kv => kv.Key, kv => kv.Value) }, "grant");

        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_set_database_row_property",
                    $$"""{"wikiDatabaseId":"{{database.Id}}","rowId":"{{row.Id}}","propertyId":"{{statusProperty.Id}}","value":"Done"}""")
            ])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            accessService: new SentinelAccessService(db), wikiDatabaseService: databases);

        SentinelAiRunView? pending = null;
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Mark it done", "grant"))
        {
            if (chunk.CompletedRun is not null) pending = chunk.CompletedRun;
        }

        var resolved = await service.ResolvePendingToolActionAsync(pending!.Id, approved: false, "grant");

        resolved.Status.Should().Be(SentinelAiRunStatuses.Cancelled);
        var reloadedRow = (await databases.GetDatabaseAsync(database.Id))!.Rows.Single(r => r.Id == row.Id);
        WikiPropertyValues.GetDisplayText(statusProperty, WikiPropertyValues.ParseObject(reloadedRow.PropertyValuesJson), reloadedRow.CreatedAt, reloadedRow.UpdatedAt, reloadedRow.CreatedBy, reloadedRow.UpdatedBy)
            .Should().NotBe("Done");

        var act = () => service.ResolvePendingToolActionAsync(pending.Id, approved: true, "grant");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already*");
    }

    // --- Part 4.1: agentic workflow authoring (propose_create_automation_workflow) ---

    [Fact]
    public async Task StreamToolCallingConversationAsync_ProposeCreateAutomationWorkflow_ShouldPersistAPendingRunWithoutCreatingAnything()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true });
        await db.SaveChangesAsync();

        var registry = new GwsBusinessSuite.Application.Automation.AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new GwsBusinessSuite.Application.Automation.AutomationWorkflowService(db, registry, TimeProvider.System);
        var nodesJson = """[{"name":"Big deal","typeKey":"crm.dealStageChangedTrigger","parametersJson":"{\"toStage\":\"Won\"}"},{"name":"Alert","typeKey":"core.notify"}]""";
        var connectionsJson = """[{"from":"Big deal","to":"Alert"}]""";
        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_create_automation_workflow",
                    $$"""{"name":"Big deal alert","description":"Notify on big wins","nodes":{{nodesJson}},"connections":{{connectionsJson}}}""")
            ])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            automationNodeRegistry: registry, automationWorkflowService: workflowService);

        var chunks = new List<SentinelAiStreamChunk>();
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Build a workflow for big deals", "grant"))
        {
            chunks.Add(chunk);
        }

        chunks[^1].CompletedRun!.Status.Should().Be(SentinelAiRunStatuses.Pending);
        chunks[^1].CompletedRun!.Output.Should().Contain("Big deal alert").And.Contain("draft");
        (await workflowService.ListAsync()).Should().BeEmpty("nothing should be created until the proposal is confirmed");
    }

    [Fact]
    public async Task ResolvePendingToolActionAsync_ApprovedWorkflowProposal_ShouldCreateAnInactiveDraftWithTheProposedGraph()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true });
        await db.SaveChangesAsync();

        var registry = new GwsBusinessSuite.Application.Automation.AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new GwsBusinessSuite.Application.Automation.AutomationWorkflowService(db, registry, TimeProvider.System);
        var nodesJson = """[{"name":"Big deal","typeKey":"crm.dealStageChangedTrigger","parametersJson":"{\"toStage\":\"Won\"}"},{"name":"Alert","typeKey":"core.notify"}]""";
        var connectionsJson = """[{"from":"Big deal","to":"Alert"}]""";
        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_create_automation_workflow",
                    $$"""{"name":"Big deal alert","description":"Notify on big wins","nodes":{{nodesJson}},"connections":{{connectionsJson}}}""")
            ])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            automationNodeRegistry: registry, automationWorkflowService: workflowService);

        SentinelAiRunView? pending = null;
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Build a workflow for big deals", "grant"))
        {
            if (chunk.CompletedRun is not null) pending = chunk.CompletedRun;
        }

        var resolved = await service.ResolvePendingToolActionAsync(pending!.Id, approved: true, "grant");

        resolved.Status.Should().Be(SentinelAiRunStatuses.Completed);
        var created = (await workflowService.ListAsync()).Should().ContainSingle().Subject;
        created.Name.Should().Be("Big deal alert");
        created.Status.Should().Be(AutomationWorkflowStatuses.Draft, "an AI-authored graph must never activate itself");
        (await db.AutomationNodes.CountAsync(node => node.WorkflowId == created.Id)).Should().Be(2);
        (await db.AutomationConnections.CountAsync(connection => connection.WorkflowId == created.Id)).Should().Be(1);
    }

    [Fact]
    public async Task StreamToolCallingConversationAsync_ProposeCreateAutomationWorkflow_ShouldRejectAnUnknownNodeType()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppUsers.Add(new AppUser { Username = "grant", Role = AppRoles.Admin, IsActive = true });
        await db.SaveChangesAsync();

        var registry = new GwsBusinessSuite.Application.Automation.AutomationNodeRegistry(new FakeHttpClient());
        var workflowService = new GwsBusinessSuite.Application.Automation.AutomationWorkflowService(db, registry, TimeProvider.System);
        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_create_automation_workflow",
                    """{"name":"Bogus","nodes":[{"name":"Start","typeKey":"not.a.real.type"}]}""")
            ])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            automationNodeRegistry: registry, automationWorkflowService: workflowService);

        var chunks = new List<SentinelAiStreamChunk>();
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Build something", "grant"))
        {
            chunks.Add(chunk);
        }

        chunks[^1].CompletedRun!.Status.Should().Be(SentinelAiRunStatuses.Failed);
        chunks[^1].CompletedRun!.Output.Should().Contain("not.a.real.type");
        (await workflowService.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task StreamToolCallingConversationAsync_ProposeWrite_ShouldFailWithoutEditAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();
        // No AppUsers/Owner seeded for "grant" - SentinelAccessService.CanAccessAsync denies by default.

        var databases = new WikiDatabaseService(db);
        var database = await databases.CreateDatabaseAsync("Tasks", null, "admin-seed");
        var titleProperty = database.Properties.Single(p => p.Type == WikiDatabasePropertyTypes.Title);
        var statusProperty = await databases.SavePropertyAsync(database.Id,
            new WikiDatabasePropertyEditor { Name = "Status", Type = WikiDatabasePropertyTypes.Text }, "admin-seed");
        var titledRow = new System.Text.Json.Nodes.JsonObject();
        WikiPropertyValues.SetText(titledRow, titleProperty.Id, "Ship the release");
        var row = await databases.SaveRowAsync(database.Id,
            new WikiDatabaseRowEditor { Values = titledRow.ToDictionary(kv => kv.Key, kv => kv.Value) }, "admin-seed");

        var ollama = new ScriptedToolCallingOllamaService(
        [
            new(string.Empty,
            [
                new OllamaToolCall(
                    "propose_set_database_row_property",
                    $$"""{"wikiDatabaseId":"{{database.Id}}","rowId":"{{row.Id}}","propertyId":"{{statusProperty.Id}}","value":"Done"}""")
            ])
        ]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db),
            new SentinelWorkspaceService(db, TimeProvider.System), CreateCache(),
            accessService: new SentinelAccessService(db), wikiDatabaseService: databases);

        var chunks = new List<SentinelAiStreamChunk>();
        await foreach (var chunk in service.StreamToolCallingConversationAsync(Guid.NewGuid(), null, "Mark it done", "grant"))
        {
            chunks.Add(chunk);
        }

        chunks[^1].CompletedRun!.Status.Should().Be(SentinelAiRunStatuses.Failed);
        chunks[^1].CompletedRun!.Output.Should().Contain("access");
    }

    private sealed class ScriptedToolCallingOllamaService(IReadOnlyList<OllamaChatResponse> responses)
        : GwsBusinessSuite.Application.Abstractions.IOllamaService
    {
        private int _index;
        public List<IReadOnlyList<OllamaChatMessage>> Requests { get; } = [];

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyCollection<string>)Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task<OllamaChatResponse> ChatAsync(
            string model,
            IReadOnlyList<OllamaChatMessage> messages,
            IReadOnlyList<OllamaToolDefinition>? tools = null,
            CancellationToken ct = default)
        {
            Requests.Add(messages);
            var response = responses[Math.Min(_index, responses.Count - 1)];
            _index++;
            return Task.FromResult(response);
        }
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

    private sealed class FakeHttpClient : GwsBusinessSuite.Application.Automation.IAutomationHttpClient
    {
        public Task<GwsBusinessSuite.Application.Automation.AutomationHttpResponse> SendAsync(
            GwsBusinessSuite.Application.Automation.AutomationHttpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
