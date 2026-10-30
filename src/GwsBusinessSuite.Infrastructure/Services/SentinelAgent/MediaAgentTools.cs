using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.SentinelAgent;
using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Infrastructure.Services.SentinelAgent;

// The media library for the agent (Phase D, 30-10-26), through IMediaLibraryService like the Media
// page (Admin/Contributor). It finds images to use (a post's hero image) and writes alt text;
// uploads and deletes stay on the page.
public sealed class MediaAgentTools(IMediaLibraryService media) : ISentinelAgentToolset
{
    private static readonly string[] Roles = [AppRoles.Admin, AppRoles.Contributor];
    private const int ImagesShown = 25;

    public SentinelAgentArea Area { get; } = new("media", "the media library's images and alt text", ["image", "media", "photo", "picture", "alt text", "hero"]);

    public IEnumerable<ISentinelAgentTool> Tools =>
    [
        new SentinelAgentTool(
            "find_media",
            "Find images in the media library by words in the file name or alt text (leave the search out for the newest). Gives ids, alt text and the image address.",
            SentinelAgentJson.Schema(("search", "string", "Words in the file name or alt text", false)),
            SentinelAgentRisk.Read, Roles,
            _ => "Looking in the media library",
            FindAsync),
        new SentinelAgentTool(
            "set_media_alt_text",
            "Set an image's alt text: a short description of what it shows, for screen readers and search.",
            SentinelAgentJson.Schema(
                ("mediaId", "string", "The image id", true),
                ("altText", "string", "What the image shows", true)),
            SentinelAgentRisk.Change, Roles,
            _ => "Writing alt text",
            SetAltTextAsync,
            UndoAltTextAsync)
    ];

    private async Task<SentinelAgentToolResult> FindAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var words = (args.OptionalText("search") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var images = (await media.ListAsync(ct))
            .Where(image => words.All(word =>
                image.FileName.Contains(word, StringComparison.OrdinalIgnoreCase) || image.AltText.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(image => image.CreatedAt)
            .ToList();
        return SentinelAgentToolResult.Of(new
        {
            found = images.Count,
            images = images.Take(ImagesShown).Select(image => new
            {
                id = image.Id,
                file = image.FileName,
                altText = image.AltText.Length == 0 ? "(none)" : image.AltText,
                url = image.Url,
                added = image.CreatedAt.ToString("yyyy-MM-dd")
            })
        });
    }

    private async Task<SentinelAgentToolResult> SetAltTextAsync(JsonObject args, SentinelAgentToolContext context, CancellationToken ct)
    {
        var image = await ImageAsync(args.Id("mediaId"), ct);
        var saved = await media.UpdateAltTextAsync(image.Id, args.Text("altText"), ct)
            ?? throw new SentinelAgentToolException($"'{image.FileName}' is no longer in the media library.");
        return SentinelAgentToolResult.Of(new { image = saved.FileName, altText = saved.AltText },
            new SentinelAgentChange($"Wrote alt text for '{saved.FileName}'", "media", saved.Id.ToString(), saved.FileName, "/admin/media",
                SentinelAgentJson.Serialize(new AltTextUndo(saved.Id, image.AltText, saved.AltText))));
    }

    private async Task UndoAltTextAsync(string undoJson, SentinelAgentToolContext context, CancellationToken ct)
    {
        var undo = SentinelAgentJson.Deserialize<AltTextUndo>(undoJson);
        var image = await ImageAsync(undo.MediaId, ct);
        if (image.AltText != undo.After)
            throw new SentinelAgentToolException($"The alt text for '{image.FileName}' has changed since, so it's left as it is.");
        await media.UpdateAltTextAsync(image.Id, undo.Before, ct);
    }

    private async Task<MediaAssetSummary> ImageAsync(Guid mediaId, CancellationToken ct) =>
        (await media.ListAsync(ct)).FirstOrDefault(image => image.Id == mediaId)
            ?? throw new SentinelAgentToolException($"There's no media library image with id {mediaId}. Find it with find_media.");

    private sealed record AltTextUndo(Guid MediaId, string Before, string After);
}
