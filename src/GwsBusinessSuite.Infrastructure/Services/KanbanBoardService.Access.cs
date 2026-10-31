using GwsBusinessSuite.Application.Boards;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GwsBusinessSuite.Infrastructure.Services;

// K9g, sharing a board with the team (31-10-26). A board is shared with a Sentinel View or Edit
// grant on its database (ISentinelAccessService, which also passes admins). Someone who isn't an
// admin changes a board only with Edit, and never does what reaches past the board: creating,
// deleting or publishing boards, its email address, links to contacts, tickets and invoices.
// The app's own actors (automation-engine, email-in, ...) aren't people and check their own
// rights, so they pass. Every change goes through these checks here, whichever page, panel, agent
// tool or API asked for it.
public sealed partial class KanbanBoardService
{
    public async Task<KanbanBoardAccess> GetAccessAsync(Guid databaseId, string username, CancellationToken cancellationToken = default)
    {
        if (access is null || !await IsTeamMemberAsync(username, cancellationToken)) return new KanbanBoardAccess(true, true, true);
        var canEdit = await access.CanAccessAsync(databaseId, true, username, SentinelAccessLevels.Edit, cancellationToken);
        var canView = canEdit || await access.CanAccessAsync(databaseId, true, username, SentinelAccessLevels.View, cancellationToken);
        return new KanbanBoardAccess(canView, canEdit, false);
    }

    public async Task<IReadOnlyList<KanbanBoardSummary>> ListBoardsForAsync(string username, CancellationToken cancellationToken = default)
    {
        var boards = await ListBoardsAsync(cancellationToken);
        if (access is null || !await IsTeamMemberAsync(username, cancellationToken)) return boards;
        var shared = new List<KanbanBoardSummary>();
        foreach (var board in boards)
            if (await access.CanAccessAsync(board.DatabaseId, true, username, SentinelAccessLevels.View, cancellationToken)) shared.Add(board);
        return shared;
    }

    private async Task RequireEditAsync(Guid databaseId, string performedBy, CancellationToken cancellationToken)
    {
        if (access is null || !await IsTeamMemberAsync(performedBy, cancellationToken)) return;
        if (!await access.CanAccessAsync(databaseId, true, performedBy, SentinelAccessLevels.Edit, cancellationToken))
            throw new InvalidOperationException("This board is shared with you to look at only - ask an admin for Edit access.");
    }

    private async Task RequireAdminAsync(string performedBy, CancellationToken cancellationToken)
    {
        if (access is not null && await IsTeamMemberAsync(performedBy, cancellationToken))
            throw new InvalidOperationException("Only an admin can do that.");
    }

    // A signed-in person who isn't an admin.
    private Task<bool> IsTeamMemberAsync(string username, CancellationToken cancellationToken) =>
        db.AppUsers.AnyAsync(user => user.Username == username && user.Role != AppRoles.Admin, cancellationToken);
}
