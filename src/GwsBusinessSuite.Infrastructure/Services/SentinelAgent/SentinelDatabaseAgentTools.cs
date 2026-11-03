using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

public sealed class SentinelDatabaseAgentTools(IWikiDatabaseService databases, ISentinelAccessService access, IAppDbContext db) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin, AppRoles.Author, AppRoles.Contributor];
    public SentinelAgentArea Area { get; } = new("databases", "Sentinel databases, rows and properties", ["database", "table", "row", "property", "record"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool("list_sentinel_databases", "List Sentinel databases the signed-in user can view.", SentinelAgentJson.Schema(), SentinelAgentRisk.Read, Roles,
            _ => "Listing Sentinel databases", ListAsync),
        new SentinelAgentTool("read_sentinel_database", "Read a Sentinel database's schema and rows. Find it first and pass its id.",
            SentinelAgentJson.Schema(("databaseId", "string", "The database id", true)), SentinelAgentRisk.Read, Roles,
            _ => "Reading a Sentinel database", ReadAsync),
        new SentinelAgentTool("create_sentinel_database", "Create a Sentinel database. This is routine, reversible CRUD.",
            SentinelAgentJson.Schema(("title", "string", "The database title", true), ("parentPageId", "string", "Optional parent Sentinel page id", false)),
            SentinelAgentRisk.Change, Roles, args => $"Creating database '{args.OptionalText("title")}'", CreateAsync, UndoCreateAsync),
        new SentinelAgentTool("rename_sentinel_database", "Rename a Sentinel database the signed-in user may edit.",
            SentinelAgentJson.Schema(("databaseId", "string", "The database id", true), ("title", "string", "The new title", true)),
            SentinelAgentRisk.Change, Roles, args => $"Renaming a database to '{args.OptionalText("title")}'", RenameAsync, UndoRenameAsync),
        new SentinelAgentTool("create_sentinel_database_row", "Create a row. Values is a JSON object keyed by property id; use read_sentinel_database first.",
            SentinelAgentJson.Schema(("databaseId", "string", "The database id", true), ("values", "string", "JSON object keyed by property id", true)),
            SentinelAgentRisk.Change, Roles, _ => "Creating a database row", CreateRowAsync, UndoCreateRowAsync),
        new SentinelAgentTool("update_sentinel_database_row", "Update a row's property values. Values replaces the row's values, so read the database first and preserve unchanged values.",
            SentinelAgentJson.Schema(("databaseId", "string", "The database id", true), ("rowId", "string", "The row id", true), ("values", "string", "Complete JSON object keyed by property id", true)),
            SentinelAgentRisk.Change, Roles, _ => "Updating a database row", UpdateRowAsync, UndoUpdateRowAsync),
        new SentinelAgentTool("trash_sentinel_database_row", "Move a database row to trash. Nothing happens until Confirm.",
            SentinelAgentJson.Schema(("databaseId", "string", "The database id", true), ("rowId", "string", "The row id", true)),
            SentinelAgentRisk.Confirm, Roles, _ => "Proposing to trash a database row", TrashRowAsync, UndoTrashRowAsync,
            preview: async (args, context, ct) => { var row = await RowAsync(args.Id("databaseId"), args.Id("rowId"), context, SentinelAccessLevels.Edit, ct); return $"Move database row {row.Id} to the trash"; }),
        new SentinelAgentTool("trash_sentinel_database", "Move a Sentinel database to trash. Nothing happens until Confirm.",
            SentinelAgentJson.Schema(("databaseId", "string", "The database id", true)), SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to trash a database", TrashAsync, UndoTrashAsync,
            preview: async (args, context, ct) => $"Move the Sentinel database '{(await DatabaseAsync(args.Id("databaseId"), context, SentinelAccessLevels.Edit, ct)).Title}' to the trash")
    ];

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var rows = new List<object>();
        foreach (var database in await databases.ListDatabasesAsync(cancellationToken: ct))
            if (await access.CanAccessAsync(database.Id, true, context.User.Username, SentinelAccessLevels.View, ct))
                rows.Add(new { database.Id, database.Title, database.Icon, database.IsLocked });
        return SentinelAgentToolResult.Of(new { databases = rows });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var database = await DatabaseAsync(args.Id("databaseId"), context, SentinelAccessLevels.View, ct);
        var snapshot = await databases.GetInlineDatabaseAsync(database.Id, ct) ?? throw new SentinelAgentToolException("That database is unavailable.");
        return SentinelAgentToolResult.Of(new
        {
            database = new { database.Id, database.Title, database.Icon, database.IsLocked },
            properties = snapshot.Properties.Select(p => new { p.Id, p.Name, p.Type }),
            rows = (await db.WikiDatabaseRows.AsNoTracking().Where(row => row.WikiDatabaseId == database.Id && row.TrashedAt == null).ToListAsync(ct))
                .Select(row => new { row.Id, values = JsonNode.Parse(row.PropertyValuesJson) })
        });
    }

    private async Task<SentinelAgentToolResult> CreateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var database = await databases.CreateDatabaseAsync(args.Text("title"), args.OptionalId("parentPageId"), context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { database.Id, database.Title }, Change($"Created database '{database.Title}'", database, SentinelAgentJson.Serialize(new { database.Id })));
    }

    private async Task UndoCreateAsync(string json, SentinelAgentToolContext context, CancellationToken ct)
    {
        var id = SentinelAgentJson.Deserialize<IdUndo>(json).Id;
        await RequireAsync(id, context, SentinelAccessLevels.Edit, ct);
        await databases.DeleteDatabasePermanentlyAsync(id, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> RenameAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var before = await DatabaseAsync(args.Id("databaseId"), context, SentinelAccessLevels.Edit, ct);
        var saved = await databases.RenameDatabaseAsync(before.Id, args.Text("title"), before.Icon, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { saved.Id, saved.Title }, Change($"Renamed database '{before.Title}' to '{saved.Title}'", saved,
            SentinelAgentJson.Serialize(new { saved.Id, Title = before.Title, before.Icon })));
    }

    private async Task UndoRenameAsync(string json, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<RenameUndo>(json);
        await RequireAsync(undo.Id, context, SentinelAccessLevels.Edit, ct);
        await databases.RenameDatabaseAsync(undo.Id, undo.Title, undo.Icon, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> CreateRowAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var id = args.Id("databaseId");
        await RequireAsync(id, context, SentinelAccessLevels.Edit, ct);
        var row = await databases.SaveRowAsync(id, new WikiDatabaseRowEditor { Values = Values(args) }, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { row.Id, values = JsonNode.Parse(row.PropertyValuesJson) }, RowChange("Created database row", id, row.Id, SentinelAgentJson.Serialize(new { DatabaseId = id, RowId = row.Id })));
    }

    private async Task UndoCreateRowAsync(string json, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<RowUndo>(json);
        await RequireAsync(undo.DatabaseId, context, SentinelAccessLevels.Edit, ct);
        await databases.DeleteRowPermanentlyAsync(undo.DatabaseId, undo.RowId, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> UpdateRowAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var databaseId = args.Id("databaseId"); var rowId = args.Id("rowId");
        var before = await RowAsync(databaseId, rowId, context, SentinelAccessLevels.Edit, ct);
        var saved = await databases.SaveRowAsync(databaseId, new WikiDatabaseRowEditor { Id = rowId, ParentRowId = before.ParentRowId, Values = Values(args) }, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { saved.Id, values = JsonNode.Parse(saved.PropertyValuesJson) }, RowChange("Updated database row", databaseId, rowId,
            SentinelAgentJson.Serialize(new { DatabaseId = databaseId, RowId = rowId, before.ParentRowId, Values = before.PropertyValuesJson })));
    }

    private async Task UndoUpdateRowAsync(string json, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<RowValuesUndo>(json);
        await RequireAsync(undo.DatabaseId, context, SentinelAccessLevels.Edit, ct);
        await databases.SaveRowAsync(undo.DatabaseId, new WikiDatabaseRowEditor { Id = undo.RowId, ParentRowId = undo.ParentRowId, Values = ParseValues(undo.Values) }, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> TrashRowAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var databaseId = args.Id("databaseId"); var rowId = args.Id("rowId");
        await RowAsync(databaseId, rowId, context, SentinelAccessLevels.Edit, ct);
        await databases.TrashRowAsync(databaseId, rowId, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { trashed = rowId }, RowChange("Trashed database row", databaseId, rowId, SentinelAgentJson.Serialize(new { DatabaseId = databaseId, RowId = rowId })));
    }

    private async Task UndoTrashRowAsync(string json, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<RowUndo>(json); await RequireAsync(undo.DatabaseId, context, SentinelAccessLevels.Edit, ct);
        await databases.RestoreRowAsync(undo.DatabaseId, undo.RowId, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> TrashAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var database = await DatabaseAsync(args.Id("databaseId"), context, SentinelAccessLevels.Edit, ct);
        await databases.TrashDatabaseAsync(database.Id, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { trashed = database.Title }, Change($"Trashed database '{database.Title}'", database, SentinelAgentJson.Serialize(new { database.Id })));
    }

    private async Task UndoTrashAsync(string json, SentinelAgentToolContext context, CancellationToken ct)
    {
        var id = SentinelAgentJson.Deserialize<IdUndo>(json).Id; await RequireAsync(id, context, SentinelAccessLevels.Edit, ct);
        await databases.RestoreDatabaseAsync(id, context.User.Username, ct);
    }

    private async Task<WikiDatabase> DatabaseAsync(Guid id, SentinelAgentToolContext context, string level, CancellationToken ct)
    {
        await RequireAsync(id, context, level, ct);
        return await databases.GetDatabaseAsync(id, ct) ?? throw new SentinelAgentToolException($"There's no Sentinel database with id {id}.");
    }

    private async Task<WikiDatabaseRow> RowAsync(Guid databaseId, Guid rowId, SentinelAgentToolContext context, string level, CancellationToken ct)
    {
        await RequireAsync(databaseId, context, level, ct);
        return await db.WikiDatabaseRows.AsNoTracking().FirstOrDefaultAsync(row => row.WikiDatabaseId == databaseId && row.Id == rowId, ct)
            ?? throw new SentinelAgentToolException($"There's no row with id {rowId} in that database.");
    }

    private async Task RequireAsync(Guid id, SentinelAgentToolContext context, string level, CancellationToken ct)
    {
        if (!await access.CanAccessAsync(id, true, context.User.Username, level, ct))
            throw new SentinelAgentToolException($"{context.User.Username} doesn't have {level.ToLowerInvariant()} access to that database.");
    }

    private static Dictionary<string, JsonNode?> Values(JsonObject args) => ParseValues(args.Text("values"));
    private static Dictionary<string, JsonNode?> ParseValues(string json)
    {
        try { return (JsonNode.Parse(json) as JsonObject ?? throw new Exception()).ToDictionary(item => item.Key, item => item.Value?.DeepClone()); }
        catch { throw new SentinelAgentToolException("'values' must be a JSON object keyed by property id."); }
    }
    private static SentinelAgentChange Change(string summary, WikiDatabase database, string undo) => new(summary, "sentinel-database", database.Id.ToString(), database.Title, $"/admin/sentinel/database/{database.Id}", undo);
    private static SentinelAgentChange RowChange(string summary, Guid databaseId, Guid rowId, string undo) => new(summary, "sentinel-database-row", rowId.ToString(), rowId.ToString(), $"/admin/sentinel/database/{databaseId}/row/{rowId}", undo);
    private sealed record IdUndo(Guid Id);
    private sealed record RenameUndo(Guid Id, string Title, string? Icon);
    private sealed record RowUndo(Guid DatabaseId, Guid RowId);
    private sealed record RowValuesUndo(Guid DatabaseId, Guid RowId, Guid? ParentRowId, string Values);
}
