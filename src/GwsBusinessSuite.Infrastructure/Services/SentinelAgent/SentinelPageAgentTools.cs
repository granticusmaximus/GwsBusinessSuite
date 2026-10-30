using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Sentinel pages and to-do lists for the agent. A to-do list is a Sentinel page of to-do blocks,
// the same as one made by hand. Everything goes through IWikiService, so permissions, revisions
// and the concurrency check still apply. Same roles as the Sentinel page (ContentAccess).
public sealed class SentinelPageAgentTools(IWikiService wiki) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin, AppRoles.Author, AppRoles.Contributor];
    private const int MaxMarkdown = 6_000;

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "find_sentinel_pages",
            "Search Sentinel (the wiki: notes, docs and to-do lists) by words in the title or text. Returns up to 8 pages with id, title and open/done to-do counts.",
            SentinelAgentJson.Schema(("query", "string", "Words to look for", true)),
            SentinelAgentRisk.Read, Roles,
            args => $"Searching Sentinel for \"{args.OptionalText("query")}\"",
            FindAsync),
        new SentinelAgentTool(
            "read_sentinel_page",
            "Read one Sentinel page: its title, its content as markdown, and its to-do items with whether each is done.",
            SentinelAgentJson.Schema(("pageId", "string", "The page id", true)),
            SentinelAgentRisk.Read, Roles,
            _ => "Reading a Sentinel page",
            ReadAsync),
        new SentinelAgentTool(
            "create_todo_list",
            "Create a new to-do list (a Sentinel page of checkboxes) with a title and its first items.",
            SentinelAgentJson.Schema(
                ("title", "string", "The list's title", true),
                ("items", "string[]", "The to-do items, one per entry", false),
                ("parentPageId", "string", "Optional: a Sentinel page to put it under", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Creating to-do list '{args.OptionalText("title")}'",
            (args, context, ct) => CreateAsync(args.Text("title"), ToDoBlocks(args.Texts("items")), args.OptionalId("parentPageId"), "📝", context, ct,
                (page, count) => $"Created to-do list '{page.Title}'" + (count > 0 ? $" with {Count(count, "item")}" : string.Empty)),
            UndoCreateAsync),
        new SentinelAgentTool(
            "create_sentinel_page",
            "Create a new Sentinel page (a note or document) from a title and markdown content. Markdown supports headings, lists, '- [ ]' checkboxes, tables and code.",
            SentinelAgentJson.Schema(
                ("title", "string", "The page title", true),
                ("content", "string", "The page content as markdown", false),
                ("parentPageId", "string", "Optional: a Sentinel page to put it under", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Creating Sentinel page '{args.OptionalText("title")}'",
            (args, context, ct) => CreateAsync(args.Text("title"), WikiBlockJson.FromMarkdown(args.OptionalText("content") ?? string.Empty),
                args.OptionalId("parentPageId"), null, context, ct, (page, _) => $"Created Sentinel page '{page.Title}'"),
            UndoCreateAsync),
        new SentinelAgentTool(
            "add_todo_items",
            "Add to-do items to the end of an existing to-do list or Sentinel page.",
            SentinelAgentJson.Schema(
                ("pageId", "string", "The list's page id", true),
                ("items", "string[]", "The items to add", true)),
            SentinelAgentRisk.Change, Roles,
            args => $"Adding {Count(args.Texts("items").Count, "item")} to a list",
            AddToDosAsync,
            UndoEditAsync),
        new SentinelAgentTool(
            "add_to_sentinel_page",
            "Add markdown content to the end of an existing Sentinel page.",
            SentinelAgentJson.Schema(
                ("pageId", "string", "The page id", true),
                ("content", "string", "The markdown to add", true)),
            SentinelAgentRisk.Change, Roles,
            _ => "Adding to a Sentinel page",
            AppendAsync,
            UndoEditAsync),
        new SentinelAgentTool(
            "set_todo_done",
            "Tick (done=true) or untick (done=false) one to-do item on a list, found by its text.",
            SentinelAgentJson.Schema(
                ("pageId", "string", "The list's page id", true),
                ("item", "string", "The item's text, or enough of it to tell it apart", true),
                ("done", "boolean", "true to tick it, false to untick it", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"{(args.Flag("done", true) ? "Ticking" : "Unticking")} '{args.OptionalText("item")}'",
            SetDoneAsync,
            UndoEditAsync),
        new SentinelAgentTool(
            "rename_sentinel_page",
            "Rename a Sentinel page or to-do list.",
            SentinelAgentJson.Schema(
                ("pageId", "string", "The page id", true),
                ("title", "string", "The new title", true)),
            SentinelAgentRisk.Change, Roles,
            args => $"Renaming a page to '{args.OptionalText("title")}'",
            RenameAsync,
            UndoRenameAsync),
        new SentinelAgentTool(
            "trash_sentinel_page",
            "Move a Sentinel page or to-do list (and the pages under it) to the trash. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(("pageId", "string", "The page id", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to trash a page",
            TrashAsync,
            UndoTrashAsync,
            async (args, _, ct) => $"Move the Sentinel page '{(await PageAsync(args.Id("pageId"), ct)).Title}' to the trash")
    ];

    public static string LinkTo(Guid pageId) => $"/admin/sentinel?page={pageId}";

    private async Task<SentinelAgentToolResult> FindAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var words = args.Text("query").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pages = await wiki.ListPagesAsync(cancellationToken: ct);
        var matches = pages
            .Select(page => (Page: page, Score: Score(page, words)))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Page.UpdatedAt ?? match.Page.CreatedAt)
            .Take(8)
            .Select(match =>
            {
                var todos = WikiBlockJson.ParseBlocks(match.Page.BlocksJson).Where(block => block.Type == WikiBlockTypes.ToDo).ToList();
                return new
                {
                    id = match.Page.Id,
                    title = match.Page.Title,
                    openToDos = todos.Count(block => !IsDone(block)),
                    doneToDos = todos.Count(IsDone)
                };
            })
            .ToList();
        var note = matches.Count == 0 ? "Nothing matched. Try fewer or other words." : null;
        return SentinelAgentToolResult.Of(new { pages = matches, note });
    }

    private async Task<SentinelAgentToolResult> ReadAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var page = await PageAsync(args.Id("pageId"), ct);
        var blocks = WikiBlockJson.ParseBlocks(page.BlocksJson);
        var markdown = WikiMarkdownExporter.ExportPage(page.Title, blocks).Trim();
        return SentinelAgentToolResult.Of(new
        {
            id = page.Id,
            title = page.Title,
            content = markdown.Length <= MaxMarkdown ? markdown : markdown[..MaxMarkdown] + "\n…(cut short)",
            todos = blocks.Where(block => block.Type == WikiBlockTypes.ToDo).Select(block => new { item = block.PlainText, done = IsDone(block) })
        });
    }

    private async Task<SentinelAgentToolResult> CreateAsync(
        string title, IReadOnlyList<WikiBlock> blocks, Guid? parentPageId, string? icon, SentinelAgentToolContext context, CancellationToken ct,
        Func<WikiPage, int, string> summary)
    {
        if (parentPageId is { } parent) await PageAsync(parent, ct);
        var page = await wiki.SavePageAsync(new WikiPageEditorModel
        {
            Title = title,
            Icon = icon,
            ParentWikiPageId = parentPageId,
            BlocksJson = WikiBlockJson.Serialize(blocks)
        }, context.User.Username, cancellationToken: ct);
        var count = blocks.Count(block => block.Type == WikiBlockTypes.ToDo);
        return SentinelAgentToolResult.Of(
            new { created = true, pageId = page.Id, title = page.Title, todoItems = count },
            new SentinelAgentChange(summary(page, count), "sentinel-page", page.Id.ToString(), page.Title, LinkTo(page.Id),
                SentinelAgentJson.Serialize(new PageUndo(page.Id))));
    }

    private Task UndoCreateAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        wiki.TrashPageAsync(SentinelAgentJson.Deserialize<PageUndo>(undoJson).PageId, context.User.Username, ct);

    private async Task<SentinelAgentToolResult> AddToDosAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var items = args.Texts("items");
        if (items.Count == 0) throw new SentinelAgentToolException("'items' needs at least one item.");
        return await EditAsync(args.Id("pageId"), blocks => [.. blocks, .. ToDoBlocks(items)], context, ct,
            page => $"Added {Count(items.Count, "item")} to '{page.Title}'",
            page => new { added = items, list = page.Title });
    }

    private Task<SentinelAgentToolResult> AppendAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var added = WikiBlockJson.FromMarkdown(args.Text("content"));
        return EditAsync(args.Id("pageId"), blocks => [.. blocks, .. added], context, ct,
            page => $"Added to the Sentinel page '{page.Title}'",
            page => new { added = added.Count + " blocks", page = page.Title });
    }

    private Task<SentinelAgentToolResult> SetDoneAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var wanted = args.Text("item");
        var done = args.Flag("done", true);
        string? matched = null;
        return EditAsync(args.Id("pageId"), blocks =>
        {
            var todos = blocks.Where(block => block.Type == WikiBlockTypes.ToDo).ToList();
            var target = todos.FirstOrDefault(block => string.Equals(block.PlainText.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                var close = todos.Where(block => block.PlainText.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
                if (close.Count > 1)
                    throw new SentinelAgentToolException($"'{wanted}' matches {close.Count} items: {string.Join("; ", close.Select(block => block.PlainText))}. Use more of the text.");
                target = close.SingleOrDefault()
                    ?? throw new SentinelAgentToolException($"No item matches '{wanted}'. The items are: {string.Join("; ", todos.Select(block => block.PlainText))}.");
            }

            matched = target.PlainText;
            var props = new Dictionary<string, string>(target.Props) { ["checked"] = done ? "true" : "false" };
            return blocks.Select(block => block.Id == target.Id ? block with { Props = props } : block).ToList();
        }, context, ct,
            page => $"{(done ? "Ticked" : "Unticked")} '{matched}' on '{page.Title}'",
            page => new { item = matched, done, list = page.Title });
    }

    // One edit to a page's blocks, undone by putting the old blocks back while nobody has changed
    // the page since.
    private async Task<SentinelAgentToolResult> EditAsync(
        Guid pageId, Func<IReadOnlyList<WikiBlock>, IReadOnlyList<WikiBlock>> change, SentinelAgentToolContext context, CancellationToken ct,
        Func<WikiPage, string> summary, Func<WikiPage, object> forModel)
    {
        var page = await PageAsync(pageId, ct);
        var before = page.BlocksJson;
        var blocks = change(WikiBlockJson.ParseBlocks(before));
        var saved = await SaveAsync(page, page.Title, WikiBlockJson.Serialize(blocks), context, ct);
        return SentinelAgentToolResult.Of(forModel(saved),
            new SentinelAgentChange(summary(saved), "sentinel-page", saved.Id.ToString(), saved.Title, LinkTo(saved.Id),
                SentinelAgentJson.Serialize(new PageEditUndo(saved.Id, before, saved.Title, saved.ContentVersion))));
    }

    private async Task UndoEditAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<PageEditUndo>(undoJson);
        var page = await PageAsync(undo.PageId, ct);
        if (page.ContentVersion != undo.VersionAfter)
            throw new SentinelAgentToolException($"'{page.Title}' has changed since, so undoing would lose that. Use the page's history to go back.");
        await SaveAsync(page, undo.Title, undo.BlocksJson, context, ct);
    }

    private async Task<SentinelAgentToolResult> RenameAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var page = await PageAsync(args.Id("pageId"), ct);
        var before = page.Title;
        var saved = await SaveAsync(page, args.Text("title"), page.BlocksJson, context, ct);
        return SentinelAgentToolResult.Of(new { renamed = true, from = before, to = saved.Title },
            new SentinelAgentChange($"Renamed '{before}' to '{saved.Title}'", "sentinel-page", saved.Id.ToString(), saved.Title, LinkTo(saved.Id),
                SentinelAgentJson.Serialize(new PageEditUndo(saved.Id, page.BlocksJson, before, saved.ContentVersion))));
    }

    private async Task UndoRenameAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<PageEditUndo>(undoJson);
        var page = await PageAsync(undo.PageId, ct);
        await SaveAsync(page, undo.Title, page.BlocksJson, context, ct);
    }

    private async Task<SentinelAgentToolResult> TrashAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var page = await PageAsync(args.Id("pageId"), ct);
        await wiki.TrashPageAsync(page.Id, context.User.Username, ct);
        return SentinelAgentToolResult.Of(new { trashed = page.Title },
            new SentinelAgentChange($"Moved '{page.Title}' to the trash", "sentinel-page", page.Id.ToString(), page.Title, null,
                SentinelAgentJson.Serialize(new PageUndo(page.Id))));
    }

    private Task UndoTrashAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        wiki.RestorePageAsync(SentinelAgentJson.Deserialize<PageUndo>(undoJson).PageId, context.User.Username, ct);

    private Task<WikiPage> SaveAsync(WikiPage page, string title, string blocksJson, SentinelAgentToolContext context, CancellationToken ct) =>
        wiki.SavePageAsync(new WikiPageEditorModel
        {
            WikiPageId = page.Id,
            Title = title,
            Slug = page.Slug,
            Icon = page.Icon,
            CoverImageUrl = page.CoverImageUrl,
            ParentWikiPageId = page.ParentWikiPageId,
            IsFullWidth = page.IsFullWidth,
            FontStyle = page.FontStyle,
            ExpectedContentVersion = page.ContentVersion,
            BaseBlocksJson = page.BlocksJson,
            BlocksJson = blocksJson
        }, context.User.Username, cancellationToken: ct);

    private async Task<WikiPage> PageAsync(Guid pageId, CancellationToken ct) =>
        await wiki.GetPageAsync(pageId, ct) is { } page && page.TrashedAt is null
            ? page
            : throw new SentinelAgentToolException($"There's no Sentinel page with id {pageId}. Search for it first.");

    private static IReadOnlyList<WikiBlock> ToDoBlocks(IEnumerable<string> items) =>
        items.Select(item => new WikiBlock(Guid.NewGuid(), WikiBlockTypes.ToDo, 0, [new WikiRichTextSpan(item)],
            new Dictionary<string, string> { ["checked"] = "false" })).ToList();

    private static bool IsDone(WikiBlock block) => block.Props.GetValueOrDefault("checked") == "true";

    private static int Score(WikiPage page, string[] words)
    {
        var title = page.Title;
        var score = 0;
        foreach (var word in words)
        {
            if (title.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 3;
            else if (page.BlocksJson.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 1;
            else return 0;
        }

        return score;
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private sealed record PageUndo(Guid PageId);

    private sealed record PageEditUndo(Guid PageId, string BlocksJson, string Title, long VersionAfter);
}
