using System.Runtime.CompilerServices;
using FluentAssertions;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Services;
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

        coordinator.GetActive("grant", null).Should().Match<SentinelGptGenerationSnapshot>(snapshot =>
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
        coordinator.GetActive("grant", null).Should().BeNull();
    }

    [Fact]
    public async Task Generation_WithUseToolsEnabled_ShouldRouteToTheToolCallingConversationInsteadOfTheAgentConversation()
    {
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        var conversationId = Guid.NewGuid();

        var started = await coordinator.StartAsync(
            conversationId, null, "What does our onboarding page say?", "grant",
            includeInternet: false, useDeepAnalysis: false, useTools: true);
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        sentinel.ToolCallingWasInvoked.Should().BeTrue();
        coordinator.GetActive("grant", null).Should().Match<SentinelGptGenerationSnapshot>(snapshot =>
            snapshot.Id == started.Id && snapshot.UseTools && snapshot.Activity == "🔧 search_wiki");

        sentinel.Release.TrySetResult();
        var completed = await WaitForTerminalAsync(coordinator, started.Id, "grant");

        completed.Status.Should().Be(SentinelGptGenerationStatuses.Completed);
        completed.Output.Should().Be("Recovered response.");
        completed.UseTools.Should().BeTrue();
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
        coordinator.GetActive("another-user", null).Should().BeNull();
        sentinel.Release.TrySetResult();
    }

    [Fact]
    public async Task GetActive_ShouldNotReturnAJobStartedForADifferentWikiPage()
    {
        // Regression guard: GetActive used to filter only by username, so two SentinelAiPanel
        // instances open on different wiki pages (same user) could "adopt" each other's active
        // generation - approving one page's panel could insert the other page's answer.
        var sentinel = new ControllableSentinelAiService();
        var coordinator = CreateCoordinator(sentinel);
        var pageA = Guid.NewGuid();
        var pageB = Guid.NewGuid();

        var started = await coordinator.StartActionAsync(pageA, SentinelAiActions.Summarize, "Summarize this page.", "grant");
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        coordinator.GetActive("grant", pageB).Should().BeNull();
        coordinator.GetActive("grant", pageA).Should().Match<SentinelGptGenerationSnapshot>(
            snapshot => snapshot.Id == started.Id);

        sentinel.Release.TrySetResult();
        await WaitForTerminalAsync(coordinator, started.Id, "grant");
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
        coordinator.GetActive("grant", null).Should().NotBeNull();

        coordinator.Cancel(started.Id, "grant").Should().BeTrue();
        await sentinel.CancellationObserved.Task.WaitAsync(TestTimeout);
        var cancelled = await WaitForTerminalAsync(coordinator, started.Id, "grant");

        cancelled.Status.Should().Be(SentinelGptGenerationStatuses.Cancelled);
        cancelled.Error.Should().BeNull();
        coordinator.GetActive("grant", null).Should().BeNull();
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

    [Fact]
    public async Task StartAsync_ShouldFireTheChatPromptAutomationTrigger_WithoutWaitingForIt()
    {
        var sentinel = new ControllableSentinelAiService();
        var trigger = new RecordingAutomationTriggerService();
        var coordinator = CreateCoordinator(sentinel, trigger);
        var conversationId = Guid.NewGuid();

        await coordinator.StartAsync(
            conversationId, null, "  What is our refund policy?  ", "grant", includeInternet: false, useDeepAnalysis: false);

        var (prompt, firedConversationId) = await trigger.Fired.Task.WaitAsync(TestTimeout);
        prompt.Should().Be("What is our refund policy?");
        firedConversationId.Should().Be(conversationId);

        sentinel.Release.TrySetResult();
    }

    [Fact]
    public async Task StartActionAsync_ShouldNotFireTheChatPromptAutomationTrigger()
    {
        // The one-shot Ask/Summarize/Rewrite action panel isn't "putting a prompt into
        // SentinelGPT" in the sense the Teacher Panel workflow cares about - only a real chat
        // conversation (StartAsync) does.
        var sentinel = new ControllableSentinelAiService();
        var trigger = new RecordingAutomationTriggerService();
        var coordinator = CreateCoordinator(sentinel, trigger);

        var started = await coordinator.StartActionAsync(Guid.NewGuid(), SentinelAiActions.Summarize, "Summarize this page.", "grant");
        await sentinel.Started.Task.WaitAsync(TestTimeout);
        sentinel.Release.TrySetResult();
        await WaitForTerminalAsync(coordinator, started.Id, "grant");

        trigger.Fired.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task StartActionAsync_WithAskAction_ShouldRouteToTheAgentConversationAndFireTheTrigger()
    {
        // The panel's "Ask" action is the one exception to "one-shot actions don't get chat
        // treatment" - it should get the same suite-context grounding and Teacher Panel/approved-
        // memory hooks as a real chat prompt, unlike Summarize/Rewrite/Translate/Research/
        // Meeting-notes.
        var sentinel = new ControllableSentinelAiService();
        var trigger = new RecordingAutomationTriggerService();
        var coordinator = CreateCoordinator(sentinel, trigger);
        var wikiPageId = Guid.NewGuid();

        var started = await coordinator.StartActionAsync(wikiPageId, SentinelAiActions.Ask, "What does this page cover?", "grant");
        await sentinel.Started.Task.WaitAsync(TestTimeout);

        sentinel.LastUseDeepAnalysis.Should().BeFalse();
        sentinel.LastAction.Should().BeNull("the Ask action should route through StreamAgentConversationAsync, not StreamAsync");
        var (prompt, firedConversationId) = await trigger.Fired.Task.WaitAsync(TestTimeout);
        prompt.Should().Be("What does this page cover?");
        // Action-based jobs (StartActionAsync) are always enqueued with conversationId: null -
        // that's what the fresh Guid.NewGuid() inside RunAsync's Ask branch scopes instead, for
        // the single StreamAgentConversationAsync call only, matching how Summarize/Rewrite/etc.
        // already scope their own one-shot StreamAsync calls.
        firedConversationId.Should().BeNull();

        sentinel.Release.TrySetResult();
        var completed = await WaitForTerminalAsync(coordinator, started.Id, "grant");
        completed.Status.Should().Be(SentinelGptGenerationStatuses.Completed);
        completed.CompletedRun!.WikiPageId.Should().Be(wikiPageId);
    }

    private static SentinelGptGenerationCoordinator CreateCoordinator(
        ControllableSentinelAiService sentinel, IAutomationTriggerService? triggerService = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISentinelAiService>(sentinel);
        if (triggerService is not null)
        {
            services.AddSingleton<IAutomationTriggerService>(triggerService);
        }
        var provider = services.BuildServiceProvider();
        return new SentinelGptGenerationCoordinator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TestHostApplicationLifetime(),
            new OllamaWorkloadScheduler(),
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

        public bool? ToolCallingWasInvoked { get; private set; }

        public async IAsyncEnumerable<SentinelAiStreamChunk> StreamToolCallingConversationAsync(
            Guid conversationId,
            Guid? wikiPageId,
            string instruction,
            string performedBy,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ToolCallingWasInvoked = true;
            Started.TrySetResult();
            yield return new SentinelAiStreamChunk(string.Empty, null, "🔧 search_wiki");
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
                    SentinelAiActions.Tools,
                    instruction,
                    "Recovered response.",
                    "completed",
                    SentinelGptDefaults.Model,
                    performedBy,
                    DateTimeOffset.UtcNow,
                    []));
        }

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

        public Task DeleteConversationAsync(
            Guid conversationId,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseAutofillResult> SuggestDatabaseRowValuesAsync(
            Guid wikiDatabaseId,
            Guid rowId,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SentinelAiRunView> ResolvePendingToolActionAsync(
            Guid runId,
            bool approved,
            string performedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAutomationTriggerService : IAutomationTriggerService
    {
        public TaskCompletionSource<(string Prompt, Guid? ConversationId)> Fired { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> TriggerSentinelChatPromptSubmittedAsync(
            string prompt, Guid? conversationId, CancellationToken cancellationToken = default)
        {
            Fired.TrySetResult((prompt, conversationId));
            return Task.FromResult(1);
        }

        public Task<AutomationExecutionView?> TriggerWebhookAsync(
            string path, string inputJson, string? providedSecret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> RunDueSchedulesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ResumeDueWaitsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutomationExecutionView?> ResumeViaWebhookAsync(
            string token, string bodyJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerDatabaseRowChangedAsync(
            Guid wikiDatabaseId, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerCrmDealStageChangedAsync(
            string stage, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerCmsPagePublishedAsync(
            Guid siteId, string inputJson, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerSupportTicketCreatedAsync(
            Guid ticketId, string subject, string contactName, string priority, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerSupportTicketRepliedAsync(
            Guid ticketId, string authorType, string authorName, string body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerSupportTicketSlaBreachedAsync(
            Guid ticketId, string subject, string contactName, string priority, string breachType,
            DateTimeOffset dueAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> TriggerCmsFormSubmittedAsync(
            Guid siteId, string inputJson, CancellationToken cancellationToken = default) =>
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
