using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Boards for the agent, through IKanbanBoardService like the board page itself. Boards are an
// Admin page (Board.razor's AdminOnly), so these are Admin only too.
public sealed class BoardAgentTools(IKanbanBoardService boards, IWikiDatabaseService databases, IAppDbContext db) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin];
    private const int CardsPerColumn = 20;

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "list_boards",
            "List the Kanban boards with their ids and how many cards are open.",
            SentinelAgentJson.Schema(),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at the boards",
            ListAsync),
        new SentinelAgentTool(
            "read_board",
            "Read one board: its columns (with ids) and the cards in each (with ids).",
            SentinelAgentJson.Schema(("boardId", "string", "The board id", true)),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading a board",
            ReadAsync),
        new SentinelAgentTool(
            "create_board",
            "Create a new Kanban board. Template: personal (Backlog, To do, Doing, Review, Done; the default), sprint, bugs or content.",
            SentinelAgentJson.Schema(
                ("title", "string", "The board's name", true),
                ("template", "string", "personal, sprint, bugs or content", false)),
            SentinelAgentRisk.Change, Roles,
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
            SentinelAgentRisk.Change, Roles,
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
            SentinelAgentRisk.Change, Roles,
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
            SentinelAgentRisk.Change, Roles,
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
            SentinelAgentRisk.Change, Roles,
            args => $"Renaming a card to '{args.OptionalText("title")}'",
            RenameCardAsync,
            UndoRenameCardAsync),
        new SentinelAgentTool(
            "delete_card",
            "Move a card to the trash. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(
                ("boardId", "string", "The board id", true),
                ("cardId", "string", "The card id", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to delete a card",
            DeleteCardAsync,
            UndoDeleteCardAsync,
            async (args, _, ct) =>
            {
                var (board, card, _) = await CardAsync(args.Id("boardId"), args.Id("cardId"), ct);
                return $"Delete the card '{card.Title}' from the board '{board.Title}'";
            })
    ];

    public static string LinkTo(Guid boardId) => $"/admin/boards/{boardId}";

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var list = await boards.ListBoardsAsync(ct);
        return SentinelAgentToolResult.Of(new
        {
            boards = list.Select(board => new { id = board.DatabaseId, title = board.Title, openCards = board.OpenCount, cards = board.CardCount })
        });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
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

    private Task UndoAddCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUndo>(undoJson);
        return databases.TrashRowAsync(undo.BoardId, undo.CardId, context.User.Username, ct);
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
        var (board, card, _) = await CardAsync(args.Id("boardId"), args.Id("cardId"), ct);
        await databases.TrashRowAsync(board.DatabaseId, card.RowId, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { deleted = card.Title },
            new SentinelAgentChange($"Deleted the card '{card.Title}' from '{board.Title}'", "board-card", card.RowId.ToString(), card.Title, LinkTo(board.DatabaseId),
                SentinelAgentJson.Serialize(new CardUndo(board.DatabaseId, card.RowId, null))));
    }

    private Task UndoDeleteCardAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<CardUndo>(undoJson);
        return databases.RestoreRowAsync(undo.BoardId, undo.CardId, context.User.Username, ct);
    }

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
}
