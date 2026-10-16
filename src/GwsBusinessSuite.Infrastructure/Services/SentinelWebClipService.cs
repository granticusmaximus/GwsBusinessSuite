using System.Security.Cryptography;
using System.Text;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using SmartReader;

namespace GwsBusinessSuite.Infrastructure.Services;

// Web clipper (Sentinel item 8). See SentinelWebClips for the rules a clip token is held to.
public sealed class SentinelWebClipService(
    IAppDbContext db,
    IWikiService wikiService,
    IDailyNoteService dailyNotes,
    ISentinelAccessService accessService,
    TimeProvider timeProvider) : ISentinelWebClipService
{
    // Same cap as a Mac quick note - one clip is one page.
    private const int MaxBodyLength = 200_000;

    public async Task<IReadOnlyList<SentinelClipTokenView>> ListTokensAsync(string username, CancellationToken cancellationToken = default)
    {
        var tokens = await db.SentinelClipTokens.AsNoTracking()
            .Where(token => token.Username == username && token.RevokedAtUnix == null)
            .ToListAsync(cancellationToken);
        // SQLite can't ORDER BY a DateTimeOffset - sort here.
        return tokens.OrderByDescending(token => token.CreatedAt).Select(ToView).ToList();
    }

    public async Task<SentinelClipTokenCreated> CreateTokenAsync(string username, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 80) throw new SentinelClipException("Give the token a name (up to 80 characters), e.g. \"Chrome on my Mac\".");
        var active = await db.SentinelClipTokens.CountAsync(token => token.Username == username && token.RevokedAtUnix == null, cancellationToken);
        if (active >= SentinelWebClips.MaxTokensPerUser) throw new SentinelClipException($"You already have {SentinelWebClips.MaxTokensPerUser} clip tokens - revoke one first.");

        var secret = SentinelWebClips.TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = new SentinelClipToken
        {
            Username = username,
            Name = trimmed,
            Prefix = secret[..(SentinelWebClips.TokenPrefix.Length + 6)],
            TokenHash = Hash(secret),
            CreatedBy = username
        };
        db.SentinelClipTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return new SentinelClipTokenCreated(ToView(token), secret);
    }

    public async Task RevokeTokenAsync(Guid tokenId, string username, CancellationToken cancellationToken = default)
    {
        var token = await db.SentinelClipTokens.FirstOrDefaultAsync(item => item.Id == tokenId && item.Username == username, cancellationToken)
            ?? throw new SentinelClipException("That token doesn't exist.");
        token.RevokedAtUnix ??= timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> ResolveTokenAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(SentinelWebClips.TokenPrefix, StringComparison.Ordinal) || token.Length > 100) return null;
        var hash = Hash(token.Trim());
        var row = await db.SentinelClipTokens.FirstOrDefaultAsync(item => item.TokenHash == hash && item.RevokedAtUnix == null, cancellationToken);
        if (row is null) return null;
        // The user's role is checked on every use, so disabling an account or dropping it to a
        // role without Sentinel access stops its tokens at once.
        var user = await db.AppUsers.AsNoTracking().FirstOrDefaultAsync(item => item.Username == row.Username, cancellationToken);
        if (user is not { IsActive: true } || !AppRoles.All.Contains(user.Role)) return null;
        row.LastUsedAtUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        await db.SaveChangesAsync(cancellationToken);
        return row.Username;
    }

    public async Task<SentinelClipResult> ClipAsync(string username, SentinelClipRequest request, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var source)
            || (source.Scheme != Uri.UriSchemeHttps && source.Scheme != Uri.UriSchemeHttp))
            throw new SentinelClipException("Only web pages (http or https) can be clipped.");
        if ((request.Html?.Length ?? 0) > SentinelWebClips.MaxHtmlLength) throw new SentinelClipException("That page is too large to clip - try clipping a selection instead.");
        if ((request.Note?.Length ?? 0) > SentinelWebClips.MaxNoteLength) throw new SentinelClipException($"The note is too long (up to {SentinelWebClips.MaxNoteLength} characters).");

        var mode = (request.Mode ?? string.Empty).Trim().ToLowerInvariant();
        var (title, body) = mode switch
        {
            SentinelWebClips.Modes.Article => Article(request, source),
            SentinelWebClips.Modes.Selection => (request.Title, WebClipMarkdownConverter.Convert(request.Html ?? string.Empty, source)),
            SentinelWebClips.Modes.Link => (request.Title, string.Empty),
            _ => throw new SentinelClipException("Choose what to clip: article, selection or link.")
        };
        if (mode == SentinelWebClips.Modes.Selection && body.Length == 0) throw new SentinelClipException("Nothing is selected on the page.");

        title = Truncate(string.IsNullOrWhiteSpace(title) ? source.Host : title.Trim(), SentinelWebClips.MaxTitleLength);
        var markdown = Compose(title, source, request, body);

        if (string.Equals(request.Target, SentinelWebClips.Targets.Today, StringComparison.OrdinalIgnoreCase))
        {
            var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
            var day = await dailyNotes.OpenOrCreateAsync(today, username, cancellationToken);
            await RequireEditAsync(day.Id, username, "today's daily note", cancellationToken);
            var updated = await dailyNotes.AppendAsync(today, title, markdown, username, cancellationToken);
            return new SentinelClipResult(updated.Id, updated.Title, SentinelWebClips.Targets.Today);
        }

        var folder = await EnsureFolderAsync(username, cancellationToken);
        var page = await wikiService.SavePageAsync(new WikiPageEditorModel
        {
            Title = title,
            Icon = mode == SentinelWebClips.Modes.Link ? "🔗" : "📎",
            ParentWikiPageId = folder.Id,
            BlocksJson = WikiBlockJson.Serialize(WikiBlockJson.FromMarkdown(markdown))
        }, username, cancellationToken: cancellationToken);
        return new SentinelClipResult(page.Id, page.Title, SentinelWebClips.Targets.NewPage);
    }

    // The readable part of the page (Readability via SmartReader, run on the HTML the extension
    // sent - the server never fetches the URL). A page Readability can't make sense of falls back
    // to its main content (WebClipMarkdownConverter.ConvertMainContent).
    private static (string? Title, string Body) Article(SentinelClipRequest request, Uri source)
    {
        var html = request.Html ?? string.Empty;
        if (html.Length == 0) throw new SentinelClipException("The page's content didn't arrive - reload the page and try again.");
        SmartReader.Article? article = null;
        try { article = new Reader(source.AbsoluteUri, html).GetArticle(); }
        catch (Exception) { /* fall back to the whole page below */ }
        var readable = article is { IsReadable: true } && !string.IsNullOrWhiteSpace(article.Content);
        var body = readable
            ? WebClipMarkdownConverter.Convert(article!.Content, source)
            : WebClipMarkdownConverter.ConvertMainContent(html, source);
        return (string.IsNullOrWhiteSpace(request.Title) ? article?.Title : request.Title, body);
    }

    private string Compose(string title, Uri source, SentinelClipRequest request, string body)
    {
        var markdown = new StringBuilder();
        markdown.Append("> Clipped from [").Append(title.Replace("[", "\\[").Replace("]", "\\]")).Append("](")
            .Append(WebClipMarkdownConverter.Absolute(source.AbsoluteUri, source, allowMailto: false))
            .Append(") on ").Append(timeProvider.GetLocalNow().ToGwsDateTime()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            foreach (var line in request.Note.Trim().Split('\n'))
                markdown.Append(line.TrimEnd()).Append('\n');
            markdown.Append('\n');
        }
        // Written into the text, so they become ordinary #tags (SentinelTags.FromBlocks).
        var tags = (request.Tags ?? []).Select(SentinelTags.Normalize).OfType<string>().Distinct().Take(10).ToList();
        if (tags.Count > 0) markdown.Append(string.Join(' ', tags.Select(tag => "#" + tag))).Append("\n\n");
        if (body.Length > 0)
        {
            markdown.Append("---\n\n");
            markdown.Append(body.Length > MaxBodyLength ? body[..MaxBodyLength] + "\n\n*(Clip shortened - the page was too long to keep in full.)*" : body);
        }
        return markdown.ToString().TrimEnd();
    }

    // Found by SystemKey, like the Quick Notes folder. Only an Admin creates it; after that,
    // clipping into it needs Edit there, like any other page in Sentinel.
    private async Task<WikiPage> EnsureFolderAsync(string username, CancellationToken cancellationToken)
    {
        var folder = await db.WikiPages.FirstOrDefaultAsync(page => page.SystemKey == SentinelWebClips.FolderSystemKey, cancellationToken);
        if (folder is null)
        {
            if (!await IsAdminAsync(username, cancellationToken))
                throw new SentinelClipException("There's no Web Clips folder yet - an admin's first clip creates it.");
            var created = await wikiService.SavePageAsync(new WikiPageEditorModel
            {
                Title = "Web Clips",
                Icon = "📎",
                BlocksJson = WikiBlockJson.Serialize(
                [
                    new WikiBlock(Guid.NewGuid(), WikiBlockTypes.Paragraph, 0,
                        [new WikiRichTextSpan("Pages saved with the Sentinel web clipper show up under this page.")],
                        new Dictionary<string, string>())
                ])
            }, username, cancellationToken: cancellationToken);
            folder = await db.WikiPages.FirstAsync(page => page.Id == created.Id, cancellationToken);
            folder.SystemKey = SentinelWebClips.FolderSystemKey;
            await db.SaveChangesAsync(cancellationToken);
            return folder;
        }
        if (folder.TrashedAt is not null)
            throw new SentinelClipException("The Web Clips folder is in the trash - restore it in Sentinel first.");
        await RequireEditAsync(folder.Id, username, "the Web Clips folder", cancellationToken);
        return folder;
    }

    private async Task RequireEditAsync(Guid pageId, string username, string what, CancellationToken cancellationToken)
    {
        if (!await accessService.CanAccessAsync(pageId, false, username, SentinelAccessLevels.Edit, cancellationToken))
            throw new SentinelClipException($"You don't have edit access to {what} - ask an admin to share it with you.");
    }

    private Task<bool> IsAdminAsync(string username, CancellationToken cancellationToken) =>
        db.AppUsers.AnyAsync(user => user.Username == username && user.Role == AppRoles.Admin, cancellationToken);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();

    private static SentinelClipTokenView ToView(SentinelClipToken token) =>
        new(token.Id, token.Name, token.Prefix, token.CreatedAt, token.LastUsedAtUnix);
}
