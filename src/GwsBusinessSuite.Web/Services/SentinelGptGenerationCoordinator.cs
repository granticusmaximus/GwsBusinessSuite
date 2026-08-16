using System.Collections.Concurrent;
using System.Text;
using GwsBusinessSuite.Application.Automation;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Infrastructure.Services;

namespace GwsBusinessSuite.Web.Services;

public static class SentinelGptGenerationStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public sealed record SentinelGptGenerationSnapshot(
    Guid Id,
    Guid? ConversationId,
    Guid? WikiPageId,
    string? Action,
    string Instruction,
    string RequestedBy,
    bool IncludeInternet,
    bool UseDeepAnalysis,
    bool UseTools,
    int MaxOutputTokens,
    string Status,
    string Output,
    string Activity,
    string? Error,
    SentinelAiRunView? CompletedRun,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminal =>
        Status is SentinelGptGenerationStatuses.Completed
            or SentinelGptGenerationStatuses.Cancelled
            or SentinelGptGenerationStatuses.Failed;
}

/// <summary>
/// Owns SentinelGPT work independently of a Blazor circuit. The component only polls
/// snapshots, so closing a tab or temporarily losing SignalR no longer disposes the
/// scoped AI service that is producing the answer.
/// </summary>
public sealed class SentinelGptGenerationCoordinator(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime applicationLifetime,
    OllamaWorkloadScheduler ollamaWorkloads,
    TimeProvider timeProvider,
    ILogger<SentinelGptGenerationCoordinator> logger)
{
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<Guid, GenerationState> _jobs = [];
    private readonly object _startGate = new();

    public Task<SentinelGptGenerationSnapshot> StartAsync(
        Guid conversationId,
        Guid? wikiPageId,
        string instruction,
        string requestedBy,
        bool includeInternet,
        bool useDeepAnalysis,
        int maxOutputTokens = SentinelGptResponseBudgets.Standard,
        bool useTools = false)
    {
        if (conversationId == Guid.Empty)
            throw new ArgumentException("A conversation is required.", nameof(conversationId));
        if (string.IsNullOrWhiteSpace(instruction))
            throw new ArgumentException("An instruction is required.", nameof(instruction));
        if (string.IsNullOrWhiteSpace(requestedBy))
            throw new ArgumentException("A requesting user is required.", nameof(requestedBy));
        if (!SentinelGptResponseBudgets.IsSupported(maxOutputTokens))
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens), "Choose a supported response length.");

        var state = Enqueue(
            conversationId,
            wikiPageId,
            action: null,
            instruction,
            requestedBy,
            includeInternet,
            useDeepAnalysis,
            useTools,
            maxOutputTokens);
        return Task.FromResult(state.Snapshot());
    }

    /// <summary>
    /// Same shared, circuit-independent generation slot as <see cref="StartAsync"/>, for the
    /// single-shot action panel (Ask/Summarize/Rewrite/...) instead of a full agent
    /// conversation. Sharing one coordinator - and its one-active-job-per-user gate - across
    /// both entry points is what actually protects the single Ollama instance from two
    /// concurrent requests from the same user, whichever surface they came from.
    /// </summary>
    public Task<SentinelGptGenerationSnapshot> StartActionAsync(
        Guid? wikiPageId,
        string action,
        string instruction,
        string requestedBy)
    {
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("An action is required.", nameof(action));
        if (string.IsNullOrWhiteSpace(instruction))
            throw new ArgumentException("An instruction is required.", nameof(instruction));
        if (string.IsNullOrWhiteSpace(requestedBy))
            throw new ArgumentException("A requesting user is required.", nameof(requestedBy));

        var state = Enqueue(
            conversationId: null,
            wikiPageId,
            action.Trim(),
            instruction,
            requestedBy,
            includeInternet: false,
            useDeepAnalysis: false,
            useTools: false,
            SentinelGptResponseBudgets.Standard);
        return Task.FromResult(state.Snapshot());
    }

    private GenerationState Enqueue(
        Guid? conversationId,
        Guid? wikiPageId,
        string? action,
        string instruction,
        string requestedBy,
        bool includeInternet,
        bool useDeepAnalysis,
        bool useTools,
        int maxOutputTokens)
    {
        lock (_startGate)
        {
            PruneCompleted();
            var active = _jobs.Values
                .FirstOrDefault(item =>
                    string.Equals(item.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase)
                    && !item.IsTerminal);
            if (active is not null)
            {
                throw new InvalidOperationException(
                    "SentinelGPT is already generating a response for this account. " +
                    "Reopen the active conversation to follow its progress.");
            }

            var now = timeProvider.GetUtcNow();
            var state = new GenerationState(
                Guid.NewGuid(),
                conversationId,
                wikiPageId,
                action,
                instruction.Trim(),
                requestedBy,
                includeInternet,
                useDeepAnalysis,
                useTools,
                maxOutputTokens,
                now);
            _jobs[state.Id] = state;

            _ = Task.Run(
                () => RunAsync(state, applicationLifetime.ApplicationStopping),
                CancellationToken.None);

            // Only a real chat prompt (StartAsync, action is null) fires the Teacher Panel
            // trigger - not the one-shot Ask/Summarize/Rewrite action panel (StartActionAsync),
            // which isn't "putting a prompt into SentinelGPT" in the sense that workflow cares
            // about. A completely separate detached task from the generation itself, so a slow
            // or failing trigger can never delay the chat response the user is waiting on.
            if (action is null)
            {
                _ = Task.Run(
                    () => FireChatPromptTriggerAsync(state.Instruction, conversationId, applicationLifetime.ApplicationStopping),
                    CancellationToken.None);
            }

            return state;
        }
    }

    private async Task FireChatPromptTriggerAsync(string prompt, Guid? conversationId, CancellationToken stoppingToken)
    {
        try
        {
            // Every Ollama call any subscriber workflow makes (via ai.modelAdvisor,
            // ai.allInstalledModelsAdvisor, ai.sentinelSynthesize, ...) flows through this same
            // AsyncLocal-scoped priority for the rest of this call tree, so it always yields the
            // single OllamaWorkloadScheduler lease to the user's own interactive chat request.
            using var workloadPriority = ollamaWorkloads.UseBackgroundPriority();
            await using var scope = scopeFactory.CreateAsyncScope();
            var triggerService = scope.ServiceProvider.GetRequiredService<IAutomationTriggerService>();
            await triggerService.TriggerSentinelChatPromptSubmittedAsync(prompt, conversationId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Server shutting down - nothing left to report this to.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SentinelGPT chat-prompt automation trigger failed.");
        }
    }

    public SentinelGptGenerationSnapshot? Get(Guid id, string requestedBy)
    {
        PruneCompleted();
        return _jobs.TryGetValue(id, out var state)
            && string.Equals(state.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase)
                ? state.Snapshot()
                : null;
    }

    public SentinelGptGenerationSnapshot? GetActive(string requestedBy)
    {
        PruneCompleted();
        return _jobs.Values
            .Where(item =>
                string.Equals(item.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase)
                && !item.IsTerminal)
            .OrderByDescending(item => item.StartedAt)
            .Select(item => item.Snapshot())
            .FirstOrDefault();
    }

    public bool Cancel(Guid id, string requestedBy)
    {
        if (!_jobs.TryGetValue(id, out var state)
            || !string.Equals(state.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return state.TryCancel(timeProvider.GetUtcNow());
    }

    private async Task RunAsync(GenerationState state, CancellationToken stoppingToken)
    {
        state.MarkRunning(timeProvider.GetUtcNow());
        using var generationToken = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            state.CancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sentinelGpt = scope.ServiceProvider.GetRequiredService<ISentinelAiService>();
            var stream = state.ConversationId is not { } conversationId
                ? sentinelGpt.StreamAsync(
                    state.WikiPageId,
                    state.Action!,
                    state.Instruction,
                    state.RequestedBy,
                    generationToken.Token)
                : state.UseTools
                    ? sentinelGpt.StreamToolCallingConversationAsync(
                        conversationId,
                        state.WikiPageId,
                        state.Instruction,
                        state.RequestedBy,
                        generationToken.Token)
                    : sentinelGpt.StreamAgentConversationAsync(
                        conversationId,
                        state.WikiPageId,
                        state.Instruction,
                        state.RequestedBy,
                        state.IncludeInternet,
                        state.UseDeepAnalysis,
                        state.MaxOutputTokens,
                        generationToken.Token);
            await foreach (var chunk in stream)
            {
                state.Apply(chunk, timeProvider.GetUtcNow());
            }

            state.MarkCompleted(timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
            when (state.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            state.MarkCancelled(timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            state.MarkFailed(
                "The server stopped before SentinelGPT finished. Send the request again after deployment completes.",
                timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "SentinelGPT background generation {GenerationId} failed for {RequestedBy}.",
                state.Id,
                state.RequestedBy);
            state.MarkFailed(ex.Message, timeProvider.GetUtcNow());
        }
    }

    private void PruneCompleted()
    {
        var cutoff = timeProvider.GetUtcNow() - CompletedRetention;
        foreach (var pair in _jobs)
        {
            if (pair.Value.IsTerminal && pair.Value.UpdatedAt < cutoff)
            {
                if (_jobs.TryRemove(pair.Key, out var removed))
                {
                    removed.Dispose();
                }
            }
        }
    }

    private sealed class GenerationState
    {
        private readonly object _gate = new();
        private readonly StringBuilder _output = new();
        private readonly CancellationTokenSource _cancellation = new();
        private string _status = SentinelGptGenerationStatuses.Queued;
        private string _activity = "Preparing SentinelGPT";
        private string? _error;
        private SentinelAiRunView? _completedRun;
        private DateTimeOffset _updatedAt;

        public GenerationState(
            Guid id,
            Guid? conversationId,
            Guid? wikiPageId,
            string? action,
            string instruction,
            string requestedBy,
            bool includeInternet,
            bool useDeepAnalysis,
            bool useTools,
            int maxOutputTokens,
            DateTimeOffset startedAt)
        {
            Id = id;
            ConversationId = conversationId;
            WikiPageId = wikiPageId;
            Action = action;
            Instruction = instruction;
            RequestedBy = requestedBy;
            IncludeInternet = includeInternet;
            UseDeepAnalysis = useDeepAnalysis;
            UseTools = useTools;
            MaxOutputTokens = maxOutputTokens;
            StartedAt = startedAt;
            _updatedAt = startedAt;
        }

        public Guid Id { get; }
        public Guid? ConversationId { get; }
        public Guid? WikiPageId { get; }
        public string? Action { get; }
        public string Instruction { get; }
        public string RequestedBy { get; }
        public bool IncludeInternet { get; }
        public bool UseDeepAnalysis { get; }
        public bool UseTools { get; }
        public int MaxOutputTokens { get; }
        public DateTimeOffset StartedAt { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    return _status is SentinelGptGenerationStatuses.Completed
                        or SentinelGptGenerationStatuses.Cancelled
                        or SentinelGptGenerationStatuses.Failed;
                }
            }
        }

        public DateTimeOffset UpdatedAt
        {
            get
            {
                lock (_gate)
                {
                    return _updatedAt;
                }
            }
        }

        public void MarkRunning(DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                if (_status == SentinelGptGenerationStatuses.Cancelled)
                {
                    return;
                }

                _status = SentinelGptGenerationStatuses.Running;
                _updatedAt = updatedAt;
            }
        }

        public void Apply(SentinelAiStreamChunk chunk, DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                if (_status == SentinelGptGenerationStatuses.Cancelled)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(chunk.Activity))
                {
                    _activity = chunk.Activity;
                }
                else if (chunk.CompletedRun is not null)
                {
                    _completedRun = chunk.CompletedRun;
                }
                else
                {
                    _output.Append(chunk.Delta);
                    _activity = string.Empty;
                }

                _updatedAt = updatedAt;
            }
        }

        public void MarkCompleted(DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                if (_status == SentinelGptGenerationStatuses.Cancelled)
                {
                    return;
                }

                if (_completedRun is null)
                {
                    _status = SentinelGptGenerationStatuses.Failed;
                    _error = "SentinelGPT finished without a persisted response.";
                }
                else
                {
                    _status = SentinelGptGenerationStatuses.Completed;
                }

                _updatedAt = updatedAt;
            }
        }

        public void MarkFailed(string error, DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                if (_status == SentinelGptGenerationStatuses.Cancelled)
                {
                    return;
                }

                _status = SentinelGptGenerationStatuses.Failed;
                _error = error;
                _updatedAt = updatedAt;
            }
        }

        public bool TryCancel(DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                if (_status is SentinelGptGenerationStatuses.Completed
                    or SentinelGptGenerationStatuses.Cancelled
                    or SentinelGptGenerationStatuses.Failed)
                {
                    return false;
                }

                _status = SentinelGptGenerationStatuses.Cancelled;
                _activity = "Response stopped";
                _error = null;
                _updatedAt = updatedAt;
            }

            _cancellation.Cancel();
            return true;
        }

        public void MarkCancelled(DateTimeOffset updatedAt)
        {
            lock (_gate)
            {
                _status = SentinelGptGenerationStatuses.Cancelled;
                _activity = "Response stopped";
                _error = null;
                _updatedAt = updatedAt;
            }
        }

        public SentinelGptGenerationSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new SentinelGptGenerationSnapshot(
                    Id,
                    ConversationId,
                    WikiPageId,
                    Action,
                    Instruction,
                    RequestedBy,
                    IncludeInternet,
                    UseDeepAnalysis,
                    UseTools,
                    MaxOutputTokens,
                    _status,
                    _output.ToString(),
                    _activity,
                    _error,
                    _completedRun,
                    StartedAt,
                    _updatedAt);
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
