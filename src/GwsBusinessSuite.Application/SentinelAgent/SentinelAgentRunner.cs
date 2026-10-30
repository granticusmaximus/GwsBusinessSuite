using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Application.SentinelAgent;

// One turn: the model thinks on this computer's Ollama (through the browser relay) and the tools
// act here, on the server, as the person asking. Model is any IOllamaService, so the loop doesn't
// care where the model runs.
public sealed record SentinelAgentRequest(
    Guid ConversationId,
    SentinelAgentUser User,
    string Message,
    string? PageContext,
    IOllamaService Model,
    string ModelName,
    int MaxRounds = SentinelAgentRunner.LocalRounds);

public sealed record SentinelAgentActionView(Guid Id, string Tool, string Summary, string Status, string? Link, bool CanUndo)
{
    public bool AwaitingConfirm => Status == SentinelAgentActionStatuses.AwaitingConfirm;
}

public abstract record SentinelAgentEvent;

// What it's doing now, for the person to watch ("Adding 3 tasks to 'Groceries'").
public sealed record SentinelAgentStep(string Text) : SentinelAgentEvent;

// A change made, or a Confirm card waiting.
public sealed record SentinelAgentActed(SentinelAgentActionView Action) : SentinelAgentEvent;

public sealed record SentinelAgentTurn(string Answer, IReadOnlyList<SentinelAgentActionView> Actions, string Model);

public sealed class SentinelAgentRunner(IServiceScopeFactory scopes, TimeProvider time, ILogger<SentinelAgentRunner> logger)
{
    // Room for a real multi-step job (find, read, then change). Only a local model runs the agent:
    // in the 30-10-26 live check the droplet's llama3.2 claimed a list it never made and wrote a
    // site page from nothing, so without one the panel answers on the server instead.
    public const int LocalRounds = 8;
    public const string UndoToolName = "undo_change";
    private const int HistoryTurns = 6;
    private const int HistoryCharacters = 1_500;
    private const int WorkingSetSize = 20;
    private const int PageContextCharacters = 4_000;

    public async Task<SentinelAgentTurn> RunAsync(SentinelAgentRequest request, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Message);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var tools = ToolsFor(scope.ServiceProvider, request.User);
        var context = new SentinelAgentToolContext(request.User, request.ConversationId);

        var messages = new List<OllamaChatMessage> { new("system", await SystemPromptAsync(db, request, cancellationToken)) };
        messages.AddRange(await HistoryAsync(db, request, cancellationToken));
        messages.Add(new OllamaChatMessage("user", request.Message.Trim()));
        var definitions = tools.Values.Select(tool => new OllamaToolDefinition(tool.Name, tool.Description, tool.ParametersJson)).ToList();

