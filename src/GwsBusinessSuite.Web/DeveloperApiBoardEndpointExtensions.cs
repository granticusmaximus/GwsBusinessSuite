using System.Security.Claims;
using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Application.DeveloperApi;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Web.Security;

namespace GwsBusinessSuite.Web;

public sealed record DeveloperApiBoardView(Guid Id, string Title, string? Icon, int OpenCards);
public sealed record DeveloperApiCardRequest(Guid? BoardId, string? Text);
public sealed record DeveloperApiCardResult(Guid BoardId, string BoardTitle, Guid CardId, string Title, string Column, string Url);

// Capture to a board from anywhere (Boards K8d): the menu-bar companion adds a one-line card
// ("Fix login !high #bug fri") to a board with a key that has the boards:write scope - its only
// write. Cards land at the top of the board's first column, made by the key's owner.
public static class DeveloperApiBoardEndpointExtensions
{
    public static WebApplication MapDeveloperApiBoardEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/boards")
            .WithTags("Developer API")
            .RequireRateLimiting("developer-api")
            .RequireAuthorization(DeveloperApiPolicies.ForScope(DeveloperApiScopes.BoardsWrite));

        // The boards to choose from, most recently active first.
        api.MapGet("", async (IKanbanBoardService boards, CancellationToken ct) =>
            Results.Ok((await boards.ListBoardsAsync(ct)).Select(board => new DeveloperApiBoardView(board.DatabaseId, board.Title, board.Icon, board.OpenCount))));

        api.MapPost("/cards", async (DeveloperApiCardRequest request, ClaimsPrincipal user, IKanbanBoardService boards, CancellationToken ct) =>
        {
            var text = request.Text?.Trim() ?? string.Empty;
            if (text.Length is 0 or > 300) return Results.BadRequest(new { error = "Write the card in 1 to 300 characters." });
            var boardId = request.BoardId ?? (await boards.ListBoardsAsync(ct)).FirstOrDefault()?.DatabaseId;
            if (boardId is not { } id || await boards.GetBoardAsync(id, ct) is not { } board) return Results.NotFound(new { error = "That board isn't there." });
            var owner = user.FindFirstValue(DeveloperApiAuthenticationDefaults.OwnerUsernameClaim) ?? string.Empty;
            var card = KanbanQuickParse.Parse(text, board, DateOnly.FromDateTime(DateTime.Now), owner);
            if (card.Title.Length == 0) return Results.BadRequest(new { error = "The card needs a title as well as its tokens." });
            var column = board.Columns.FirstOrDefault(item => item.OptionId is not null && item.Group != WikiDatabaseStatusGroups.Complete) ?? board.Columns.First();
            var rowId = await boards.AddQuickCardAsync(id, column.OptionId, card, owner, ct);
            return Results.Ok(new DeveloperApiCardResult(id, board.Title, rowId, card.Title, column.Label, $"/admin/boards/{id}?card={rowId}"));
        });

        return app;
    }
}
