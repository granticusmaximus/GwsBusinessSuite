using System.Collections.Concurrent;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.WorldNewsKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// What a Pull latest found, shown on the World News page until the next one.
public sealed record PullSummary(DateTimeOffset At, string By, int NewReports, int NewStories, int UpdatedStories, int Merged,
    int Corroborated, int ThinChecked, int Searches, int PagesQueued, IReadOnlyList<PullQuery> Queries, string? Note,
    int CrawledAdded = 0, bool CrawledPending = false)
{
    public string Text
    {
        get
        {
            var parts = new List<string>
            {
                NewReports == 0 ? "no new reports" : $"{NewReports} new {Plural(NewReports, "report")}",
                $"{NewStories} new {Plural(NewStories, "story", "stories")}",
                $"{UpdatedStories} updated",
            };
            if (Merged > 0) parts.Add($"{Merged} {Plural(Merged, "story", "stories")} merged");
            if (ThinChecked > 0) parts.Add($"{Corroborated} of {ThinChecked} single-source {Plural(ThinChecked, "story", "stories")} now have a second source");
            if (PagesQueued > 0)
                parts.Add(CrawledPending ? $"{PagesQueued} web {Plural(PagesQueued, "page")} found, read within a few minutes"
                    : $"{CrawledAdded} more {Plural(CrawledAdded, "report")} from the web");
            var text = "Pulled: " + string.Join(", ", parts) + ".";
            return Note is null ? text : $"{text} {Note}";
        }
    }

    public string SearchedText => Queries.Count == 0 ? "No extra searches were needed."
        : "Searched for " + string.Join("; ", Queries.Select(q => $"“{q.Query}” ({q.Why})")) + ".";

    private static string Plural(int count, string one, string? many = null) => count == 1 ? one : many ?? one + "s";
}

// What a Regroup did: merges, and the AI's verdicts on close calls.
public sealed record RegroupSummary(DateTimeOffset At, string By, int Merged, int Asked, int JudgedSame, int JudgedApart, string? Note)
{
    public string Text
    {
        get
        {
            var text = Merged == 0 ? "Regrouped: no stories needed merging" : $"Regrouped: {Merged} {(Merged == 1 ? "story" : "stories")} merged";
            if (Asked > 0) text += $"; the AI checked {Asked} close {(Asked == 1 ? "call" : "calls")} ({JudgedSame} same, {JudgedApart} different)";
            text += ".";
            return Note is null ? text : $"{text} {Note}";
        }
    }
}

// Pull latest, Regroup and grouping corrections (18-11-26, .claude/plans/world-news-pull-regroup-2026-11-18.md).
public sealed partial class WorldNewsService
{
    // Pages the crawler queued for a pull are fetched at its own polite pace; one more read this
    // long after the pull imports what it has fetched by then.
    private static readonly TimeSpan CrawlFollowUp = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StoryPullCooldown = TimeSpan.FromMinutes(2);
    // Close calls: pairs this far below the merge threshold get the AI's opinion.
    private const double CloseCallBand = 0.05;

    private readonly SemaphoreSlim _pulling = new(1, 1);
    private readonly SemaphoreSlim _regrouping = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _storyPulls = new(StringComparer.Ordinal);
    private readonly int _regroupAiChecks;
    private DateTimeOffset _lastPullAt = DateTimeOffset.MinValue;

    // WorldNews:ManualPullCooldownMinutes: one pull for the whole server at most this often, as
    // every pull reads every source.
    public TimeSpan PullCooldown { get; }
    public bool IsPulling => _pulling.CurrentCount == 0;
    public bool IsRegrouping => _regrouping.CurrentCount == 0;
    // What a pull or regroup is doing now; null when neither is running.
    public string? PullStatus { get; private set; }
    public PullSummary? LastPull { get; private set; }
    public RegroupSummary? LastRegroup { get; private set; }

