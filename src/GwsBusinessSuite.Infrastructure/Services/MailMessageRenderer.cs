using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using GwsBusinessSuite.Application.Mail;
using MimeKit;

namespace GwsBusinessSuite.Infrastructure.Services;

// A message as Mail shows it (both providers hand over the raw MIME message). The HTML is
// sanitized here, and the page also shows it in a sandboxed frame with no scripts and no
// same-origin, so a missed trick still can't reach the app. Remote images and other remote
// resources are left out unless asked for: they're how senders see that a message was opened.
// Inline (cid:) images come along as data: URIs.
public static partial class MailMessageRenderer
{
    private const long MaxInlineImageBytes = 5 * 1024 * 1024;

    public static MailMessageDetail Render(string id, string? threadId, MimeMessage message, bool unread, bool starred, bool showRemoteImages)
    {
        var html = message.HtmlBody;
        var text = message.TextBody ?? (html is null ? string.Empty : HtmlToText(html));
        var blocked = 0;
        var document = html is null
            ? Document(TextToHtml(text))
            : Document(Sanitize(html, InlineImages(message), showRemoteImages, out blocked));

        return new MailMessageDetail(
            id, threadId, Address(message.From.Mailboxes.FirstOrDefault()), Addresses(message.To), Addresses(message.Cc), Addresses(message.ReplyTo),
            message.Subject ?? string.Empty, message.Date, document, text.Trim(), blocked,
            Attachments(message).Select((part, index) => new MailAttachmentInfo(index, FileName(part, index), part.ContentType.MimeType, EstimatedSize(part))).ToList(),
            message.MessageId, message.References.ToList(), unread, starred);
    }

    // What counts as an attachment, in the order the indexes refer to.
    public static IReadOnlyList<MimePart> Attachments(MimeMessage message) =>
        message.BodyParts.OfType<MimePart>()
            .Where(part => part.IsAttachment || (part.ContentDisposition is null && part.FileName is not null && !part.ContentType.IsMimeType("text", "*")))
            .Where(part => string.IsNullOrEmpty(part.ContentId) || part.IsAttachment)
            .ToList();

    public static string FileName(MimePart part, int index) =>
        string.IsNullOrWhiteSpace(part.FileName) ? $"attachment-{index + 1}" : Path.GetFileName(part.FileName);

    public static MailAddress Address(MailboxAddress? mailbox) =>
        mailbox is null ? new MailAddress(string.Empty, string.Empty) : new MailAddress(mailbox.Name ?? string.Empty, mailbox.Address ?? string.Empty);

    public static IReadOnlyList<MailAddress> Addresses(InternetAddressList list) => list.Mailboxes.Select(Address).ToList();

    // Plain text for quoting a reply when a message has only HTML.
    public static string HtmlToText(string html)
    {
        var body = new HtmlParser().ParseDocument(html).Body;
        if (body is null) return string.Empty;
        foreach (var br in body.QuerySelectorAll("br")) br.Replace(body.Owner!.CreateTextNode("\n"));
        foreach (var block in body.QuerySelectorAll("p, div, tr, li, h1, h2, h3, h4, h5, h6")) block.Append(body.Owner!.CreateTextNode("\n"));
        return MultipleBlankLines().Replace(body.TextContent.Replace("\r", string.Empty), "\n\n").Trim();
    }

    internal static string Sanitize(string html, IReadOnlyDictionary<string, string> inlineImages, bool showRemoteImages, out int blockedRemote)
    {
        var sanitizer = new HtmlSanitizer();
        // Email styles live in <style> blocks and class names; inside the sandboxed frame they
        // can only style the message itself.
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedTags.Add("center");
        sanitizer.AllowedTags.Add("font");
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("bgcolor");
        sanitizer.AllowedAttributes.Add("color");
        sanitizer.AllowedAttributes.Add("face");
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("cid");
        foreach (var tag in new[] { "form", "input", "button", "select", "textarea", "option" }) sanitizer.AllowedTags.Remove(tag);

        var blocked = 0;
        sanitizer.FilterUrl += (_, e) =>
        {
            var url = e.OriginalUrl.Trim();
            if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = inlineImages.TryGetValue(url[4..].Trim('<', '>'), out var dataUri) ? dataUri : null;
                return;
            }
            var isLink = string.Equals(e.Tag?.LocalName, "a", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Tag?.LocalName, "area", StringComparison.OrdinalIgnoreCase);
            if (isLink)
            {
                // Links leave the frame in a new tab; only web and mail links.
                if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                      || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)))
                    e.SanitizedUrl = null;
                return;
            }
            if (!showRemoteImages && e.SanitizedUrl is not null)
            {
                blocked++;
                e.SanitizedUrl = null;
            }
        };
        var sanitized = sanitizer.SanitizeDocument(html);
        blockedRemote = blocked;
        return sanitized;
    }

    // The sanitized message as a whole document for the frame: links open in a new tab, nothing
    // sends a Referer, and a plain readable default under the message's own styles.
    internal static string Document(string sanitizedDocument)
    {
        const string head = """
            <meta charset="utf-8"><meta name="referrer" content="no-referrer"><base target="_blank">
            <style>html{background:#fff;color:#1f2328}body{margin:0;padding:16px;font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;overflow-wrap:anywhere}img{max-width:100%;height:auto}pre.gws-text{white-space:pre-wrap;font:inherit;margin:0}</style>
            """;
        var index = sanitizedDocument.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        return index >= 0
            ? sanitizedDocument.Insert(index + "<head>".Length, head)
            : $"<!DOCTYPE html><html><head>{head}</head><body>{sanitizedDocument}</body></html>";
    }

    internal static string TextToHtml(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        var linked = WebLink().Replace(encoded, match => $"<a href=\"{match.Value}\">{match.Value}</a>");
        return $"<!DOCTYPE html><html><head></head><body><pre class=\"gws-text\">{linked}</pre></body></html>";
    }

    // cid -> data: URI for the message's inline images.
    private static Dictionary<string, string> InlineImages(MimeMessage message)
    {
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || !part.ContentType.IsMimeType("image", "*") || part.Content is null) continue;
            using var stream = new MemoryStream();
            part.Content.DecodeTo(stream);
            if (stream.Length > MaxInlineImageBytes) continue;
            images[part.ContentId] = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(stream.ToArray())}";
        }
        return images;
    }

    // From the stored bytes without decoding them: base64 is about a third bigger than the file.
    private static long EstimatedSize(MimePart part)
    {
        var stored = part.Content?.Stream is { CanSeek: true } stream ? stream.Length : 0;
        return part.Content?.Encoding == ContentEncoding.Base64 ? stored * 3 / 4 : stored;
    }

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex WebLink();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
