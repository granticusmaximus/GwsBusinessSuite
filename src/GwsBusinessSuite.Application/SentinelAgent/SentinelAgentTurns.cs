using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.SentinelAgent;

// Turns started from the Mac app's own SentinelGPT tab (30-10-26, plan Phase B) and, since 05-11-26,
// from the side panel whenever it thinks on the person's Mac through the relay. The agent runs here
// and thinks on that Mac; the tab or panel follows a turn by polling its snapshot. So a long job
// carries on when the browser tab closes. If one finishes with nobody following it, the bell says
// so (FinishedUnwatched). Kept in memory for half an hour: the action log, not this, is the record
// of what happened.
public sealed class SentinelAgentTurns(SentinelAgentRunner runner, SentinelAgentRelay relay, TimeProvider time, ILogger<SentinelAgentTurns> logger)
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(30);
    // Someone who polled this recently is still watching; a panel polls every half second.
    private static readonly TimeSpan WatchedWithin = TimeSpan.FromSeconds(20);
    private readonly ConcurrentDictionary<Guid, Turn> _turns = new();
    private readonly ConcurrentDictionary<Guid, FinishedJob> _unwatched = new();

    // A job that finished while nobody was following it, for the bell.
    public sealed record FinishedJob(Guid TurnId, string Username, string Request, string Outcome, DateTimeOffset FinishedAt);

    // Raised with the username when one of their jobs finishes unwatched.
    public event Action<string>? JobFinishedUnwatched;

    // GoMessage travels with each snapshot so the app sends exactly what the runner expects after Go.
    public sealed record Snapshot(string Status, IReadOnlyList<string> Steps, IReadOnlyList<SentinelAgentActionView> Actions, string? Answer, string? Model, string? Error)
    {
        public string GoMessage => SentinelAgentRunner.GoMessage;
    }

    public static class Statuses
    {
        public const string Running = "running";
        public const string Done = "done";
        public const string Failed = "failed";
    }

    private sealed class Turn(string username, string message, DateTimeOffset startedAt)
    {
        private readonly object _gate = new();
        private readonly List<string> _steps = [];
        private readonly List<SentinelAgentActionView> _actions = [];
        private string _status = Statuses.Running;
        private string? _answer;
        private string? _model;
        private string? _error;

        public string Username { get; } = username;
        public string Message { get; } = message;
        public DateTimeOffset StartedAt { get; } = startedAt;
        // The starter is watching when it starts. Ticks so the poller and the job never tear a read.
        private long _lastWatchedTicks = startedAt.UtcTicks;

        public DateTimeOffset LastWatched
        {
            get => new(Interlocked.Read(ref _lastWatchedTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastWatchedTicks, value.UtcTicks);
        }
        public CancellationTokenSource Stop { get; } = new();

        public int ActionCount
        {
            get { lock (_gate) return _actions.Count; }
        }

        public void Add(SentinelAgentEvent agentEvent)
        {
            lock (_gate)
            {
                switch (agentEvent)
                {
                    case SentinelAgentStep step:
                        _steps.Add(step.Text);
                        break;
                    case SentinelAgentActed acted:
                        var index = _actions.FindIndex(action => action.Id == acted.Action.Id);
                        if (index >= 0) _actions[index] = acted.Action;
                        else _actions.Add(acted.Action);
                        break;
                    case SentinelAgentWithdrawn withdrawn:
                        _actions.RemoveAll(action => action.Id == withdrawn.ActionId);
                        break;
                }
            }
        }

        public void Finish(string answer, string model)
        {
            lock (_gate) (_status, _answer, _model) = (Statuses.Done, answer, model);
        }

        public void Fail(string error)
        {
            lock (_gate) (_status, _error) = (Statuses.Failed, error);
        }

        public Snapshot Snapshot()
        {
            lock (_gate) return new Snapshot(_status, _steps.ToList(), _actions.ToList(), _answer, _model, _error);
        }
    }

    // Null with a reason when this person's Mac isn't connected to the relay (the tab then
    // answers the way it did before).
    public (Guid? Id, string? Problem) Start(SentinelAgentUser user, Guid conversationId, string message, string? preferredModel, string? pageContext = null)
    {
        var models = relay.ModelsFor(user.Username).ToList();
        if (models.Count == 0) return (null, "This Mac isn't connected to SentinelGPT's relay yet.");
        if (preferredModel is not null && models.FindIndex(model => model.Equals(preferredModel, StringComparison.OrdinalIgnoreCase)) is var index and > 0)
        {
            models.Insert(0, models[index]);
            models.RemoveAt(index + 1);
        }

        var now = time.GetUtcNow();
        foreach (var (id, old) in _turns)
        {
            if (now - old.StartedAt > KeepFor) _turns.TryRemove(id, out _);
        }
        foreach (var (id, job) in _unwatched)
        {
            if (now - job.FinishedAt > KeepFor) _unwatched.TryRemove(id, out _);
        }

        var turnId = Guid.NewGuid();
        var turn = new Turn(user.Username, message, now);
        _turns[turnId] = turn;
        _ = Task.Run(async () =>
        {
            await RunAsync(turn, user, conversationId, message, pageContext, models);
            NoteIfUnwatched(turnId, turn);
        });
        return (turnId, null);
    }

    private void NoteIfUnwatched(Guid turnId, Turn turn)
    {
        var now = time.GetUtcNow();
        if (now - turn.LastWatched <= WatchedWithin) return;
        var snapshot = turn.Snapshot();
        _unwatched[turnId] = new FinishedJob(turnId, turn.Username, turn.Message, snapshot.Answer ?? snapshot.Error ?? "Finished.", now);
        try
        {
            JobFinishedUnwatched?.Invoke(turn.Username);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            logger.LogWarning(ex, "Couldn't tell the bell a SentinelGPT job finished.");
        }
    }

    // This person's jobs that finished while nobody was following them, newest first.
    public IReadOnlyList<FinishedJob> FinishedUnwatched(string username) =>
        _unwatched.Values.Where(job => job.Username.Equals(username, StringComparison.OrdinalIgnoreCase)
                && time.GetUtcNow() - job.FinishedAt <= KeepFor)
            .OrderByDescending(job => job.FinishedAt)
            .ToList();

    private async Task RunAsync(Turn turn, SentinelAgentUser user, Guid conversationId, string message, string? pageContext, IReadOnlyList<string> models)
    {
        foreach (var model in models)
        {
            try
            {
                var result = await runner.RunAsync(
                    new SentinelAgentRequest(conversationId, user, message, pageContext, new RelayedOllamaService(relay, user.Username), model),
                    agentEvent =>
                    {
                        turn.Add(agentEvent);
                        return Task.CompletedTask;
                    },
                    turn.Stop.Token);
                turn.Finish(result.Answer, model);
                return;
            }
            catch (InvalidOperationException ex) when (SentinelAgentModels.IsNoToolsError(ex) && turn.ActionCount == 0)
            {
                // That model can't use tools: the next one may.
            }
            catch (OperationCanceledException)
            {
                turn.Fail("Stopped.");
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                logger.LogWarning(ex, "A SentinelGPT turn (Mac app or panel) failed for {User}.", user.Username);
                turn.Fail(ex.Message);
                return;
            }
        }

        turn.Fail("None of this Mac's models can use tools.");
    }

    // Polling a turn is what "watching" means.
    public Snapshot? Get(Guid turnId, string username)
    {
        if (!_turns.TryGetValue(turnId, out var turn) || !turn.Username.Equals(username, StringComparison.OrdinalIgnoreCase)) return null;
        turn.LastWatched = time.GetUtcNow();
        return turn.Snapshot();
    }

    public bool Stop(Guid turnId, string username)
    {
        if (!_turns.TryGetValue(turnId, out var turn) || !turn.Username.Equals(username, StringComparison.OrdinalIgnoreCase)) return false;
        turn.Stop.Cancel();
        return true;
    }
}