    public TimeSpan PullAvailableIn(DateTimeOffset now)
    {
        var wait = _lastPullAt + PullCooldown - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    // Starts a pull in the background; the page follows it through Changed. Null when it started,
    // else why not.
    public string? StartPull(string username)
    {
        var now = DateTimeOffset.UtcNow;
        if (PullAvailableIn(now) is { } wait && wait > TimeSpan.Zero)
            return $"The news was pulled a moment ago. Pull again in {Math.Ceiling(wait.TotalMinutes):0} min.";
        if (!_pulling.Wait(0)) return "A pull is already running.";
        _lastPullAt = now;
        _ = Task.Run(() => PullAsync(username, _stopping.Token));
        return null;
    }

    private async Task PullAsync(string username, CancellationToken ct)
    {
        var crawlQueued = 0;
        try
        {
            SetPullStatus("Choosing what to search for...");
            var now = DateTimeOffset.UtcNow;
            var thin = await Task.Run(() => Store.TopEvents(now.AddHours(-24), MaxEvents)
                .Where(e => e.OutletCount == 1).OrderByDescending(e => e.LastSeen).Take(3).ToList(), ct);
            var watches = string.IsNullOrWhiteSpace(username) ? []
                : await Task.Run(() => Store.KeywordWatches(username).Select(w => WorldNewsFinder.GdeltQuery(w.Query)).OfType<string>().ToList(), ct);
            var beats = string.IsNullOrWhiteSpace(username) ? []
                : await Task.Run(() => Store.Beats(username).Select(b => $"latest {b.Value} news").ToList(), ct);
            var plan = PullPlanner.Plan(_surgingTitles.Values.ToList(), thin.Select(e => e.Headline ?? e.Title), watches, ReadRequest.MaxQueries);

            var searches = 0;
            string? note = null;
            if (_webCrawler is not null)
            {
                SetPullStatus("Searching the web for fresh reports...");
                try
                {
                    (searches, crawlQueued) = await _webCrawler.DiscoverNowAsync(plan.Select(q => q.Query).Concat(beats).ToList(), ct: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "World News pull: the web search failed");
                    note = "The web search failed; the feeds were still read.";
                }
            }

            SetPullStatus(IsRefreshing ? "Waiting for the read in progress to finish..." : "Reading every source now...");
            var report = await ReadAsync(new ReadRequest(Force: true, plan.Select(q => q.Query).ToList()), wait: true, ct);
            if (report is null) note ??= "The read didn't finish: " + Status;

            SetPullStatus("Regrouping stories...");
            var regroup = await RegroupCoreAsync(string.IsNullOrWhiteSpace(username) ? "a pull" : username, ct, skipAiChecks: true);
            var corroborated = await Task.Run(() => thin.Count(e => Store.Event(e.Id) is { OutletCount: > 1 }), ct);
            LastPull = new PullSummary(DateTimeOffset.UtcNow, username, report?.Added ?? 0, report?.NewEvents ?? 0, report?.UpdatedEvents ?? 0,
                (report?.MergedEvents ?? 0) + regroup.Merged, corroborated, thin.Count, searches, crawlQueued, plan, note ?? regroup.Note,
                CrawledPending: crawlQueued > 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "World News pull failed");
            LastPull = new PullSummary(DateTimeOffset.UtcNow, username, 0, 0, 0, 0, 0, 0, 0, 0, [], "The pull failed: " + ex.Message);
        }
        finally
        {
            PullStatus = null;
            _pulling.Release();
            Changed?.Invoke();
        }
        if (crawlQueued > 0) await ImportCrawledAsync(ct);
    }

    // The follow-up read after a pull: brings in the pages the crawler has fetched since.
    private async Task ImportCrawledAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(CrawlFollowUp, ct);
            var report = await ReadAsync(null, wait: true, ct);
            if (LastPull is { } pull) LastPull = pull with { CrawledAdded = report?.Added ?? 0, CrawledPending = false };
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    // One story's own search, now: GDELT and the web for its headline's words. A message for the page.
    public async Task<string> PullStoryAsync(string eventId, string username, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_storyPulls.TryGetValue(eventId, out var last) && now - last < StoryPullCooldown)
            return "This story was searched a moment ago. Try again in a couple of minutes.";
        var story = await Task.Run(() => Store.Event(eventId), ct);
        if (story is null) return "That story is no longer kept.";
        if (FollowUpQueries.From(story.Headline ?? story.Title) is not { } query) return "The headline has too few distinctive words to search for.";
        _storyPulls[eventId] = now;
        foreach (var old in _storyPulls.Where(pair => now - pair.Value > StoryPullCooldown).Select(pair => pair.Key).ToList())
            _storyPulls.TryRemove(old, out _);

        var queued = 0;
        if (_webCrawler is not null)
        {
            try { (_, queued) = await _webCrawler.DiscoverNowAsync([story.Headline ?? story.Title], includeConfigured: false, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "World News story pull: the web search failed");
            }
        }
        var before = story.ArticleCount;
        await ReadAsync(new ReadRequest(Queries: [query]), wait: true, ct);
        var after = await Task.Run(() => Store.Event(eventId), ct);
        _logger.LogInformation("World News story pull for {EventId} by {User}: {Before} -> {After} reports", eventId, username, before, after?.ArticleCount);
        if (after is null) return "The story was merged into another while it was searched. Open World News to find it.";
        var added = after.ArticleCount - before;
        var crawl = queued > 0 ? $" {queued} more pages were found on the web and are read within a few minutes." : "";
        return (added > 0 ? $"{added} new {(added == 1 ? "report" : "reports")} joined this story." : "No new reports joined this story yet.") + crawl;
    }

