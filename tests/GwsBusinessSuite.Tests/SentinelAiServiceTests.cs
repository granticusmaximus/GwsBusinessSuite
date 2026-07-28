using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        var service = new SentinelAiService(factory, ollama, new SiteSettingsService(db), new SentinelWorkspaceService(db, TimeProvider.System));

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
    public async Task StreamAsync_ShouldRejectAnUnknownActionBeforeCallingOllama()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ollama = new FakeStreamingOllamaService(["should not be called"]);
        var service = new SentinelAiService(
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db), new SentinelWorkspaceService(db, TimeProvider.System));

        var act = async () =>
        {
            await foreach (var _ in service.StreamAsync(null, "not-a-real-action", "hello", "grant")) { }
        };

        await act.Should().ThrowAsync<ArgumentException>();
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
            new FakeAppDbContextFactory(options), ollama, new SiteSettingsService(db), new SentinelWorkspaceService(db, TimeProvider.System));

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

    private sealed class FakeAppDbContextFactory(DbContextOptions<ApplicationDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IAppDbContext>(new ApplicationDbContext(options));
    }

    private sealed class FakeStreamingOllamaService(IReadOnlyList<string> fragments) : GwsBusinessSuite.Application.Abstractions.IOllamaService
    {
        public bool WasCalled { get; private set; }

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(string.Join(string.Empty, fragments));

        public async IAsyncEnumerable<string> GenerateStreamAsync(
            string model, string systemPrompt, string userPrompt, [EnumeratorCancellation] CancellationToken ct = default)
        {
            WasCalled = true;
            foreach (var fragment in fragments)
            {
                await Task.Yield();
                yield return fragment;
            }
        }

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>());

        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);
    }
}
