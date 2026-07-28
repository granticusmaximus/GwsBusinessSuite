using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Wiki;

public sealed class SentinelAiService(
    IAppDbContextFactory dbContextFactory,
    IOllamaService ollama,
    ISiteSettingsService siteSettings,
    ISentinelWorkspaceService workspaceService) : ISentinelAiService
{
    private static readonly HashSet<string> AllowedActions =
    [
        SentinelAiActions.Ask, SentinelAiActions.Summarize, SentinelAiActions.Rewrite,
        SentinelAiActions.Translate, SentinelAiActions.Research, SentinelAiActions.MeetingNotes,
        SentinelAiActions.DatabaseAutofill
    ];

    public async IAsyncEnumerable<SentinelAiStreamChunk> StreamAsync(
        Guid? wikiPageId,
        string action,
        string instruction,
        string performedBy,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in StreamConversationAsync(
            Guid.NewGuid(), wikiPageId, action, instruction, performedBy, cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<SentinelAiStreamChunk> StreamConversationAsync(
        Guid conversationId,
        Guid? wikiPageId,
        string action,
        string instruction,
        string performedBy,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty) throw new ArgumentException("A conversation is required.", nameof(conversationId));
        if (!AllowedActions.Contains(action)) throw new ArgumentException("Unknown SentinelGPT action.", nameof(action));
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("An instruction or source text is required.", nameof(instruction));

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await siteSettings.GetSettingsAsync(cancellationToken);
        var model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? ContentStudioOptions.DefaultModel : settings.OllamaModelOverride;
        var (context, citations) = await BuildGroundedContextAsync(db, wikiPageId, instruction, cancellationToken);

        var run = new SentinelAiRun
        {
            ConversationId = conversationId,
            WikiPageId = wikiPageId,
            Action = action,
            Instruction = instruction.Trim(),
            Model = model,
            Status = SentinelAiRunStatuses.Completed,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = performedBy
        };

        var output = new StringBuilder();
        var systemPrompt = SystemPrompt(action);
        var priorRuns = await db.SentinelAiRuns.AsNoTracking()
            .Where(item => item.ConversationId == conversationId && item.CreatedBy == performedBy)
            .ToListAsync(cancellationToken);
        var history = string.Join("\n\n", priorRuns
            .OrderBy(item => item.CreatedAt)
            .TakeLast(8)
            .Select(item => $"USER: {item.Instruction}\nSENTINELGPT: {item.Output}"));
        var userPrompt = string.IsNullOrWhiteSpace(history)
            ? $"WORKSPACE CONTEXT:\n{context}\n\nREQUEST:\n{instruction.Trim()}"
            : $"CONVERSATION SO FAR:\n{history}\n\nWORKSPACE CONTEXT:\n{context}\n\nNEW REQUEST:\n{instruction.Trim()}";

        var stream = ollama.GenerateStreamAsync(model, systemPrompt, userPrompt, cancellationToken);
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            string fragment;
            try
            {
                if (!await enumerator.MoveNextAsync()) break;
                fragment = enumerator.Current;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                run.Status = SentinelAiRunStatuses.Failed;
                run.Output = "Generation failed before producing a reviewable response.";
                db.SentinelAiRuns.Add(run);
                await db.SaveChangesAsync(cancellationToken);
                throw;
            }

            output.Append(fragment);
            yield return new SentinelAiStreamChunk(fragment, null);
        }

        run.Output = output.ToString().Trim();
        if (run.Output.Length == 0)
        {
            run.Status = SentinelAiRunStatuses.Failed;
            run.Output = "SentinelGPT returned an empty response.";
            db.SentinelAiRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("SentinelGPT returned an empty response.");
        }

        run.CitationsJson = JsonSerializer.Serialize(citations);
        db.SentinelAiRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        yield return new SentinelAiStreamChunk(string.Empty, ToView(run, citations));
    }

    public async Task<IReadOnlyList<SentinelAiRunView>> ListRunsAsync(Guid? wikiPageId, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // SQLite/EF Core can't translate an ORDER BY on DateTimeOffset - materialize the
        // (usually small, per-page) result set first and sort client-side.
        var runs = await db.SentinelAiRuns.AsNoTracking()
            .Where(run => wikiPageId == null || run.WikiPageId == wikiPageId)
            .ToListAsync(cancellationToken);
        return runs
            .OrderByDescending(run => run.CreatedAt)
            .Take(Math.Clamp(maxResults, 1, 100))
            .Select(run => ToView(run, DeserializeCitations(run.CitationsJson)))
            .ToList();
    }

    public async Task<IReadOnlyList<SentinelGptConversationView>> ListConversationsAsync(
        string requestedBy,
        int maxResults = 40,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runs = await db.SentinelAiRuns.AsNoTracking()
            .Where(run => run.CreatedBy == requestedBy)
            .ToListAsync(cancellationToken);

        return runs
            .GroupBy(run => run.ConversationId == Guid.Empty ? run.Id : run.ConversationId)
            .Select(group =>
            {
                var ordered = group.OrderBy(run => run.CreatedAt).ToList();
                var first = ordered[0];
                var last = ordered[^1];
                return new SentinelGptConversationView(
                    group.Key,
                    ConversationTitle(first.Instruction),
                    Preview(last.Output),
                    last.Model,
                    last.CreatedAt,
                    ordered.Count);
            })
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .Take(Math.Clamp(maxResults, 1, 100))
            .ToList();
    }

    public async Task<IReadOnlyList<SentinelAiRunView>> ListConversationRunsAsync(
        Guid conversationId,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runs = await db.SentinelAiRuns.AsNoTracking()
            .Where(run => (run.ConversationId == conversationId || (run.ConversationId == Guid.Empty && run.Id == conversationId))
                && run.CreatedBy == requestedBy)
            .ToListAsync(cancellationToken);
        return runs
            .OrderBy(run => run.CreatedAt)
            .Select(run => ToView(run, DeserializeCitations(run.CitationsJson)))
            .ToList();
    }

    public async Task ReviewAsync(Guid runId, bool approved, string performedBy, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.SentinelAiRuns.FirstOrDefaultAsync(item => item.Id == runId, cancellationToken)
            ?? throw new InvalidOperationException("SentinelGPT run not found.");
        run.Status = approved ? SentinelAiRunStatuses.Approved : SentinelAiRunStatuses.Rejected;
        run.ReviewedAt = DateTimeOffset.UtcNow;
        run.ReviewedBy = performedBy;
        run.UpdatedAt = run.ReviewedAt;
        run.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Ranked top-K retrieval (reusing the same SentinelWorkspaceService.SearchAsync the
    // sidebar search box already uses) rather than the old "dump the 30 most-recently-updated
    // pages and 12 databases wholesale" approach. Besides being far more prompt-token-
    // efficient, it produces a citation list a reviewer can actually check the answer against.
    // The current page (if any) is always pinned first regardless of whether it matches the
    // instruction's search terms, since asking about the open page shouldn't depend on that.
    private async Task<(string Context, List<SentinelAiCitation> Citations)> BuildGroundedContextAsync(
        IAppDbContext db, Guid? pageId, string instruction, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var citations = new List<SentinelAiCitation>();
        var citedIds = new HashSet<Guid>();

        if (pageId is { } pinnedPageId)
        {
            var pinnedPage = await db.WikiPages.AsNoTracking().FirstOrDefaultAsync(page => page.Id == pinnedPageId, cancellationToken);
            if (pinnedPage is not null)
            {
                AppendPage(builder, pinnedPage);
                citations.Add(new SentinelAiCitation(pinnedPage.Id, false, pinnedPage.Title));
                citedIds.Add(pinnedPage.Id);
            }
        }

        var results = await workspaceService.SearchAsync(instruction, maxResults: 6, cancellationToken);
        foreach (var result in results)
        {
            if (!citedIds.Add(result.Id)) continue;

            if (result.IsDatabase)
            {
                var database = await db.WikiDatabases.AsNoTracking()
                    .Include(item => item.Properties)
                    .Include(item => item.Rows)
                    .FirstOrDefaultAsync(item => item.Id == result.Id, cancellationToken);
                if (database is null) continue;
                AppendDatabase(builder, database);
            }
            else
            {
                var page = await db.WikiPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == result.Id, cancellationToken);
                if (page is null) continue;
                AppendPage(builder, page);
            }

            citations.Add(new SentinelAiCitation(result.Id, result.IsDatabase, result.Title));
        }

        return (builder.ToString(), citations);
    }

    private static void AppendPage(StringBuilder builder, WikiPage page)
    {
        var text = string.Join(" ", WikiBlockJson.ParseBlocks(page.BlocksJson).Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 240)));
        builder.AppendLine($"PAGE: {page.Title}\n{text}");
    }

    private static void AppendDatabase(StringBuilder builder, WikiDatabase database)
    {
        var titleProperty = database.Properties.FirstOrDefault(property => property.Type == WikiDatabasePropertyTypes.Title);
        var titles = titleProperty is null ? [] : database.Rows.Take(30)
            .Select(row => WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), titleProperty.Id) ?? "Untitled")
            .ToList();
        builder.AppendLine($"DATABASE: {database.Title}\nROWS: {string.Join(", ", titles)}");
    }

    private static string SystemPrompt(string action) =>
        $"You are SentinelGPT inside a private knowledge workspace. Perform the '{action}' task using the supplied workspace context. " +
        "Never invent a source, person, decision, or fact that is absent from the context. Clearly label uncertainty. " +
        "Return useful plain text that can be inserted into a page. For meeting notes, include summary, decisions, and action items.";

    private static string ConversationTitle(string instruction)
    {
        var normalized = string.Join(' ', instruction.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 54 ? normalized : $"{normalized[..51]}…";
    }

    private static string Preview(string output)
    {
        var normalized = string.Join(' ', output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 88 ? normalized : $"{normalized[..85]}…";
    }

    private static List<SentinelAiCitation> DeserializeCitations(string citationsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SentinelAiCitation>>(citationsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SentinelAiRunView ToView(SentinelAiRun run, IReadOnlyList<SentinelAiCitation> citations) =>
        new(run.Id, run.ConversationId == Guid.Empty ? run.Id : run.ConversationId, run.WikiPageId, run.Action,
            run.Instruction, run.Output, run.Status, run.Model, run.CreatedBy, run.CreatedAt, citations);
}