    // Starts a regroup in the background. Null when it started, else why not.
    public string? StartRegroup(string username)
    {
        if (IsPulling) return "A pull is running; it regroups when it finishes.";
        if (!_regrouping.Wait(0)) return "A regroup is already running.";
        _ = Task.Run(async () =>
        {
            try
            {
                SetPullStatus("Regrouping stories...");
                LastRegroup = await RegroupCoreAsync(username, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "World News regroup failed");
                LastRegroup = new RegroupSummary(DateTimeOffset.UtcNow, username, 0, 0, 0, 0, "The regroup failed: " + ex.Message);
            }
            finally
            {
                PullStatus = null;
                _regrouping.Release();
                Changed?.Invoke();
            }
        });
        return null;
    }

    // Asks the AI about the closest pairs below the merge threshold (a few a run, at background
    // priority, standing aside for anyone chatting), then compares every active story with every
    // other, merging the AI's confirmed pairs and never the ones kept apart.
    private async Task<RegroupSummary> RegroupCoreAsync(string actor, CancellationToken ct, bool skipAiChecks = false)
    {
        var calls = skipAiChecks || _regroupAiChecks == 0 ? [] : await Task.Run(() => _pipeline.CloseCalls(CloseCallBand, _regroupAiChecks), ct);
        var confirmed = new HashSet<(string, string)>();
        var asked = 0;
        var apart = 0;
        string? note = null;
        foreach (var call in calls)
        {
            SetPullStatus($"Checking close calls with the AI ({asked + 1} of {calls.Count})...");
            var verdict = await JudgeAsync(call, ct);
            if (verdict.Note is not null) { note = verdict.Note; break; }
            asked++;
            if (verdict.Same is not { } same) continue;
            var reason = string.IsNullOrWhiteSpace(verdict.Reason) ? (same ? "the same event" : "different events") : verdict.Reason;
            var by = $"AI ({_model})";
            await Task.Run(() =>
            {
                Store.SavePairVerdict(call.A.Id, call.B.Id, same ? GroupingVerdicts.Same : GroupingVerdicts.Apart, GroupingVerdicts.Ai, reason, by, DateTimeOffset.UtcNow);
                var detail = $"{(same ? "Same story as" : "Not the same story as")} “{{0}}” (similarity {call.Similarity:0.00}): {reason}";
                Store.LogGrouping(call.A.Id, call.B.Id, "judged", string.Format(detail, call.B.Title), by, DateTimeOffset.UtcNow);
                Store.LogGrouping(call.B.Id, call.A.Id, "judged", string.Format(detail, call.A.Title), by, DateTimeOffset.UtcNow);
            }, ct);
            if (same) confirmed.Add(GroupingPairs.Key(call.A.Id, call.B.Id));
            else apart++;
        }

        SetPullStatus("Regrouping stories...");
        await _running.WaitAsync(ct);
        RegroupResult result;
        try
        {
            result = await Task.Run(() => _pipeline.Regroup(confirmed, actor), ct);
        }
        finally
        {
            _running.Release();
        }
        await ClassifySituationsAsync(ct);
        ClearLists();
        _mapCache?.Clear();
        Changed?.Invoke();
        return new RegroupSummary(DateTimeOffset.UtcNow, actor, result.Merges.Count, asked, confirmed.Count, apart, note);
    }

