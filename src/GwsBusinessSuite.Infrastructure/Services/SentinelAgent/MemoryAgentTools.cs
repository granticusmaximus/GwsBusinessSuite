using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// "Remember that we host on DigitalOcean": facts the person asks SentinelGPT to keep for later
// chats. Each person's own; the runner puts them in every turn's prompt, and the What SentinelGPT
// did page lists them.
public sealed class MemoryAgentTools(IAppDbContext db, TimeProvider time) : ISentinelAgentToolset
{
    public const string Link = "/admin/sentinelgpt/activity?tab=memories";

    public SentinelAgentArea Area { get; } = new("memory", "what you've been asked to remember", [], Always: true);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "remember",
            "Remember a fact or preference the person gives you, for later chats (\"remember that ...\"). One short sentence.",
            SentinelAgentJson.Schema(("fact", "string", "What to remember, as a short sentence", true)),
            SentinelAgentRisk.Change, [],
            args => $"Remembering \"{args.OptionalText("fact")}\"",
            RememberAsync,
            UndoRememberAsync),
        new SentinelAgentTool(
            "forget",
            "Forget something you were asked to remember, by its memory id.",
            SentinelAgentJson.Schema(("memoryId", "string", "The memory id", true)),
            SentinelAgentRisk.Change, [],
            _ => "Forgetting a memory",
            ForgetAsync,
            UndoForgetAsync)
    ];

    private async Task<SentinelAgentToolResult> RememberAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var memory = new SentinelAgentMemory
        {
            Fact = args.Text("fact"),
            ConversationId = context.ConversationId,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = context.User.Username
        };
        db.SentinelAgentMemories.Add(memory);
        await db.SaveChangesAsync(ct);
        return SentinelAgentToolResult.Of(new { remembered = memory.Fact },
            new SentinelAgentChange($"Remembered: {memory.Fact}", "memory", memory.Id.ToString(), memory.Fact, Link,
                SentinelAgentJson.Serialize(new MemoryUndo(memory.Id, memory.Fact))));
    }

    private async Task UndoRememberAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<MemoryUndo>(undoJson);
        if (await db.SentinelAgentMemories.FirstOrDefaultAsync(memory => memory.Id == undo.MemoryId && memory.CreatedBy == context.User.Username, ct) is { } memory)
        {
            db.SentinelAgentMemories.Remove(memory);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<SentinelAgentToolResult> ForgetAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var id = args.Id("memoryId");
        var memory = await db.SentinelAgentMemories.FirstOrDefaultAsync(item => item.Id == id && item.CreatedBy == context.User.Username, ct)
            ?? throw new SentinelAgentToolException($"There's no memory {id}. The memories are listed in your instructions with their ids.");
        db.SentinelAgentMemories.Remove(memory);
        await db.SaveChangesAsync(ct);
        return SentinelAgentToolResult.Of(new { forgot = memory.Fact },
            new SentinelAgentChange($"Forgot: {memory.Fact}", "memory", memory.Id.ToString(), memory.Fact, Link,
                SentinelAgentJson.Serialize(new MemoryUndo(memory.Id, memory.Fact))));
    }

    private async Task UndoForgetAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<MemoryUndo>(undoJson);
        if (await db.SentinelAgentMemories.AnyAsync(memory => memory.Id == undo.MemoryId, ct)) return;
        db.SentinelAgentMemories.Add(new SentinelAgentMemory
        {
            Id = undo.MemoryId,
            Fact = undo.Fact,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = context.User.Username
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed record MemoryUndo(Guid MemoryId, string Fact);
}