        var actions = new List<SentinelAgentActionView>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        string? answer = null;
        for (var round = 0; round < Math.Max(1, request.MaxRounds) && answer is null; round++)
        {
            var response = await request.Model.ChatAsync(request.ModelName, messages, definitions, cancellationToken);
            if (response.ToolCalls.Count == 0)
            {
                answer = response.Content.Trim();
                break;
            }

            messages.Add(new OllamaChatMessage("assistant", response.Content, ToolCalls: response.ToolCalls));
            foreach (var call in response.ToolCalls)
            {
                var result = await CallAsync(db, tools, call, context, actions, done, onEvent, cancellationToken);
                messages.Add(new OllamaChatMessage("tool", result, Name: call.Name));
            }
        }

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = actions.Count > 0
                ? "Done: " + string.Join("; ", actions.Select(action => action.Summary)) + "."
                : $"I couldn't finish that in {request.MaxRounds} steps. Try asking for one thing at a time.";
        }

        db.SentinelAiRuns.Add(new SentinelAiRun
        {
            ConversationId = request.ConversationId,
            Action = SentinelAiActions.Agent,
            Instruction = request.Message.Trim(),
            Output = answer,
            Model = request.ModelName,
            Status = SentinelAiRunStatuses.Completed,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = request.User.Username
        });
        await db.SaveChangesAsync(cancellationToken);
        return new SentinelAgentTurn(answer, actions, request.ModelName);
    }

    // The person's answer to a Confirm card. Approving runs exactly the call that was shown.
    public async Task<SentinelAgentActionView> ConfirmAsync(Guid actionId, SentinelAgentUser user, bool approve, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var action = await FindAsync(db, actionId, user, cancellationToken);
        if (action.Status != SentinelAgentActionStatuses.AwaitingConfirm)
            throw new InvalidOperationException($"That was already {Describe(action.Status)}.");

        var tools = ToolsFor(scope.ServiceProvider, user);
        tools.TryGetValue(action.Tool, out var tool);
        if (!approve)
        {
            action.Status = SentinelAgentActionStatuses.Declined;
        }
        else
        {
            if (tool is null) throw new InvalidOperationException("You can't do that any more.");
            var arguments = JsonNode.Parse(action.ArgumentsJson) as JsonObject ?? [];
            try
            {
                var result = await tool.RunAsync(arguments, new SentinelAgentToolContext(user, action.ConversationId), cancellationToken);
                Apply(action, result.Change);
                action.Status = SentinelAgentActionStatuses.Done;
            }
            catch (SentinelAgentToolException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }

        action.UpdatedAt = time.GetUtcNow();
        action.UpdatedBy = user.Username;
        await db.SaveChangesAsync(cancellationToken);
        return View(action, tool);
    }

    public async Task<SentinelAgentActionView> UndoAsync(Guid actionId, SentinelAgentUser user, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var tools = ToolsFor(scope.ServiceProvider, user);
        var action = await FindAsync(db, actionId, user, cancellationToken);
        var tool = await UndoCoreAsync(db, tools, action, user, cancellationToken);
        return View(action, tool);
    }

    private async Task<ISentinelAgentTool> UndoCoreAsync(
        IAppDbContext db, IReadOnlyDictionary<string, ISentinelAgentTool> tools, SentinelAgentAction action, SentinelAgentUser user, CancellationToken cancellationToken)
    {
        if (action.Status != SentinelAgentActionStatuses.Done || action.UndoJson is null)
            throw new InvalidOperationException(action.Status == SentinelAgentActionStatuses.Undone ? "That's already undone." : "That can't be undone.");
        if (!tools.TryGetValue(action.Tool, out var tool))
            throw new InvalidOperationException("You can't undo that any more.");
        try
        {
            await tool.UndoAsync(action.UndoJson, new SentinelAgentToolContext(user, action.ConversationId), cancellationToken);
        }
        catch (SentinelAgentToolException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        action.Status = SentinelAgentActionStatuses.Undone;
        action.UpdatedAt = time.GetUtcNow();
        action.UpdatedBy = user.Username;
        await db.SaveChangesAsync(cancellationToken);
        return tool;
    }

    private async Task<string> CallAsync(
        IAppDbContext db,
        IReadOnlyDictionary<string, ISentinelAgentTool> tools,
        OllamaToolCall call,
        SentinelAgentToolContext context,
        List<SentinelAgentActionView> actions,
        HashSet<string> done,
        Func<SentinelAgentEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        if (call.Name == UndoToolName) return await UndoForModelAsync(db, tools, call, context, actions, onEvent, cancellationToken);
        if (!tools.TryGetValue(call.Name, out var tool)) return Problem($"There's no tool called {call.Name}.");
        JsonObject arguments;
        try
        {
            arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return Problem("The arguments weren't valid JSON.");
        }

        // Small models sometimes send the same change twice in a row; the second one would make a
        // duplicate list or card, so it's answered from the first.
        var key = call.Name + arguments.ToJsonString();
        if (tool.Risk != SentinelAgentRisk.Read && !done.Add(key))
            return Problem("That's already done in this turn. Don't repeat it; carry on or answer.");

        await onEvent(new SentinelAgentStep(tool.Describe(arguments)));
        try
        {
            if (tool.Risk == SentinelAgentRisk.Confirm)
            {
                var proposal = new SentinelAgentAction
                {
                    ConversationId = context.ConversationId,
                    Tool = tool.Name,
                    ArgumentsJson = arguments.ToJsonString(),
                    Status = SentinelAgentActionStatuses.AwaitingConfirm,
                    Summary = await tool.PreviewAsync(arguments, context, cancellationToken),
                    CreatedAt = time.GetUtcNow(),
                    CreatedBy = context.User.Username
                };
                db.SentinelAgentActions.Add(proposal);
                await db.SaveChangesAsync(cancellationToken);
                var view = View(proposal, tool);
                actions.Add(view);
                await onEvent(new SentinelAgentActed(view));
                return SentinelAgentJson.Serialize(new
                {
                    status = "waiting for the person to confirm",
                    proposal = proposal.Summary,
                    note = "Nothing has happened yet. Tell them it's waiting for their Confirm; don't call this again."
                });
            }

            var result = await tool.RunAsync(arguments, context, cancellationToken);
            if (result.Change is { } change)
            {
                var action = new SentinelAgentAction
                {
                    ConversationId = context.ConversationId,
                    Tool = tool.Name,
                    ArgumentsJson = arguments.ToJsonString(),
                    Status = SentinelAgentActionStatuses.Done,
                    CreatedAt = time.GetUtcNow(),
                    CreatedBy = context.User.Username
                };
                Apply(action, change);
                db.SentinelAgentActions.Add(action);
                await db.SaveChangesAsync(cancellationToken);
                var view = View(action, tool);
                actions.Add(view);
                await onEvent(new SentinelAgentActed(view));
            }

            return result.ForModel;
        }
        catch (SentinelAgentToolException ex)
        {
            done.Remove(key);
            return Problem(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or DbUpdateException or KeyNotFoundException)
        {
            done.Remove(key);
            logger.LogWarning(ex, "SentinelGPT's {Tool} failed for {User}.", tool.Name, context.User.Username);
            return Problem($"That didn't work: {ex.Message}");
        }
    }

    private async Task<string> UndoForModelAsync(
        IAppDbContext db, IReadOnlyDictionary<string, ISentinelAgentTool> tools, OllamaToolCall call, SentinelAgentToolContext context,
        List<SentinelAgentActionView> actions, Func<SentinelAgentEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        try
        {
            var arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) as JsonObject ?? [];
            var action = await FindAsync(db, arguments.Id("actionId"), context.User, cancellationToken);
            if (action.ConversationId != context.ConversationId) return Problem("Only changes made in this chat can be undone here.");
            await onEvent(new SentinelAgentStep($"Undoing: {action.Summary}"));
            var tool = await UndoCoreAsync(db, tools, action, context.User, cancellationToken);
            var view = View(action, tool);
            actions.Add(view);
            await onEvent(new SentinelAgentActed(view));
            return SentinelAgentJson.Serialize(new { undone = action.Summary });
        }
        catch (Exception ex) when (ex is SentinelAgentToolException or InvalidOperationException or JsonException)
        {
            return Problem(ex.Message);
        }
    }

    private static Dictionary<string, ISentinelAgentTool> ToolsFor(IServiceProvider services, SentinelAgentUser user)
    {
        var tools = services.GetServices<ISentinelAgentToolset>()
            .SelectMany(toolset => toolset.Tools)
            .Where(user.MayUse)
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        tools[UndoToolName] = new SentinelAgentTool(
            UndoToolName,
            "Undo one change you made earlier in this chat, by its action id from 'What this chat has changed'. Use when the person asks to undo or take something back.",
            SentinelAgentJson.Schema(("actionId", "string", "The action id from the list of changes in this chat", true)),
            SentinelAgentRisk.Change, [], _ => "Undoing a change",
            (_, _, _) => throw new SentinelAgentToolException("Undo runs through the runner."));
        return tools;
    }

    private async Task<string> SystemPromptAsync(IAppDbContext db, SentinelAgentRequest request, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().ToLocalTime();
        var prompt = new StringBuilder($"""
            You are SentinelGPT, the assistant inside GWS Business Suite. You can look things up and make changes in the app with your tools, acting as {request.User.Username} ({request.User.RoleLabel}). Today is {now.ToString("dddd dd-MM-yy", CultureInfo.InvariantCulture)}, {now:HH:mm}.

            How to work:
            - When asked to do something, research first, then do it. Look up what you need (Sentinel, the app, World News, the web) so what you write is right, then make the change with the tools. Don't tell the person how to do it themselves.
            - Write from what you found, not from memory, and say where facts came from when it matters.
            - Find things before changing them, unless "What this chat has changed" below already gives the id.
            - "That list", "the page", "the board" and the like mean the most recent matching item below.
            - Use ids exactly as the tools return them. Never make up an id.
            - Deleting and publishing only propose: say it's waiting for their Confirm.
            - If a tool returns an error, fix the call and try again, or say plainly what's stopping you.
            - For facts, use only what the tools and the page give you. If you don't know, say so.
            - When you've finished, answer in one to three short sentences saying what you did. The app shows links and Undo buttons, so don't paste ids or links.
            - Questions that need no change get a direct answer.
            """);

        var changes = (await db.SentinelAgentActions.AsNoTracking()
                .Where(action => action.ConversationId == request.ConversationId && action.CreatedBy == request.User.Username)
                .ToListAsync(cancellationToken))
            .OrderBy(action => action.CreatedAt)
            .TakeLast(WorkingSetSize)
            .ToList();
        prompt.AppendLine().AppendLine().AppendLine("What this chat has changed (oldest first):");
        if (changes.Count == 0) prompt.AppendLine("- Nothing yet.");
        foreach (var action in changes)
        {
            prompt.Append($"- [{Describe(action.Status)}] {action.Summary}");
            if (action.ObjectId is not null) prompt.Append($" ({action.ObjectKind} id {action.ObjectId})");
            prompt.AppendLine($" - action id {action.Id}");
        }

        if (!string.IsNullOrWhiteSpace(request.PageContext))
        {
            var page = request.PageContext.Trim();
            prompt.AppendLine().AppendLine("What's on the person's screen:")
                .AppendLine(page.Length <= PageContextCharacters ? page : page[..PageContextCharacters]);
        }

        return prompt.ToString();
    }

    private static async Task<IReadOnlyList<OllamaChatMessage>> HistoryAsync(IAppDbContext db, SentinelAgentRequest request, CancellationToken cancellationToken)
    {
        var turns = (await db.SentinelAiRuns.AsNoTracking()
                .Where(run => run.ConversationId == request.ConversationId && run.CreatedBy == request.User.Username && run.Action == SentinelAiActions.Agent)
                .ToListAsync(cancellationToken))
            .OrderBy(run => run.CreatedAt)
            .TakeLast(HistoryTurns);
        return turns.SelectMany(run => new[]
        {
            new OllamaChatMessage("user", Limit(run.Instruction)),
            new OllamaChatMessage("assistant", Limit(run.Output))
        }).ToList();
    }

    private static async Task<SentinelAgentAction> FindAsync(IAppDbContext db, Guid actionId, SentinelAgentUser user, CancellationToken cancellationToken) =>
        await db.SentinelAgentActions.FirstOrDefaultAsync(action => action.Id == actionId && action.CreatedBy == user.Username, cancellationToken)
            ?? throw new InvalidOperationException("That change isn't in your chats.");

    private static void Apply(SentinelAgentAction action, SentinelAgentChange? change)
    {
        if (change is null) return;
        action.Summary = change.Summary;
        action.ObjectKind = change.ObjectKind;
        action.ObjectId = change.ObjectId;
        action.ObjectTitle = change.ObjectTitle;
        action.Link = change.Link;
        action.UndoJson = change.UndoJson;
    }

    private static SentinelAgentActionView View(SentinelAgentAction action, ISentinelAgentTool? tool) =>
        new(action.Id, action.Tool, action.Summary, action.Status, action.Link,
            tool is { CanUndo: true } && action.UndoJson is not null && action.Status == SentinelAgentActionStatuses.Done);

    private static string Describe(string status) => status switch
    {
        SentinelAgentActionStatuses.AwaitingConfirm => "waiting for Confirm",
        SentinelAgentActionStatuses.Declined => "declined",
        SentinelAgentActionStatuses.Undone => "undone",
        SentinelAgentActionStatuses.Failed => "failed",
        _ => "done"
    };

    private static string Problem(string message) => SentinelAgentJson.Serialize(new { error = message });

    private static string Limit(string text) => text.Length <= HistoryCharacters ? text : text[..HistoryCharacters] + "…";
}