    // Merge suggestions, the reverse of the hourly split (20-11-26): once an hour the closest few
    // pairs just below the merge threshold go to the AI, and the ones it calls the same event are
    // merged, as Regroup would, without comparing every story with every other on a keypress.
    public const string HourlyMergeActor = "automatic (hourly merge check)";
    public static readonly TimeSpan MergeCheckEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _lastCloseCallCheck = DateTimeOffset.MinValue;

    public void StartHourlyMergeCheck(DateTimeOffset now)
    {
        if (_regroupAiChecks == 0 || now - _lastCloseCallCheck < MergeCheckEvery) return;
        if (!_regrouping.Wait(0)) return;
        _lastCloseCallCheck = now;
        _ = Task.Run(async () =>
        {
            try
            {
                var merged = await MergeCloseCallsAsync(Math.Min(3, _regroupAiChecks), _stopping.Token);
                if (merged > 0) _logger.LogInformation("World News: the hourly merge check joined {Count} stories split in two", merged);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "World News: hourly merge check failed");
            }
            finally
            {
                _regrouping.Release();
            }
        });
    }

    private async Task<int> MergeCloseCallsAsync(int checks, CancellationToken ct)
    {
        var calls = await Task.Run(() => _pipeline.CloseCalls(CloseCallBand, checks), ct);
        var merged = 0;
        foreach (var call in calls)
        {
            var verdict = await JudgeAsync(call, ct);
            if (verdict.Note is not null) break;
            if (verdict.Same is not { } same) continue;
            var reason = string.IsNullOrWhiteSpace(verdict.Reason) ? (same ? "the same event" : "different events") : verdict.Reason;
            var by = $"AI ({_model})";
            await Task.Run(() =>
            {
                Store.SavePairVerdict(call.A.Id, call.B.Id, same ? GroupingVerdicts.Same : GroupingVerdicts.Apart, GroupingVerdicts.Ai, reason, by, DateTimeOffset.UtcNow);
                var detail = $"{(same ? "Same story as" : "Not the same story as")} “{{0}}” (similarity {call.Similarity:0.00}, hourly merge check): {reason}";
                Store.LogGrouping(call.A.Id, call.B.Id, "judged", string.Format(detail, call.B.Title), by, DateTimeOffset.UtcNow);
                Store.LogGrouping(call.B.Id, call.A.Id, "judged", string.Format(detail, call.A.Title), by, DateTimeOffset.UtcNow);
            }, ct);
            if (same && await MergeAsync(call.A.Id, call.B.Id, HourlyMergeActor, ct) is not null) merged++;
        }
        return merged;
    }

    private async Task<(bool? Same, string Reason, string? Note)> JudgeAsync(CloseCall call, CancellationToken ct)
    {
        var headlinesA = await Task.Run(() => Headlines(call.A.Id), ct);
        var headlinesB = await Task.Run(() => Headlines(call.B.Id), ct);
        try
        {
            using var standAside = _workloads is null ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : CancellationTokenSource.CreateLinkedTokenSource(ct, _workloads.InteractiveArrivedToken);
            using var background = _workloads?.UseBackgroundPriority();
            // Public headlines only, so an Admin's Mac may answer when the server has no model.
            using var shared = MacRelayScope.ForSharedWork();
            await using var scope = _scopes.CreateAsyncScope();
            var reply = await scope.ServiceProvider.GetRequiredService<IOllamaService>().GenerateAsync(
                _model, GroupingAdjudicator.SystemPrompt, GroupingAdjudicator.Prompt(headlinesA, headlinesB), 4096, standAside.Token);
            var (same, reason) = GroupingAdjudicator.Parse(reply);
            return (same, reason, null);
        }
        catch (ServerAiUnavailableException)
        {
            return (null, "", "The AI wasn't available, so stories were regrouped by similarity alone.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "", "The AI check stood aside for someone using the AI; the rest wait for the next regroup.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "World News regroup: the AI check failed");
            return (null, "", "The AI check failed, so stories were regrouped by similarity alone.");
        }
    }

    private List<string> Headlines(string eventId) =>
        Store.ArticlesForEvent(eventId).OrderByDescending(a => a.SeenAt).Select(a => a.English?.Headline ?? a.Title).Take(6).ToList();

    // ---- A person's corrections. Each takes the read's turn, so a read never sees half of one. ----

    // "Not the same story": the reports become a story of their own, kept apart from this one.
    // Returns the new story's id, or null when it couldn't be done.
    public async Task<string?> SplitAsync(string eventId, IReadOnlyCollection<string> articleIds, string username, CancellationToken ct = default)
    {
        var vectors = await HeadlineVectorsAsync(eventId, articleIds, ct);
        return await CorrectAsync(() => Store.SplitReports(eventId, articleIds, vectors, username, DateTimeOffset.UtcNow), ct);
    }

    public async Task<bool> MoveAsync(string fromId, string toId, IReadOnlyCollection<string> articleIds, string username, CancellationToken ct = default)
    {
        var vectors = await HeadlineVectorsAsync(fromId, articleIds, ct);
        return await CorrectAsync(() => Store.MoveReports(fromId, toId, articleIds, vectors, username, DateTimeOffset.UtcNow), ct);
    }

    // Returns the id the merged story keeps, or null when either is gone.
    public Task<string?> MergeAsync(string eventA, string eventB, string username, CancellationToken ct = default) =>
        CorrectAsync(() => Store.MergeStories(eventA, eventB, username, DateTimeOffset.UtcNow), ct);

    public IReadOnlyList<GroupingLogEntry> GroupingHistory(string eventId) => Store.GroupingHistory(eventId);

    public IReadOnlyList<SplitSuggestion> SuggestedSplits(string eventId) => SplitSuggestions.For(Store.ArticlesForEvent(eventId));

    private async Task<T> CorrectAsync<T>(Func<T> correction, CancellationToken ct)
    {
        await _running.WaitAsync(ct);
        T result;
        try
        {
            result = await Task.Run(correction, ct);
        }
        finally
        {
            _running.Release();
        }
        ClearLists();
        _mapCache?.Clear();
        Changed?.Invoke();
        return result;
    }

    // The reports' headlines embedded again (a grouped report's own vector isn't kept), so a split
    // or move carries their meaning. Null without the model: the store then shares the story's.
    private async Task<IReadOnlyDictionary<string, float[]>?> HeadlineVectorsAsync(string eventId, IReadOnlyCollection<string> articleIds, CancellationToken ct)
    {
        if (_serverHasNoModel || articleIds.Count == 0) return null;
        var wanted = articleIds.ToHashSet(StringComparer.Ordinal);
        var articles = await Task.Run(() => Store.ArticlesForEvent(eventId).Where(a => wanted.Contains(a.Id)).ToList(), ct);
        if (articles.Count == 0) return null;
        try
        {
            var vectors = await _ollama.EmbedAsync(_options.EmbeddingModel, articles.Select(a => a.Title).ToList(), ct);
            return articles.Zip(vectors).ToDictionary(pair => pair.First.Id, pair => pair.Second);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "World News correction: headlines couldn't be embedded; the story's vector is shared instead");
            return null;
        }
    }

    private void SetPullStatus(string status)
    {
        PullStatus = status;
        Changed?.Invoke();
    }
}
