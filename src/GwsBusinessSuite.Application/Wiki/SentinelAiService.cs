using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using GwsBusinessSuite.Application.Settings;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Application.Wiki;

public sealed class SentinelAiService(
    IAppDbContextFactory dbContextFactory,
    IOllamaService ollama,
    ISiteSettingsService siteSettings) : ISentinelAiService
{
    private static readonly HashSet<string> AllowedActions =
    [
        SentinelAiActions.Ask, SentinelAiActions.Summarize, SentinelAiActions.Rewrite,
        SentinelAiActions.Translate, SentinelAiActions.Research, SentinelAiActions.MeetingNotes,
        SentinelAiActions.DatabaseAutofill
    ];

    public async Task<SentinelAiRunView> RunAsync(Guid? wikiPageId, string action, string instruction, string performedBy, CancellationToken cancellationToken = default)
    {
        if (!AllowedActions.Contains(action)) throw new ArgumentException("Unknown Sentinel AI action.", nameof(action));
        if (string.IsNullOrWhiteSpace(instruction)) throw new ArgumentException("An instruction or source text is required.", nameof(instruction));
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await siteSettings.GetSettingsAsync(cancellationToken);
        var model = string.IsNullOrWhiteSpace(settings.OllamaModelOverride) ? ContentStudioOptions.DefaultModel : settings.OllamaModelOverride;
        var context = await BuildWorkspaceContextAsync(db, wikiPageId, cancellationToken);
        var run = new SentinelAiRun
        {
            WikiPageId = wikiPageId, Action = action, Instruction = instruction.Trim(), Model = model,
            Status = SentinelAiRunStatuses.Completed, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = performedBy
        };

        try
        {
            run.Output = (await ollama.GenerateAsync(model, SystemPrompt(action), $"WORKSPACE CONTEXT:\n{context}\n\nREQUEST:\n{instruction.Trim()}", cancellationToken)).Trim();
            if (run.Output.Length == 0) throw new InvalidOperationException("The AI returned an empty response.");
        }
        catch
        {
            run.Status = SentinelAiRunStatuses.Failed;
            run.Output = "Generation failed before producing a reviewable response.";
            db.SentinelAiRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }

        db.SentinelAiRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return ToView(run);
    }

    public async Task<IReadOnlyList<SentinelAiRunView>> ListRunsAsync(Guid? wikiPageId, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SentinelAiRuns.AsNoTracking()
            .Where(run => wikiPageId == null || run.WikiPageId == wikiPageId)
            .OrderByDescending(run => run.CreatedAt)
            .Take(Math.Clamp(maxResults, 1, 100))
            .Select(run => new SentinelAiRunView(run.Id, run.WikiPageId, run.Action, run.Instruction, run.Output, run.Status, run.Model, run.CreatedBy, run.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task ReviewAsync(Guid runId, bool approved, string performedBy, CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await db.SentinelAiRuns.FirstOrDefaultAsync(item => item.Id == runId, cancellationToken)
            ?? throw new InvalidOperationException("Sentinel AI run not found.");
        run.Status = approved ? SentinelAiRunStatuses.Approved : SentinelAiRunStatuses.Rejected;
        run.ReviewedAt = DateTimeOffset.UtcNow;
        run.ReviewedBy = performedBy;
        run.UpdatedAt = run.ReviewedAt;
        run.UpdatedBy = performedBy;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string> BuildWorkspaceContextAsync(IAppDbContext db, Guid? pageId, CancellationToken cancellationToken)
    {
        var pages = await db.WikiPages.AsNoTracking().OrderByDescending(page => page.UpdatedAt ?? page.CreatedAt).Take(30).ToListAsync(cancellationToken);
        var databases = await db.WikiDatabases.AsNoTracking().Include(database => database.Properties).Include(database => database.Rows).Take(12).ToListAsync(cancellationToken);
        var builder = new StringBuilder();
        foreach (var page in pages.OrderByDescending(page => page.Id == pageId))
        {
            var text = string.Join(" ", WikiBlockJson.ParseBlocks(page.BlocksJson).Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 240)));
            builder.AppendLine($"PAGE: {page.Title}\n{text}");
        }
        foreach (var database in databases)
        {
            var titleProperty = database.Properties.FirstOrDefault(property => property.Type == WikiDatabasePropertyTypes.Title);
            var titles = titleProperty is null ? [] : database.Rows.Take(30)
                .Select(row => WikiPropertyValues.GetText(WikiPropertyValues.ParseObject(row.PropertyValuesJson), titleProperty.Id) ?? "Untitled")
                .ToList();
            builder.AppendLine($"DATABASE: {database.Title}\nROWS: {string.Join(", ", titles)}");
        }
        return builder.ToString();
    }

    private static string SystemPrompt(string action) =>
        $"You are Sentinel AI inside a private knowledge workspace. Perform the '{action}' task using the supplied workspace context. " +
        "Never invent a source, person, decision, or fact that is absent from the context. Clearly label uncertainty. " +
        "Return useful plain text that can be inserted into a page. For meeting notes, include summary, decisions, and action items.";

    private static SentinelAiRunView ToView(SentinelAiRun run) =>
        new(run.Id, run.WikiPageId, run.Action, run.Instruction, run.Output, run.Status, run.Model, run.CreatedBy, run.CreatedAt);
}
