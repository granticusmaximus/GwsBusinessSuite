using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// Site pages for the agent: it drafts, the person publishes. New pages are Drafts, so nothing
// shows on the public site until a Confirm on publish_site_page. It only edits pages that are
// still Drafts; a live page's changes belong in the page editor, where "Publish changes"
// shows them first. Same roles as the page editor (ContributorAccess).
public sealed class SitePageAgentTools(ICmsBuilderService cms) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin, AppRoles.Contributor];
    private const string SectionsDescription =
        "The page's sections, in order. Each is an object: {\"heading\": \"...\", \"text\": \"markdown paragraphs\"}, " +
        "or {\"template\": \"feature-grid\"} for a ready-made section (feature-grid, testimonial-row, cta-banner, team-grid, pricing-table, newsletter-signup).";

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "find_site_pages",
            "Search the website's pages by title or address. Returns id, title, address and whether each is Draft or Published.",
            SentinelAgentJson.Schema(("query", "string", "Words in the title or address; empty lists the newest pages", false)),
            SentinelAgentRisk.Read, Roles,
            args => $"Searching site pages for \"{args.OptionalText("query")}\"",
            FindAsync),
        new SentinelAgentTool(
            "create_site_page_draft",
            "Create a new website page as a Draft (not public until published). Give a title and sections.",
            Schema(("title", "string", "The page title", true), ("slug", "string", "Optional address, e.g. services", false)),
            SentinelAgentRisk.Change, Roles,
            args => $"Drafting site page '{args.OptionalText("title")}'",
            CreateAsync,
            UndoCreateAsync),
        new SentinelAgentTool(
            "add_to_site_page_draft",
            "Add sections to the end of a website page that is still a Draft.",
            Schema(("pageId", "string", "The draft page's id", true)),
            SentinelAgentRisk.Change, Roles,
            _ => "Adding sections to a draft page",
            AppendAsync,
            UndoAppendAsync),
        new SentinelAgentTool(
            "publish_site_page",
            "Publish a Draft website page so the public can see it. Only proposes: the person confirms first.",
            SentinelAgentJson.Schema(("pageId", "string", "The draft page's id", true)),
            SentinelAgentRisk.Confirm, Roles,
            _ => "Proposing to publish a page",
            PublishAsync,
            UndoPublishAsync,
            async (args, _, ct) => $"Publish the site page '{(await DraftAsync(args.Id("pageId"), ct)).Title}' to the public site")
    ];

    public static string LinkTo(Guid pageId) => $"/admin/pages/{pageId}/edit";

    // The sections parameter is an array of objects, which SentinelAgentJson.Schema doesn't cover.
    private static string Schema(params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var schema = JsonNode.Parse(SentinelAgentJson.Schema(parameters))!.AsObject();
        schema["properties"]!["sections"] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = SectionsDescription,
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["heading"] = new JsonObject { ["type"] = "string" },
                    ["text"] = new JsonObject { ["type"] = "string" },
                    ["template"] = new JsonObject { ["type"] = "string" }
                }
            }
        };
        schema["required"]!.AsArray().Add("sections");
        return schema.ToJsonString();
    }

    private async Task<SentinelAgentToolResult> FindAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var words = (args.OptionalText("query") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pages = (await cms.ListPagesAsync(cancellationToken: ct))
            .Where(page => words.All(word => page.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || page.Slug.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(page => page.UpdatedAt ?? page.CreatedAt)
            .Take(10)
            .Select(page => new { id = page.Id, title = page.Title, address = "/" + page.Slug, status = page.Status })
            .ToList();
        return SentinelAgentToolResult.Of(new { pages });
    }

    private async Task<SentinelAgentToolResult> CreateAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var site = (await cms.ListSitesAsync(ct)).OrderBy(site => site.CreatedAt).FirstOrDefault()
            ?? throw new SentinelAgentToolException("There's no website set up yet.");
        var layout = new PageLayout { Sections = Sections(args) };
        var page = await cms.SavePageAsync(new CmsPageEditorModel
        {
            SiteId = site.Id,
            Title = args.Text("title"),
            Slug = args.OptionalText("slug")?.Trim('/') ?? string.Empty,
            BlocksJson = CmsBuilderJson.Serialize(layout),
            Status = CmsPageStatuses.Draft
        }, context.User.Username, ct);
        return SentinelAgentToolResult.Of(
            new { created = true, pageId = page.Id, title = page.Title, address = "/" + page.Slug, status = page.Status, sections = layout.Sections.Count },
            new SentinelAgentChange($"Drafted the site page '{page.Title}' ({Count(layout.Sections.Count)})", "site-page", page.Id.ToString(), page.Title,
                LinkTo(page.Id), SentinelAgentJson.Serialize(new PageUndo(page.Id))));
    }

    private Task UndoCreateAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct) =>
        cms.TrashPageAsync(SentinelAgentJson.Deserialize<PageUndo>(undoJson).PageId, ct);

    private async Task<SentinelAgentToolResult> AppendAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var page = await DraftAsync(args.Id("pageId"), ct);
        var before = page.BlocksJson;
        var layout = CmsBuilderJson.ParseLayoutOrEmpty(before);
        var added = Sections(args);
        layout.Sections.AddRange(added);
        var saved = await SaveAsync(page, CmsBuilderJson.Serialize(layout), CmsPageStatuses.Draft, null, context, ct);
        return SentinelAgentToolResult.Of(
            new { added = added.Count, page = saved.Title, sections = layout.Sections.Count },
            new SentinelAgentChange($"Added {Count(added.Count)} to the draft '{saved.Title}'", "site-page", saved.Id.ToString(), saved.Title, LinkTo(saved.Id),
                SentinelAgentJson.Serialize(new PageEditUndo(saved.Id, before, saved.ConcurrencyVersion))));
    }

    private async Task UndoAppendAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<PageEditUndo>(undoJson);
        var page = await DraftAsync(undo.PageId, ct);
        if (page.ConcurrencyVersion != undo.VersionAfter)
            throw new SentinelAgentToolException($"'{page.Title}' has changed since, so undoing would lose that. Change it in the page editor instead.");
        await SaveAsync(page, undo.BlocksJson, CmsPageStatuses.Draft, null, context, ct);
    }

    private async Task<SentinelAgentToolResult> PublishAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var page = await DraftAsync(args.Id("pageId"), ct);
        var saved = await SaveAsync(page, page.BlocksJson, CmsPageStatuses.Published, DateTimeOffset.UtcNow, context, ct);
        return SentinelAgentToolResult.Of(new { published = saved.Title, address = "/" + saved.Slug },
            new SentinelAgentChange($"Published the site page '{saved.Title}' at /{saved.Slug}", "site-page", saved.Id.ToString(), saved.Title, LinkTo(saved.Id),
                SentinelAgentJson.Serialize(new PageUndo(saved.Id))));
    }

    // Takes it back off the public site: a Draft again, content untouched.
    private async Task UndoPublishAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var page = await cms.GetPageAsync(SentinelAgentJson.Deserialize<PageUndo>(undoJson).PageId, ct)
            ?? throw new SentinelAgentToolException("That page no longer exists.");
        await SaveAsync(page, page.BlocksJson, CmsPageStatuses.Draft, null, context, ct);
    }

    // CmsPageEditorModel replaces every field on save, so each one the page has is carried over
    // (the 21-08-26 autosave bug wiped category, tags, canonical URL and properties this way).
    private async Task<CmsPage> SaveAsync(CmsPage page, string blocksJson, string status, DateTimeOffset? publishedAt, SentinelAgentToolContext context, CancellationToken ct)
    {
        var categoryName = string.Empty;
        if (page.CategoryId is { } categoryId)
            categoryName = (await cms.ListPageCategoriesAsync(page.SiteId, ct)).FirstOrDefault(category => category.Id == categoryId)?.Name ?? string.Empty;
        var properties = new Dictionary<Guid, string>();
        foreach (var pair in CmsPropertyValues.ParseObject(page.PropertyValuesJson))
        {
            if (Guid.TryParse(pair.Key, out var propertyId) && pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                properties[propertyId] = text;
        }

        return await cms.SavePageAsync(new CmsPageEditorModel
        {
            PageId = page.Id,
            SiteId = page.SiteId,
            ParentPageId = page.ParentPageId,
            Title = page.Title,
            Slug = page.Slug,
            BlocksJson = blocksJson,
            MetaTitle = page.MetaTitle,
            MetaDescription = page.MetaDescription,
            OgImageUrl = page.OgImageUrl,
            CustomCss = page.CustomCss,
            Status = status,
            PublishedAt = publishedAt ?? page.PublishedAt,
            EditPermission = page.EditPermission,
            CategoryName = categoryName,
            Tags = page.Tags,
            CanonicalUrl = page.CanonicalUrl,
            PropertyValues = properties,
            ExpectedVersion = page.ConcurrencyVersion
        }, context.User.Username, ct);
    }

    private async Task<CmsPage> DraftAsync(Guid pageId, CancellationToken ct)
    {
        var page = await cms.GetPageAsync(pageId, ct);
        if (page is null || page.TrashedAt is not null)
            throw new SentinelAgentToolException($"There's no site page with id {pageId}. Search for it first.");
        if (page.Status != CmsPageStatuses.Draft)
            throw new SentinelAgentToolException($"'{page.Title}' is already live. Changes to a live page are made in the page editor, which shows them before 'Publish changes'.");
        return page;
    }

    private static List<LayoutSection> Sections(JsonObject args)
    {
        if (args["sections"] is JsonValue text && text.TryGetValue(out string? raw))
        {
            try
            {
                args["sections"] = JsonNode.Parse(raw);
            }
            catch (System.Text.Json.JsonException)
            {
                args["sections"] = new JsonArray(new JsonObject { ["text"] = raw });
            }
        }

        var sections = new List<LayoutSection>();
        foreach (var item in args["sections"] as JsonArray ?? [])
        {
            if (item is not JsonObject section) continue;
            if (section.OptionalText("template") is { } key)
            {
                sections.Add(CmsSectionTemplates.Find(key)?.Build()
                    ?? throw new SentinelAgentToolException($"There's no section template '{key}'. Use one of: {string.Join(", ", CmsSectionTemplates.All.Select(template => template.Key))}."));
                continue;
            }

            var widgets = new List<LayoutWidget>();
            if (section.OptionalText("heading") is { } heading)
                widgets.Add(new LayoutWidget { WidgetType = "heading", Props = new Dictionary<string, string> { ["text"] = heading, ["level"] = sections.Count == 0 ? "h1" : "h2" } });
            if (section.OptionalText("text") is { } body)
                widgets.Add(new LayoutWidget { WidgetType = "paragraph", Props = new Dictionary<string, string> { ["text"] = body } });
            if (widgets.Count == 0) continue;
            sections.Add(new LayoutSection
            {
                Label = section.OptionalText("heading") ?? "Section",
                Columns = [new LayoutColumn { Span = 12, Widgets = widgets }]
            });
        }

        return sections.Count > 0 ? sections : throw new SentinelAgentToolException("'sections' needs at least one section with a heading or text.");
    }

    private static string Count(int sections) => sections == 1 ? "1 section" : $"{sections} sections";

    private sealed record PageUndo(Guid PageId);

    private sealed record PageEditUndo(Guid PageId, string BlocksJson, long VersionAfter);
}
