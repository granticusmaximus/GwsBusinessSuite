using System.Text.Json.Nodes;
using System.Text.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Boards for the agent, through IKanbanBoardService like the board page itself. Boards are an
// Admin page (Board.razor's AdminOnly), so these are Admin only too.
public sealed class BoardAgentTools(IKanbanBoardService boards, IWikiDatabaseService databases, IAppDbContext db) : ISentinelAgentToolset
{
    private static readonly string[] AdminRoles = [AppRoles.Admin];
    private static readonly string[] BoardRoles = AppRoles.All;
    private const int CardsPerColumn = 20;

    public SentinelAgentArea Area { get; } = new("boards", "Kanban boards and cards", ["board", "card", "kanban", "column", "backlog", "sprint"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "list_boards",
            "List the Kanban boards with their ids and how many cards are open.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Read, BoardRoles,
            _ => "Looking at the boards",
            ListAsync),
        new SentinelAgentTool(
            "read_board",
            "Read one board: its columns (with ids) and the cards in each (with ids).",
            SentinelAgentJson.Schema(("boardId", "string", "The board id", true)),
            SentinelAgentRisk.Read, BoardRoles,
            _ => "Reading a board",
            ReadAsync),
        new SentinelAgentTool(
            "create_board",
            "Create a new Kanban board. Template: personal (Backlog, To do, Doing, Review, Done; the default), sprint, bugs or content.",
            SentinelAgentJson.Schema(
                ("title", "string", "The board's name", true),
                ("template", "string", "personal, sprint, bugs or content", false)),
            SentinelAgentRisk.Change, AdminRoles,
            args => $"Creating board '{args.OptionalText("title")}'",
            CreateBoardAsync,
            UndoCreateBoardAsync),
        new SentinelAgentTool(
            "add_card",
            "Add a card to a board, in a column given by its name (or the first column when left out).",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("title", "string", "The card's title", true),
                ("column", "string", "The column's name, e.g. To do", false)),
            SentinelAgentRisk.Change, BoardRoles,
            args => $"Adding card '{args.OptionalText("title")}'",
            AddCardAsync,
            UndoAddCardAsync),
        new SentinelAgentTool(
            "add_ticket_card",
            "Add a card for a support ticket to a board, linked to the ticket (titled from it). Use for 'add this ticket to the board' when a ticket is open.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("ticketId", "string", "The support ticket's id", true),
                ("column", "string", "The column's name (the first column when left out)", false)),
            SentinelAgentRisk.Change, BoardRoles,
            _ => "Adding the ticket to a board",
            AddTicketCardAsync,
            UndoAddCardAsync),
        new SentinelAgentTool(
            "move_card",
            "Move a card to another column of its board.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The card id", true),
                ("column", "string", "The column's name to move it to", true)),
            SentinelAgentRisk.Change, BoardRoles,
            args => $"Moving a card to '{args.OptionalText("column")}'",
            MoveCardAsync,
            UndoMoveCardAsync),
        new SentinelAgentTool(
            "rename_card",
            "Change a card's title.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The card id", true),
                ("title", "string", "The new title", true)),
            SentinelAgentRisk.Change, BoardRoles,
            args => $"Renaming a card to '{args.OptionalText("title")}'",
            RenameCardAsync,
            UndoRenameCardAsync),
        new SentinelAgentTool(
            "delete_card",
            "Move a card to the trash. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The card id", true)),
            SentinelAgentRisk.Confirm, BoardRoles,
            _ => "Proposing to delete a card",
            DeleteCardAsync,
            UndoDeleteCardAsync,
            async (args, context, ct) =>
            {
                await RequireEditAsync(args.Id("boardId"), context.User, ct);
                var (board, card, _) = await CardAsync(args.Id("boardId"), args.Id("cardId"), ct);
                return $"Delete the card '{card.Title}' from the board '{board.Title}'";
            }),
        new SentinelAgentTool(
            "find_cards",
            "Find cards anywhere on one board by title or card key. Returns every match, including cards past the board preview limit.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("query", "string", "Part of the card title or its key", true)),
            SentinelAgentRisk.Read, BoardRoles,
            args => $"Finding cards matching '{args.OptionalText("query")}'",
            FindCardsAsync),
        new SentinelAgentTool(
            "read_card",
            "Read one existing card completely: column, standard fields, custom properties, description, sub-items and allowed field options.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The card id", true)),
            SentinelAgentRisk.Read, BoardRoles,
            _ => "Reading a card",
            ReadCardAsync),
        new SentinelAgentTool(
            "update_card",
            "Update one field on an existing card. Field may be title, description, column/status, priority, labels, due, assignee, estimate, sprint, epic, blocked, blocked reason, waiting on, or any editable custom property returned by read_card. Use an empty value to clear it. For multi-value fields use comma-separated option labels, usernames or card ids.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The card id", true),
                ("field", "string", "The field name", true),
                ("value", "string", "The new value; empty clears it", true)),
            SentinelAgentRisk.Change, BoardRoles,
            args => $"Updating {args.OptionalText("field")} on a card",
            UpdateCardAsync,
            UndoUpdateCardAsync),
        new SentinelAgentTool(
            "add_card_subitem",
            "Add a sub-item to an existing card.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The parent card id", true),
                ("title", "string", "The sub-item title", true)),
            SentinelAgentRisk.Change, BoardRoles,
            args => $"Adding sub-item '{args.OptionalText("title")}'",
            AddSubItemAsync,
            UndoAddCardAsync),
        new SentinelAgentTool(
            "set_card_subitem_done",
            "Mark a card sub-item done or not done.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("subitemId", "string", "The sub-item row id", true),
                ("done", "boolean", "True to complete it; false to reopen it", true)),
            SentinelAgentRisk.Change, BoardRoles,
            args => args.Flag("done", false) ? "Completing a sub-item" : "Reopening a sub-item",
            SetSubItemDoneAsync),
        new SentinelAgentTool(
            "archive_card",
            "Archive an existing card so it leaves the active board. The change can be undone.",
            SentinelAgentJson.Schema(("boardId", "string", "The board id", true), ("cardId", "string", "The card id", true)),
            SentinelAgentRisk.Change, BoardRoles,
            _ => "Archiving a card",
            ArchiveCardAsync,
            UndoArchiveCardAsync)
    ];

    public static string LinkTo(Guid boardId) => $"/admin/boards/{boardId}";

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var list = await boards.ListBoardsForAsync(context.User.Username, ct);
        return SentinelAgentToolResult.Of(new
        {
            boards = list.Select(board => new { id = board.DatabaseId, title = board.Title, openCards = board.OpenCount, cards = board.CardCount })
        });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        await RequireViewAsync(args.Id("boardId"), context.User, ct);
        var board = await BoardAsync(args.Id("boardId"), ct);
        return SentinelAgentToolResult.Of(new
        {
            id = board.DatabaseId,
            title = board.Title,
            columns = board.Columns.Select(column => new
            {
                column = column.Label,
                cards = column.Cards.Count,
                shown = column.Cards.Take(CardsPerColumn).Select(card => new { id = card.RowId, title = card.Title })
            })
        });
    }

    private async Task<SentinelAgentToolResult> CreateBoardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var template = args.OptionalText("template")?.ToLowerInvariant() ?? "personal";
        if (KanbanTemplates.Find(template) is null) template = "personal";
        var id = await boards.CreateBoardAsync(args.Text("title"), template, context.User.Username, ct);
        var board = await BoardAsync(id, ct);
        return SentinelAgentToolResult.Of(
            new { created = true, boardId = id, title = board.Title, columns = board.Columns.Select(column => column.Label) },
            new SentinelAgentChange($"Created the board '{board.Title}'", "board", id.ToString(), board.Title, LinkTo(id),
                SentinelAgentJson.Serialize(new BoardUndo(id))));
    }

    private Task UndoCreateBoardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        boards.DeleteBoardAsync(SentinelAgentJson.Deserialize<BoardUndo>(undoJson).BoardId, context.User.Username, ct);

    private async Task<SentinelAgentToolResult> AddCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var board = await BoardAsync(args.Id("boardId"), ct);
        var column = args.OptionalText("column") is { } name ? Column(board, name) : board.Columns.FirstOrDefault(c => c.OptionId is not null) ?? board.Columns[0];
        var title = args.Text("title");
        var rowId = await boards.AddCardAsync(board.DatabaseId, column.OptionId, title, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { added = true, cardId = rowId, title, column = column.Label, board = board.Title },
            new SentinelAgentChange($"Added '{title}' to {column.Label} on '{board.Title}'", "board-card", rowId.ToString(), title, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(new CardUndo(board.DatabaseId, rowId, null))));
    }

    private async Task<SentinelAgentToolResult> AddTicketCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var board = await BoardAsync(args.Id("boardId"), ct);
        var column = args.OptionalText("column") is { } name ? Column(board, name) : board.Columns.FirstOrDefault(c => c.OptionId is not null) ?? board.Columns[0];
        var ticketId = args.Id("ticketId");
        var subject = await db.SupportTickets.AsNoTracking().Where(ticket => ticket.Id == ticketId).Select(ticket => ticket.Subject).FirstOrDefaultAsync(ct)
            ?? throw new SentinelAgentToolException($"There's no support ticket with id {ticketId}.");
        var rowId = await boards.AddCardFromTicketAsync(board.DatabaseId, ticketId, column.OptionId, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { added = true, cardId = rowId, ticket = subject, column = column.Label, board = board.Title },
            new SentinelAgentChange($"Added the ticket '{subject}' to {column.Label} on '{board.Title}'", "board-card", rowId.ToString(), subject, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(new CardUndo(board.DatabaseId, rowId, null))));
    }

    private async Task UndoAddCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUndo>(undoJson);
        await RequireEditAsync(undo.BoardId, context.User, ct);
        await databases.TrashRowAsync(undo.BoardId, undo.CardId, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> MoveCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var (board, card, from) = await CardAsync(args.Id("boardId"), args.Id("cardId"), ct);
        var to = Column(board, args.Text("column"));
        await boards.MoveCardAsync(board.DatabaseId, card.RowId, to.OptionId, null, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { moved = card.Title, from = from.Label, to = to.Label },
            new SentinelAgentChange($"Moved '{card.Title}' from {from.Label} to {to.Label}", "board-card", card.RowId.ToString(), card.Title, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(new CardUndo(board.DatabaseId, card.RowId, from.OptionId))));
    }

    private Task UndoMoveCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUndo>(undoJson);
        return boards.MoveCardAsync(undo.BoardId, undo.CardId, undo.OptionId, null, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> RenameCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var (board, card, _) = await CardAsync(args.Id("boardId"), args.Id("cardId"), ct);
        var title = args.Text("title");
        var titleProperty = await TitlePropertyAsync(board.DatabaseId, ct);
        var before = await boards.SetCardsValueAsync(board.DatabaseId, [card.RowId], titleProperty, _ => JsonValue.Create(title), context.User.Username, ct);
        var saved = new JsonObject();
        foreach (var (rowId, value) in before) saved[rowId.ToString()] = value?.DeepClone();
        return SentinelAgentToolResult.Of(
            new { renamed = true, from = card.Title, to = title },
            new SentinelAgentChange($"Renamed the card '{card.Title}' to '{title}'", "board-card", card.RowId.ToString(), title, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(new CardRenameUndo(board.DatabaseId, titleProperty, saved.ToJsonString()))));
    }

    private Task UndoRenameCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardRenameUndo>(undoJson);
        var values = (JsonNode.Parse(undo.ValuesJson) as JsonObject ?? [])
            .ToDictionary(pair => Guid.Parse(pair.Key), pair => pair.Value?.DeepClone());
        return boards.RestoreCardValuesAsync(undo.BoardId, undo.PropertyId, values, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> DeleteCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        await RequireEditAsync(args.Id("boardId"), context.User, ct);
        var (board, card, _) = await CardAsync(args.Id("boardId"), args.Id("cardId"), ct);
        await databases.TrashRowAsync(board.DatabaseId, card.RowId, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { deleted = card.Title },
            new SentinelAgentChange($"Deleted the card '{card.Title}' from '{board.Title}'", "board-card", card.RowId.ToString(), card.Title, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(new CardUndo(board.DatabaseId, card.RowId, null))));
    }

    private async Task UndoDeleteCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUndo>(undoJson);
        await RequireEditAsync(undo.BoardId, context.User, ct);
        await databases.RestoreRowAsync(undo.BoardId, undo.CardId, context.User.Username, ct);
    }

    private async Task<SentinelAgentToolResult> FindCardsAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var boardId = args.Id("boardId");
        await RequireViewAsync(boardId, context.User, ct);
        var board = await BoardAsync(boardId, ct);
        var query = args.Text("query");
        var matches = board.Columns.SelectMany(column => column.Cards.Select(card => new { card, column }))
            .Where(item => item.card.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (item.card.Key?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(50)
            .Select(item => new { id = item.card.RowId, key = item.card.Key, title = item.card.Title, column = item.column.Label })
            .ToList();
        return SentinelAgentToolResult.Of(new { boardId, board = board.Title, matches });
    }

    private async Task<SentinelAgentToolResult> ReadCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var boardId = args.Id("boardId");
        var cardId = args.Id("cardId");
        await RequireViewAsync(boardId, context.User, ct);
        var (board, card, column) = await CardAsync(boardId, cardId, ct);
        var database = await DatabaseAsync(boardId, ct);
        var row = database.Rows.First(item => item.Id == cardId);
        var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
        var properties = database.Properties.OrderBy(property => property.SortOrder).Select(property => new
        {
            name = property.Name,
            type = property.Type,
            value = WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt, row.UpdatedAt, row.CreatedBy, row.UpdatedBy),
            options = property.Type is WikiDatabasePropertyTypes.Select or WikiDatabasePropertyTypes.MultiSelect or WikiDatabasePropertyTypes.Status
                ? WikiDatabasePropertyConfig.GetOptions(property).Select(option => new { id = option.Id, label = option.Label }).ToList()
                : null
        });
        var description = SentinelNoteCleanup.ToMarkdown(WikiBlockJson.ParseBlocks(row.BlocksJson));
        var subItems = await boards.ListSubItemsAsync(boardId, cardId, ct);
        return SentinelAgentToolResult.Of(new
        {
            boardId,
            cardId,
            key = card.Key,
            title = card.Title,
            column = column.Label,
            description,
            blocked = card.Blocked,
            blockedReason = card.BlockedReason,
            waitingOn = card.WaitingOn,
            properties,
            subItems
        });
    }

    private async Task<SentinelAgentToolResult> UpdateCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var boardId = args.Id("boardId");
        var cardId = args.Id("cardId");
        await RequireEditAsync(boardId, context.User, ct);
        var (board, card, column) = await CardAsync(boardId, cardId, ct);
        var field = args.Text("field");
        var value = args["value"] is null ? string.Empty : args["value"]!.GetValue<string>().Trim();

        if (Matches(field, "column", "status"))
        {
            var target = Column(board, value);
            await boards.MoveCardAsync(boardId, cardId, target.OptionId, null, context.User.Username, ct);
            return Changed(card, board, field, value, new CardUpdateUndo(boardId, cardId, "column", null, column.OptionId, target.OptionId, 0));
        }

        if (Matches(field, "blocked"))
        {
            if (!bool.TryParse(value, out var blocked)) throw new SentinelAgentToolException("Blocked must be true or false.");
            await boards.SetBlockedAsync(boardId, cardId, blocked, card.BlockedReason, context.User.Username, ct);
            return Changed(card, board, field, value, new CardUpdateUndo(boardId, cardId, "blocked", null,
                JsonSerializer.Serialize(new { card.Blocked, card.BlockedReason }),
                JsonSerializer.Serialize(new { Blocked = blocked, card.BlockedReason }), 0));
        }

        if (Matches(field, "blocked reason", "blockedreason"))
        {
            await boards.SetBlockedAsync(boardId, cardId, true, EmptyToNull(value), context.User.Username, ct);
            return Changed(card, board, field, value, new CardUpdateUndo(boardId, cardId, "blocked", null,
                JsonSerializer.Serialize(new { card.Blocked, card.BlockedReason }),
                JsonSerializer.Serialize(new { Blocked = true, BlockedReason = EmptyToNull(value) }), 0));
        }

        if (Matches(field, "waiting on", "waitingon", "dependencies"))
        {
            var ids = Split(value).Select(text => Guid.TryParse(text, out var id) ? id : throw new SentinelAgentToolException($"'{text}' isn't a card id. Find the dependency first.")).ToList();
            var dependencyBefore = card.WaitingOn?.ToList() ?? [];
            await boards.SetWaitingOnAsync(boardId, cardId, ids, context.User.Username, ct);
            return Changed(card, board, field, value, new CardUpdateUndo(boardId, cardId, "waiting", null,
                JsonSerializer.Serialize(dependencyBefore), JsonSerializer.Serialize(ids), 0));
        }

        var database = await DatabaseAsync(boardId, ct);
        var row = database.Rows.First(item => item.Id == cardId);
        if (Matches(field, "description", "body", "notes"))
        {
            var updated = await databases.SaveRowAsync(boardId, new WikiDatabaseRowEditor
            {
                Id = cardId,
                ParentRowId = row.ParentRowId,
                BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(value)),
                KeepPropertyValues = true,
                ExpectedVersion = row.ConcurrencyVersion
            }, context.User.Username, ct);
            return Changed(card, board, field, value, new CardUpdateUndo(boardId, cardId, "description", null, row.BlocksJson, null, updated.ConcurrencyVersion));
        }

        var property = ResolveProperty(database, field);
        var newValue = PropertyValue(property, value);
        var beforeValues = await boards.SetCardsValueAsync(boardId, [cardId], property.Id,
            _ => newValue?.DeepClone(), context.User.Username, ct);
        var before = beforeValues.GetValueOrDefault(cardId)?.ToJsonString() ?? "null";
        return Changed(card, board, property.Name, value, new CardUpdateUndo(boardId, cardId, "property", property.Id, before,
            newValue?.ToJsonString() ?? "null", 0));
    }

    private async Task UndoUpdateCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUpdateUndo>(undoJson);
        await RequireEditAsync(undo.BoardId, context.User, ct);
        switch (undo.Kind)
        {
            case "column":
                var (_, _, currentColumn) = await CardAsync(undo.BoardId, undo.CardId, ct);
                if (currentColumn.OptionId != undo.Secondary) throw ChangedSince();
                await boards.MoveCardAsync(undo.BoardId, undo.CardId, undo.Before, null, context.User.Username, ct);
                break;
            case "blocked":
                var (_, currentBlockedCard, _) = await CardAsync(undo.BoardId, undo.CardId, ct);
                var expectedBlocked = JsonSerializer.Deserialize<BlockedUndo>(undo.Secondary!)!;
                if (currentBlockedCard.Blocked != expectedBlocked.Blocked || currentBlockedCard.BlockedReason != expectedBlocked.BlockedReason) throw ChangedSince();
                var blocked = JsonSerializer.Deserialize<BlockedUndo>(undo.Before!)!;
                await boards.SetBlockedAsync(undo.BoardId, undo.CardId, blocked.Blocked, blocked.BlockedReason, context.User.Username, ct);
                break;
            case "waiting":
                var (_, currentWaitingCard, _) = await CardAsync(undo.BoardId, undo.CardId, ct);
                var expectedWaiting = JsonSerializer.Deserialize<List<Guid>>(undo.Secondary!) ?? [];
                if (!(currentWaitingCard.WaitingOn ?? []).Order().SequenceEqual(expectedWaiting.Order())) throw ChangedSince();
                await boards.SetWaitingOnAsync(undo.BoardId, undo.CardId, JsonSerializer.Deserialize<List<Guid>>(undo.Before!) ?? [], context.User.Username, ct);
                break;
            case "description":
                var database = await DatabaseAsync(undo.BoardId, ct);
                var row = database.Rows.First(item => item.Id == undo.CardId);
                await databases.SaveRowAsync(undo.BoardId, new WikiDatabaseRowEditor
                {
                    Id = undo.CardId,
                    ParentRowId = row.ParentRowId,
                    BlocksJson = undo.Before,
                    KeepPropertyValues = true,
                    ExpectedVersion = undo.ExpectedVersion
                }, context.User.Username, ct);
                break;
            default:
                var currentDatabase = await DatabaseAsync(undo.BoardId, ct);
                var currentRow = currentDatabase.Rows.First(item => item.Id == undo.CardId);
                var currentValue = WikiPropertyValues.ParseObject(currentRow.PropertyValuesJson)[undo.PropertyId!.Value.ToString()];
                var expectedValue = undo.Secondary == "null" ? null : JsonNode.Parse(undo.Secondary!);
                if (!JsonNode.DeepEquals(currentValue, expectedValue)) throw ChangedSince();
                var node = undo.Before == "null" ? null : JsonNode.Parse(undo.Before!);
                await boards.RestoreCardValuesAsync(undo.BoardId, undo.PropertyId.Value,
                    new Dictionary<Guid, JsonNode?> { [undo.CardId] = node }, context.User.Username, ct);
                break;
        }

        static InvalidOperationException ChangedSince() => new("That card changed after SentinelGPT edited it, so Undo did not overwrite the newer change.");
    }

    private async Task<SentinelAgentToolResult> AddSubItemAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var boardId = args.Id("boardId");
        var cardId = args.Id("cardId");
        await RequireEditAsync(boardId, context.User, ct);
        var (board, card, _) = await CardAsync(boardId, cardId, ct);
        var title = args.Text("title");
        var id = await boards.AddSubItemAsync(boardId, cardId, title, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { added = true, subitemId = id, title, parentCard = card.Title },
            new SentinelAgentChange($"Added sub-item '{title}' to '{card.Title}'", "board-card", id.ToString(), title, LinkTo(boardId),
                SentinelAgentJson.Serialize(new CardUndo(boardId, id, null))));
    }

    private async Task<SentinelAgentToolResult> SetSubItemDoneAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var boardId = args.Id("boardId");
        await RequireEditAsync(boardId, context.User, ct);
        var id = args.Id("subitemId");
        var done = args.Flag("done", false);
        await boards.SetSubItemDoneAsync(boardId, id, done, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { subitemId = id, done },
            new SentinelAgentChange($"Marked sub-item {(done ? "done" : "not done")}", "board-card", id.ToString(), "Sub-item", LinkTo(boardId), null));
    }

    private async Task<SentinelAgentToolResult> ArchiveCardAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var boardId = args.Id("boardId");
        var cardId = args.Id("cardId");
        await RequireEditAsync(boardId, context.User, ct);
        var (board, card, _) = await CardAsync(boardId, cardId, ct);
        await boards.ArchiveCardsAsync(boardId, [cardId], context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { archived = card.Title },
            new SentinelAgentChange($"Archived '{card.Title}'", "board-card", cardId.ToString(), card.Title, LinkTo(boardId),
                SentinelAgentJson.Serialize(new CardUndo(boardId, cardId, null))));
    }

    private Task UndoArchiveCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUndo>(undoJson);
        return boards.UnarchiveCardsAsync(undo.BoardId, [undo.CardId], context.User.Username, ct);
    }

    private async Task RequireViewAsync(Guid boardId, SentinelAgentUser user, CancellationToken ct)
    {
        var allowed = await boards.GetAccessAsync(boardId, user.Username, ct);
        if (!allowed.CanView) throw new SentinelAgentToolException("You don't have access to that board.");
    }

    private async Task RequireEditAsync(Guid boardId, SentinelAgentUser user, CancellationToken ct)
    {
        var allowed = await boards.GetAccessAsync(boardId, user.Username, ct);
        if (!allowed.CanEdit) throw new SentinelAgentToolException("That board is view-only for you; ask an admin for Edit access.");
    }

    private async Task<WikiDatabase> DatabaseAsync(Guid boardId, CancellationToken ct) =>
        await databases.GetDatabaseAsync(boardId, ct) ?? throw new SentinelAgentToolException("That board no longer exists.");

    private static WikiDatabaseProperty ResolveProperty(WikiDatabase database, string field)
    {
        var normalized = Normalize(field);
        var aliases = normalized switch
        {
            "assignee" or "assignees" or "owner" => new[] { "assignee", "assignees", "owner" },
            "due" or "duedate" or "deadline" => new[] { "due", "duedate", "deadline" },
            "label" or "labels" or "tag" or "tags" => new[] { "label", "labels", "tag", "tags" },
            "estimate" or "points" or "storypoints" => new[] { "estimate", "points", "storypoints" },
            _ => [normalized]
        };
        var matches = database.Properties.Where(property => aliases.Contains(Normalize(property.Name), StringComparer.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new SentinelAgentToolException($"'{field}' isn't an editable field on this board. Read the card for its field names.");
        if (matches.Count > 1) throw new SentinelAgentToolException($"'{field}' matches more than one field. Use the exact field name from read_card.");
        var property = matches[0];
        if (property.Type is WikiDatabasePropertyTypes.Formula or WikiDatabasePropertyTypes.Rollup or WikiDatabasePropertyTypes.CreatedTime
            or WikiDatabasePropertyTypes.LastEditedTime or WikiDatabasePropertyTypes.LastEditedBy or WikiDatabasePropertyTypes.CreatedBy
            or WikiDatabasePropertyTypes.UniqueId or WikiDatabasePropertyTypes.Button or WikiDatabasePropertyTypes.AiField
            or WikiDatabasePropertyTypes.Files or WikiDatabasePropertyTypes.Place or WikiDatabasePropertyTypes.Relation
            or WikiDatabasePropertyTypes.Verification)
            throw new SentinelAgentToolException($"'{property.Name}' is read-only or needs its dedicated application workflow.");
        return property;
    }

    private static JsonNode? PropertyValue(WikiDatabaseProperty property, string value)
    {
        if (value.Length == 0) return null;
        switch (property.Type)
        {
            case WikiDatabasePropertyTypes.Title:
            case WikiDatabasePropertyTypes.Text:
            case WikiDatabasePropertyTypes.Url:
            case WikiDatabasePropertyTypes.Email:
            case WikiDatabasePropertyTypes.Phone:
                return JsonValue.Create(value);
            case WikiDatabasePropertyTypes.Number:
                return decimal.TryParse(value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw new SentinelAgentToolException($"'{property.Name}' needs a number.");
            case WikiDatabasePropertyTypes.Checkbox:
                return bool.TryParse(value, out var flag) ? JsonValue.Create(flag)
                    : throw new SentinelAgentToolException($"'{property.Name}' needs true or false.");
            case WikiDatabasePropertyTypes.Date:
                var parsed = ParseDate(value, property.Name);
                return JsonValue.Create(parsed.ToString("O"));
            case WikiDatabasePropertyTypes.Person:
                return new JsonArray(Split(value).Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());
            case WikiDatabasePropertyTypes.Select:
            case WikiDatabasePropertyTypes.Status:
                return JsonValue.Create(ResolveOption(property, value));
            case WikiDatabasePropertyTypes.MultiSelect:
                return new JsonArray(Split(value).Select(item => (JsonNode?)JsonValue.Create(ResolveOption(property, item))).ToArray());
            default:
                throw new SentinelAgentToolException($"'{property.Name}' cannot be changed through update_card.");
        }
    }

    private static string ResolveOption(WikiDatabaseProperty property, string value)
    {
        var options = WikiDatabasePropertyConfig.GetOptions(property);
        return options.FirstOrDefault(option => string.Equals(option.Id, value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(option.Label, value, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new SentinelAgentToolException($"'{value}' isn't an option for {property.Name}. Choose: {string.Join(", ", options.Select(option => option.Label))}.");
    }

    private static DateTimeOffset ParseDate(string value, string field) =>
        DateTime.TryParseExact(value, ["yyyy-MM-dd", "dd-MM-yy", "dd-MM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date.Date, TimeSpan.Zero)
            : throw new SentinelAgentToolException($"'{field}' needs a date like 2026-11-04.");

    private static IReadOnlyList<string> Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool Matches(string value, params string[] choices) => choices.Any(choice => Normalize(value) == Normalize(choice));
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static SentinelAgentToolResult Changed(KanbanCard card, KanbanBoard board, string field, string value, CardUpdateUndo undo) =>
        SentinelAgentToolResult.Of(new { updated = true, cardId = card.RowId, field, value },
            new SentinelAgentChange($"Updated {field} on '{card.Title}'", "board-card", card.RowId.ToString(), card.Title, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(undo)));

    private async Task<KanbanBoard> BoardAsync(Guid boardId, CancellationToken ct) =>
        await boards.GetBoardAsync(boardId, ct)
            ?? throw new SentinelAgentToolException($"There's no board with id {boardId}. List the boards first.");

    private async Task<(KanbanBoard Board, KanbanCard Card, KanbanColumn Column)> CardAsync(Guid boardId, Guid cardId, CancellationToken ct)
    {
        var board = await BoardAsync(boardId, ct);
        foreach (var column in board.Columns)
        {
            if (column.Cards.FirstOrDefault(card => card.RowId == cardId) is { } card) return (board, card, column);
        }

        throw new SentinelAgentToolException($"There's no card {cardId} on '{board.Title}'. Read the board to get card ids.");
    }

    private static KanbanColumn Column(KanbanBoard board, string name) =>
        board.Columns.FirstOrDefault(column => string.Equals(column.Label, name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? board.Columns.FirstOrDefault(column => column.OptionId == name.Trim())
        ?? board.Columns.FirstOrDefault(column => column.Label.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new SentinelAgentToolException($"'{board.Title}' has no column called '{name}'. Its columns are: {string.Join(", ", board.Columns.Select(column => column.Label))}.");

    private async Task<Guid> TitlePropertyAsync(Guid boardId, CancellationToken ct) =>
        await db.WikiDatabaseProperties.AsNoTracking()
            .Where(property => property.WikiDatabaseId == boardId && property.Type == WikiDatabasePropertyTypes.Title)
            .Select(property => property.Id)
            .FirstAsync(ct);

    private sealed record BoardUndo(Guid BoardId);

    private sealed record CardUndo(Guid BoardId, Guid CardId, string? OptionId);

    private sealed record CardRenameUndo(Guid BoardId, Guid PropertyId, string ValuesJson);
    private sealed record CardUpdateUndo(Guid BoardId, Guid CardId, string Kind, Guid? PropertyId, string? Before, string? Secondary, long ExpectedVersion);
    private sealed record BlockedUndo(bool Blocked, string? BlockedReason);
}
