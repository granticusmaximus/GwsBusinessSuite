using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Comments;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Blog comment moderation for the agent (05-11-26, the role coverage pass), through ICommentService
// like the Comments page (ContentAccess). Approving, marking spam and sending back to pending are
// routine, and Undo puts the comment's previous status back. Deleting is permanent, so it waits
// for a Confirm.
public sealed class CommentAgentTools(ICommentService comments) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin, AppRoles.Author, AppRoles.Contributor];
    private const int Shown = 25;
    private const int BodyCharacters = 400;

    public SentinelAgentArea Area { get; } = new("comments", "blog comments: the moderation queue, approving, spam and deleting", ["comment", "moderat", "spam"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "list_comments",
            "List blog comments for moderation, newest first, optionally only one status (Pending, Approved, Spam). Gives ids, the post, who wrote it and the text.",
            SentinelAgentJson.Schema(("status", "string", "Only comments with this status", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking at the comments",
            ListAsync),
        new SentinelAgentTool(
            "set_comment_status",
            "Approve a comment (it shows on the post), mark it Spam, or send it back to Pending.",
            SentinelAgentJson.Schema(
                ("commentId", "string", "The comment id", true),
                ("status", "string", "Approved, Spam or Pending", true)),
            SentinelAgentRisk.Change, Roles,
            args => $"Marking a comment {args.OptionalText("status") ?? "changed"}",
            SetStatusAsync,
            UndoSetStatusAsync),
        new SentinelAgentTool(
            "delete_comment",
            "Delete a comment for good. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(("commentId", "string", "The comment id", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to delete a comment",
            DeleteAsync,
            preview: async (args, _, ct) =>
            {
                var comment = await CommentAsync(args.Id("commentId"), ct);
                return $"Delete {comment.AuthorName}'s comment on '{comment.ArticleTitle}' for good: \"{Shorten(comment.Body, 120)}\"";
            })
    ];

    private sealed record StatusUndo(Guid CommentId, string Status);

    private async Task<SentinelAgentToolResult> ListAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var status = Status(args.OptionalText("status"), allowNone: true);
        var found = (await comments.ListForModerationAsync(status, ct)).OrderByDescending(comment => comment.CreatedAt).ToList();
        return SentinelAgentToolResult.Of(new
        {
            total = found.Count,
            comments = found.Take(Shown).Select(comment => new
            {
                id = comment.Id,
                post = comment.ArticleTitle,
                author = comment.AuthorName,
                status = comment.Status,
                written = comment.CreatedAt.ToLocalTime().ToString("dd-MM-yy HH:mm"),
                text = Shorten(comment.Body, BodyCharacters)
            })
        });
    }

    private async Task<SentinelAgentToolResult> SetStatusAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var comment = await CommentAsync(args.Id("commentId"), ct);
        var status = Status(args.Text("status"), allowNone: false)!;
        if (comment.Status == status) return SentinelAgentToolResult.Of(new { unchanged = $"It's already {status}." });
        await ApplyAsync(comment.Id, status, ct);
        return SentinelAgentToolResult.Of(new { comment = comment.Id, status },
            new SentinelAgentChange($"Marked {comment.AuthorName}'s comment on '{comment.ArticleTitle}' {status}", "comment", comment.Id.ToString(),
                comment.ArticleTitle, "/admin/comments", SentinelAgentJson.Serialize(new StatusUndo(comment.Id, comment.Status))));
    }

    private async Task UndoSetStatusAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<StatusUndo>(undoJson);
        await CommentAsync(undo.CommentId, ct);
        await ApplyAsync(undo.CommentId, undo.Status, ct);
    }

    private async Task<SentinelAgentToolResult> DeleteAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var comment = await CommentAsync(args.Id("commentId"), ct);
        await comments.DeleteAsync(comment.Id, ct);
        return SentinelAgentToolResult.Of(new { deleted = comment.Id },
            new SentinelAgentChange($"Deleted {comment.AuthorName}'s comment on '{comment.ArticleTitle}'", "comment", comment.Id.ToString(),
                comment.ArticleTitle, "/admin/comments", null));
    }

    private Task ApplyAsync(Guid commentId, string status, CancellationToken ct) => status switch
    {
        CommentStatuses.Approved => comments.ApproveAsync(commentId, ct),
        CommentStatuses.Spam => comments.MarkSpamAsync(commentId, ct),
        _ => comments.MarkPendingAsync(commentId, ct)
    };

    private async Task<CommentView> CommentAsync(Guid commentId, CancellationToken ct) =>
        (await comments.ListForModerationAsync(null, ct)).FirstOrDefault(comment => comment.Id == commentId)
            ?? throw new SentinelAgentToolException($"There's no comment with id {commentId}. List the comments first.");

    private static string? Status(string? text, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(text)) return allowNone ? null : throw new SentinelAgentToolException("Say which status: Approved, Spam or Pending.");
        return new[] { CommentStatuses.Pending, CommentStatuses.Approved, CommentStatuses.Spam }
                   .FirstOrDefault(status => status.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)
                       || (text.Trim().Equals("approve", StringComparison.OrdinalIgnoreCase) && status == CommentStatuses.Approved))
               ?? throw new SentinelAgentToolException($"'{text}' isn't a comment status. Use Approved, Spam or Pending.");
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
