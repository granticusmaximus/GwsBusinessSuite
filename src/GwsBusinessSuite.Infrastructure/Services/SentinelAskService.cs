using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// "Ask the workspace" (Sentinel item 2). Retrieval goes only through
// ISentinelWorkspaceService.SearchAsync - the sidebar search, which already filters to what the
// asker can view, mixes keyword and meaning (semantic) matches, and ranks verified pages up -
// so an answer can never be grounded in, or cite, a page the asker can't open.
public sealed class SentinelAskService(
    IAppDbContext dbContext,
    ISentinelWorkspaceService workspace,
    IOllamaService ollama,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    ILogger<SentinelAskService> logger) : ISentinelAskService
{
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromMinutes(2);

    public async Task<SentinelAskContext> PrepareAsync(string question, string username, CancellationToken cancellationToken = default)
    {
        var normalized = SentinelAsk.Normalize(question);
        if (normalized.Length < 3) throw new ArgumentException("Ask a question about the workspace.");
        var terms = SentinelAsk.KeyTerms(normalized);

        // Keyword search needs every word to match, so: the key words together, the whole
        // question (its meaning match), and - if that's thin - the most distinctive words alone.
        var found = new Dictionary<(Guid, bool), SentinelSearchResult>();
        void Merge(IEnumerable<SentinelSearchResult> results)
        {
            foreach (var result in results)
            {
                var key = (result.Id, result.IsDatabase);
                if (!found.TryGetValue(key, out var existing) || result.Score > existing.Score) found[key] = result;
            }
        }
        if (terms.Count > 0) Merge(await workspace.SearchAsync(string.Join(' ', terms.Take(6)), username, 8, cancellationToken));
        Merge(await workspace.SearchAsync(normalized, username, 8, cancellationToken));
        if (found.Count < 3)
        {
            foreach (var term in terms.Take(3)) Merge(await workspace.SearchAsync(term, username, 5, cancellationToken));
        }

        var ranked = found.Values.OrderByDescending(r => r.Score).Take(SentinelAsk.MaxSources).ToList();
        var pageIds = ranked.Where(r => !r.IsDatabase).Select(r => r.Id).ToList();
        var pages = await dbContext.WikiPages.AsNoTracking().Where(p => pageIds.Contains(p.Id) && p.TrashedAt == null).ToListAsync(cancellationToken);
        var nowUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var sources = new List<SentinelAskSource>();
        foreach (var result in ranked)
        {
            string excerpt;
            var verified = false;
            if (result.IsDatabase)
            {
                excerpt = result.Preview;
            }
            else
            {
                if (pages.FirstOrDefault(p => p.Id == result.Id) is not { } page) continue;
                excerpt = SentinelAsk.Excerpt(WikiBlockJson.ParseBlocks(page.BlocksJson), terms);
                verified = page.VerifiedUntilUnix is { } until && until > nowUnix;
            }
            if (string.IsNullOrWhiteSpace(excerpt)) continue;
            sources.Add(new SentinelAskSource(sources.Count + 1, result.Id, result.IsDatabase, result.SourceRowId,
                string.IsNullOrWhiteSpace(result.Title) ? "Untitled" : result.Title, excerpt, verified, result.Score));
        }
        return new SentinelAskContext(normalized, sources);
    }

    public async Task<SentinelAskAnswer> AnswerOnServerAsync(SentinelAskContext context, CancellationToken cancellationToken = default)
    {
        if (!context.HasSources) return SentinelAsk.NotFound();
        var model = await ServerModelAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ServerTimeout);
        var output = await ollama.GenerateAsync(model, SentinelAsk.SystemPrompt, SentinelAsk.UserPrompt(context), timeout.Token);
        return SentinelAsk.Clean(output, context);
    }

    public async Task<string> ServerModelAsync(CancellationToken cancellationToken = default)
    {
        var settings = await siteSettings.GetSettingsAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? SentinelGptDefaults.Model : settings.OllamaModelOverride;
    }

    public async Task RecordAsync(SentinelAskContext context, SentinelAskAnswer answer, string username, string answeredBy, string model, CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.SentinelQuestions.AddAsync(new SentinelQuestion
            {
                Username = username,
                Question = context.Question,
                Terms = Limit(string.Join(' ', SentinelAsk.KeyTerms(context.Question)), 300),
                NotFound = answer.NotFound,
                Uncited = answer.Uncited,
                SourceCount = context.Sources.Count,
                TopScore = context.Sources.Count == 0 ? 0 : context.Sources.Max(s => s.Score),
                SourcesJson = JsonSerializer.Serialize(context.Sources.Select(s => new
                {
                    id = s.TargetId, isDatabase = s.IsDatabase, title = s.Title, cited = answer.Cited.Contains(s.Number)
                })),
                AnsweredBy = answeredBy is "local" or "server" ? answeredBy : "server",
                Model = Limit(model, 120),
                AskedUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                CreatedBy = username
            }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The answer has been given; a failed log line mustn't turn it into an error.
            logger.LogWarning(ex, "Couldn't log a workspace question");
        }
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}
