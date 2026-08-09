using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class SentinelGptGenerationCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Generation_ShouldContinueWithoutAWaitingBrowserCaller()
    {
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        var conversationId = Guid.NewGuid();

        var started = await coordinator.StartAsync(
            conversationId, null, "Summarize this long email.", "grant", includeInternet: false, useDeepAnalysis: true,
            maxOutputTokens: SentinelGptResponseBudgets.Concise);
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        coordinator.GetActive("grant").Should().Match<SentinelGptGenerationSnapshot>(snapshot =>
            snapshot.Id == started.Id
            && snapshot.ConversationId == conversationId
            && snapshot.UseDeepAnalysis
            && snapshot.MaxOutputTokens == SentinelGptResponseBudgets.Concise
            && snapshot.Status == SentinelGptGenerationStatuses.Running);
        sentinel.LastUseDeepAnalysis.Should().BeTrue();
        sentinel.LastMaxOutputTokens.Should().Be(SentinelGptResponseBudgets.Concise);

        // No component or request awaits the generation here. Releasing the fake model
        // simulates work continuing while the original browser circuit is disconnected.
        sentinel.Release.TrySetResult();
        var completed = await WaitForTerminalAsync(coordinator, started.Id, "grant");

        completed.Status.Should().Be(SentinelGptGenerationStatuses.Completed);
        completed.Output.Should().Be("Recovered response.");
        completed.CompletedRun!.ConversationId.Should().Be(conversationId);
        coordinator.GetActive("grant").Should().BeNull();
    }

    [Fact]
    public async Task Generation_ShouldAllowOnlyOneActiveResponsePerUser()
    {
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        await coordinator.StartAsync(
            Guid.NewGuid(), null, "First request", "grant", includeInternet: false, useDeepAnalysis: false);
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        var act = () => coordinator.StartAsync(
            Guid.NewGuid(), null, "Second request", "grant", includeInternet: false, useDeepAnalysis: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already generating*");
        sentinel.Release.TrySetResult();
    }

    [Fact]
    public async Task GenerationSnapshots_ShouldNotBeVisibleToAnotherUser()
    {
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        var started = await coordinator.StartAsync(
            Guid.NewGuid(), null, "Private request", "grant", includeInternet: false, useDeepAnalysis: false);

        coordinator.Get(started.Id, "another-user").Should().BeNull();
        coordinator.GetActive("another-user").Should().BeNull();
        sentinel.Release.TrySetResult();
    }

    [Fact]
    public async Task Cancel_ShouldStopOnlyTheRequestingUsersActiveGeneration()
    {
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        var started = await coordinator.StartAsync(
            Guid.NewGuid(), null, "Long running request", "grant", includeInternet: false, useDeepAnalysis: false);
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        coordinator.Cancel(started.Id, "another-user").Should().BeFalse();
        coordinator.GetActive("grant").Should().NotBeNull();

        coordinator.Cancel(started.Id, "grant").Should().BeTrue();
        await sentinel.CancellationObserved.Task.WaitAsync(TestTimeout);
        var cancelled = await WaitForTerminalAsync(coordinator, started.Id, "grant");

        cancelled.Status.Should().Be(SentinelGptGenerationStatuses.Cancelled);
        cancelled.Error.Should().BeNull();
        coordinator.GetActive("grant").Should().BeNull();
        coordinator.Cancel(started.Id, "grant").Should().BeFalse();
    }

    [Fact]
    public async Task StartActionAsync_ShouldRunOutsideAConversationAndSurviveWithoutAWaitingCaller()
    {
        // Regression guard for routing SentinelAiPanel through the coordinator: the panel's
        // quick-action flow (Ask/Summarize/...) has no conversation, unlike the chat page's
        // StartAsync, but must still run as a circuit-independent, poll-able job.
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        var wikiPageId = Guid.NewGuid();

        var started = await coordinator.StartActionAsync(wikiPageId, SentinelAiActions.Summarize, "Summarize this page.", "grant");
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        started.ConversationId.Should().BeNull();
        started.Action.Should().Be(SentinelAiActions.Summarize);
        sentinel.LastAction.Should().Be(SentinelAiActions.Summarize);

        sentinel.Release.TrySetResult();
        var completed = await WaitForTerminalAsync(coordinator, started.Id, "grant");

        completed.Status.Should().Be(SentinelGptGenerationStatuses.Completed);
        completed.Output.Should().Be("Recovered response.");
        completed.CompletedRun!.WikiPageId.Should().Be(wikiPageId);
    }

    [Fact]
    public async Task StartActionAsync_AndStartAsync_ShouldShareTheSameOneJobPerUserGate()
    {
        // The whole point of routing the panel through the shared coordinator is protecting
        // the single Ollama instance from two concurrent requests by the same user, whichever
        // surface (chat page or inline panel) they came from.
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        await coordinator.StartActionAsync(Guid.NewGuid(), SentinelAiActions.Ask, "Quick question", "grant");
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        var act = () => coordinator.StartAsync(
            Guid.NewGuid(), null, "Chat message", "grant", includeInternet: false, useDeepAnalysis: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already generating*");
        sentinel.Release.TrySetResult();
    }

    private static SentinelGptGenerationCoordinator CreateCoordinator(
        ControllableSentinelAiService sentinel)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISentinelAiService>(sentinel);
        var provider = services.BuildServiceProvider();
        return new SentinelGptGenerationCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestHostApplicationLifetime(),
            TimeProvider.System,
            NullLogger<SentinelGptGenerationCoordinator>.Instance);
    }

    private static async Task<SentinelGptGenerationSnapshot> WaitForTerminalAsync(
        SentinelGptGenerationCoordinator coordinator,
        Guid id,
        string requestedBy)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = coordinator.Get(id, requestedBy)
                ?? throw new InvalidOperationException("Generation disappeared before completion.");
            if (snapshot.IsTerminal) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ControllableSentinelAiService : ISentinelAiService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsInternetConfigured => false;
        public bool? LastUseDeepAnalysis { get; private set; }
        public int? LastMaxOutputTokens { get; private set; }

        public async IAsyncEnumerable<SentinelAiStreamChunk> StreamAgentConversationAsync(
            Guid conversationId,
            Guid? wikiPageId,
            string instruction,
            string performedBy,
            bool includeInternet,
            bool useDeepAnalysis,
            int maxOutputTokens = SentinelGptResponseBudgets.Standard,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastUseDeepAnalysis = useDeepAnalysis;
            LastMaxOutputTokens = maxOutputTokens;
            Started.TrySetResult();
            yield return new SentinelAiStreamChunk(string.Empty, null, "Thinking");
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            yield return new SentinelAiStreamChunk("Recovered ", null);
            yield return new SentinelAiStreamChunk("response.", null);
            yield return new SentinelAiStreamChunk(
                string.Empty,
                new SentinelAiRunView(
                    Guid.NewGuid(),
                    conversationId,
                    wikiPageId,
                    SentinelAiActions.Ask,
                    instruction,
                    "Recovered response.",
                    "completed",
                    SentinelGptDefaults.Model,
                    performedBy,
                    DateTimeOffset.UtcNow,
                    []));
        }

        public string? LastAction { get; private set; }

        public async IAsyncEnumerable<SentinelAiStreamChunk> StreamAsync(
            Guid? wikiPageId,
            string action,
            string instruction,
            string performedBy,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastAction = action;
            Started.TrySetResult();
            yield return new SentinelAiStreamChunk(string.Empty, null, "Thinking");
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            yield return new SentinelAiStreamChunk("Recovered ", null);
            yield return new SentinelAiStreamChunk("response.", null);
            yield return new SentinelAiStreamChunk(
                string.Empty,
                new SentinelAiRunView(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    wikiPageId,
                    action,
                    instruction,
                    "Recovered response.",
                    "completed",
                    SentinelGptDefaults.Model,
                    performedBy,
                    DateTimeOffset.UtcNow,
                    []));
        }

        public IAsyncEnumerable<SentinelAiStreamChunk> StreamConversationAsync(
            Guid conversationId,
            Guid? wikiPageId,
            string action,
            string instruction,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SentinelAiStreamChunk> StreamToolCallingConversationAsync(
            Guid conversationId,
            Guid? wikiPageId,
            string instruction,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SentinelGptCommandResult> ExecuteModelCommandAsync(
            Guid conversationId,
            string instruction,
            string performedBy,
            bool confirmed,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SentinelAiRunView>> ListRunsAsync(
            Guid? wikiPageId,
            int maxResults = 20,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SentinelGptConversationView>> ListConversationsAsync(
            string requestedBy,
            int maxResults = 40,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SentinelAiRunView>> ListConversationRunsAsync(
            Guid conversationId,
            string requestedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReviewAsync(
            Guid runId,
            bool approved,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseAutofillResult> SuggestDatabaseRowValuesAsync(
            Guid wikiDatabaseId,
            Guid rowId,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
