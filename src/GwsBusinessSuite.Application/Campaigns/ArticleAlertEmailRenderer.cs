using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GwsBusinessSuite.Application.Campaigns;

public sealed record ArticleAlertArticle(
    string Title,
    string Url,
    string? HeroImageUrl,
    DateTimeOffset PublishedAt,
    string BodyMarkdown);

// What the renderer needs to know about where it's sending from: absolute URLs (email clients
// can't resolve relative ones) and the site's accent color so the email matches the website.
public sealed record ArticleAlertBranding(string SiteName, string SiteUrl, string AccentHex);

public sealed record RenderedEmail(string Subject, string Html, string Text);

// Builds the alert and confirmation emails. Pure (no I/O) so every rule - excerpt length,
// tokens, required footer, absolute URLs - is unit-testable. Table-based, inline-styled HTML is
// deliberate: it's what Gmail/Outlook/Apple Mail actually render consistently.
public static class ArticleAlertEmailRenderer
{
    private static readonly MarkdownPipeline SafeMarkdown = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly Regex TemplateToken = new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled);
    private static readonly Regex VerifyMarker = new(@"\[VERIFY:[^\]]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    public static RenderedEmail RenderAlert(
        ArticleAlertSettings settings,
        ArticleAlertArticle article,
        string subscriberFirstName,
        string unsubscribeUrl,
        ArticleAlertBranding branding)
    {
        var publishedText = FormatPublished(article.PublishedAt, settings.TimeZoneId);
        var tokens = Tokens(subscriberFirstName, article.Title, publishedText, article.Url);
        var subject = Replace(settings.SubjectTemplate, tokens, html: false).Trim();
        if (subject.Length == 0) subject = article.Title;

        var excerpt = Excerpt(article.BodyMarkdown, settings.ExcerptLength);
        var accent = SafeAccent(branding.AccentHex);
        var readMore = string.IsNullOrWhiteSpace(settings.ReadMoreLabel) ? "Read More" : settings.ReadMoreLabel.Trim();
        var messageHtml = Markdown.ToHtml(Replace(settings.Message, tokens, html: false), SafeMarkdown);

        var card = new StringBuilder();
        if (settings.ShowHeroImage && IsAbsoluteHttp(article.HeroImageUrl))
        {
            card.Append($"""<tr><td style="padding:0"><a href="{H(article.Url)}"><img src="{H(article.HeroImageUrl!)}" alt="{H(article.Title)}" width="600" style="display:block;width:100%;max-width:600px;height:auto;border:0" /></a></td></tr>""");
        }
        card.Append($"""
            <tr><td style="padding:24px 28px 8px">
              <a href="{H(article.Url)}" style="color:#111827;text-decoration:none"><h2 style="margin:0 0 6px;font-size:22px;line-height:1.3;font-weight:700;color:#111827">{H(article.Title)}</h2></a>
              <p style="margin:0;font-size:13px;color:#6b7280">Published {H(publishedText)}</p>
            </td></tr>
            <tr><td style="padding:8px 28px 28px">
              <p style="margin:0;font-size:15px;line-height:1.6;color:#374151">{H(excerpt)} <a href="{H(article.Url)}" style="color:{accent};font-weight:700;text-decoration:none">({H(readMore)})</a></p>
            </td></tr>
            """);

        var preheader = excerpt.Length > 0 ? excerpt : article.Title;
        var html = Layout(
            preheader,
            $"""<tr><td style="padding:28px 28px 8px;font-size:15px;line-height:1.6;color:#374151">{messageHtml}</td></tr>""",
            $"""<tr><td style="padding:16px 20px 0"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;background:#ffffff">{card}</table></td></tr>""",
            Footer(settings, branding, unsubscribeUrl, "You're receiving this because you asked to hear about new articles from " + branding.SiteName + "."));

        var text = new StringBuilder()
            .AppendLine(Markdown.ToPlainText(Replace(settings.Message, tokens, html: false), SafeMarkdown).Trim())
            .AppendLine()
            .AppendLine(article.Title)
            .AppendLine($"Published {publishedText}")
            .AppendLine()
            .AppendLine($"{excerpt} ({readMore}: {article.Url})")
            .AppendLine()
            .Append(FooterText(settings, unsubscribeUrl))
            .ToString();

        return new RenderedEmail(subject, html, text);
    }

    // Weekly digest: the list's message, then one card per article (newest first).
    public static RenderedEmail RenderDigest(
        ArticleAlertSettings settings,
        IReadOnlyList<ArticleAlertArticle> articles,
        string subscriberFirstName,
        string unsubscribeUrl,
        ArticleAlertBranding branding)
    {
        if (articles.Count == 0) throw new ArgumentException("A digest needs at least one article.", nameof(articles));
        var ordered = articles.OrderByDescending(a => a.PublishedAt).ToList();
        var lead = ordered[0];
        var tokens = Tokens(subscriberFirstName, lead.Title, FormatPublished(lead.PublishedAt, settings.TimeZoneId), lead.Url);
        tokens["{{digest.count}}"] = ordered.Count.ToString(CultureInfo.InvariantCulture);
        var subjectTemplate = string.IsNullOrWhiteSpace(settings.DigestSubject) ? "New this week: {{digest.count}} new article(s)" : settings.DigestSubject;
        var subject = Replace(subjectTemplate, tokens, html: false).Trim();
        var accent = SafeAccent(branding.AccentHex);
        var readMore = string.IsNullOrWhiteSpace(settings.ReadMoreLabel) ? "Read More" : settings.ReadMoreLabel.Trim();
        var messageHtml = Markdown.ToHtml(Replace(settings.Message, tokens, html: false), SafeMarkdown);

        var cards = new StringBuilder();
        var text = new StringBuilder()
            .AppendLine(Markdown.ToPlainText(Replace(settings.Message, tokens, html: false), SafeMarkdown).Trim())
            .AppendLine();
        foreach (var article in ordered)
        {
            var publishedText = FormatPublished(article.PublishedAt, settings.TimeZoneId);
            var excerpt = Excerpt(article.BodyMarkdown, settings.ExcerptLength);
            var image = settings.ShowHeroImage && IsAbsoluteHttp(article.HeroImageUrl)
                ? $"""<tr><td style="padding:0"><a href="{H(article.Url)}"><img src="{H(article.HeroImageUrl!)}" alt="{H(article.Title)}" width="600" style="display:block;width:100%;max-width:600px;height:auto;border:0" /></a></td></tr>"""
                : string.Empty;
            cards.Append($"""
                <tr><td style="padding:16px 20px 0"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #e5e7eb;border-radius:12px;overflow:hidden;background:#ffffff">
                  {image}
                  <tr><td style="padding:20px 24px 6px">
                    <a href="{H(article.Url)}" style="color:#111827;text-decoration:none"><h2 style="margin:0 0 6px;font-size:20px;line-height:1.3;font-weight:700;color:#111827">{H(article.Title)}</h2></a>
                    <p style="margin:0;font-size:13px;color:#6b7280">Published {H(publishedText)}</p>
                  </td></tr>
                  <tr><td style="padding:6px 24px 22px">
                    <p style="margin:0;font-size:15px;line-height:1.6;color:#374151">{H(excerpt)} <a href="{H(article.Url)}" style="color:{accent};font-weight:700;text-decoration:none">({H(readMore)})</a></p>
                  </td></tr>
                </table></td></tr>
                """);
            text.AppendLine(article.Title).AppendLine($"Published {publishedText}").AppendLine($"{excerpt} ({readMore}: {article.Url})").AppendLine();
        }

        var html = Layout(
            $"{ordered.Count} new article{(ordered.Count == 1 ? "" : "s")}: {lead.Title}",
            $"""<tr><td style="padding:28px 28px 8px;font-size:15px;line-height:1.6;color:#374151">{messageHtml}</td></tr>""",
            cards.ToString(),
            Footer(settings, branding, unsubscribeUrl, "You're receiving this weekly roundup because you asked to hear about new articles from " + branding.SiteName + "."));
        text.Append(FooterText(settings, unsubscribeUrl));
        return new RenderedEmail(subject.Length == 0 ? $"{ordered.Count} new articles" : subject, html, text.ToString());
    }

    public static RenderedEmail RenderConfirmation(
        ArticleAlertSettings settings,
        string subscriberFirstName,
        string confirmUrl,
        ArticleAlertBranding branding)
    {
        var tokens = Tokens(subscriberFirstName, string.Empty, string.Empty, branding.SiteUrl);
        var accent = SafeAccent(branding.AccentHex);
        var messageHtml = Markdown.ToHtml(Replace(settings.ConfirmationMessage, tokens, html: false), SafeMarkdown);
        var subject = string.IsNullOrWhiteSpace(settings.ConfirmationSubject) ? "Please confirm your subscription" : settings.ConfirmationSubject.Trim();

        var html = Layout(
            "Confirm your subscription - one click and you're in.",
            $"""<tr><td style="padding:28px 28px 8px;font-size:15px;line-height:1.6;color:#374151">{messageHtml}</td></tr>""",
            $"""
            <tr><td align="center" style="padding:12px 28px 8px">
              <a href="{H(confirmUrl)}" style="display:inline-block;padding:12px 26px;border-radius:8px;background:{accent};color:#ffffff;font-size:15px;font-weight:700;text-decoration:none">Confirm subscription</a>
            </td></tr>
            <tr><td style="padding:8px 28px 24px;font-size:13px;line-height:1.5;color:#6b7280">This link expires in 7 days. If you didn't sign up, ignore this email - you won't hear from us again.</td></tr>
            """,
            Footer(settings, branding, unsubscribeUrl: null, reason: "Someone (hopefully you) entered this address on " + branding.SiteName + "."));

        var text = new StringBuilder()
            .AppendLine(Markdown.ToPlainText(Replace(settings.ConfirmationMessage, tokens, html: false), SafeMarkdown).Trim())
            .AppendLine()
            .AppendLine($"Confirm: {confirmUrl}")
            .AppendLine()
            .AppendLine("This link expires in 7 days. If you didn't sign up, ignore this email.")
            .Append(FooterText(settings, unsubscribeUrl: null))
            .ToString();

        return new RenderedEmail(subject, html, text);
    }

    // The article's opening prose, Markdown stripped, at most maxLength characters, cut back to
    // a word boundary with a trailing ellipsis when it had to be shortened. Only paragraphs are
    // read, so the title heading, code samples and lists don't leak in, and template/ad-slot
    // tokens ({{CJ_AD_SLOT_1}}) and [VERIFY: ...] markers are removed.
    public static string Excerpt(string markdown, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        maxLength = Math.Clamp(maxLength, 40, 600);

        var cleaned = VerifyMarker.Replace(TemplateToken.Replace(markdown, " "), " ");
        var document = Markdown.Parse(cleaned, SafeMarkdown);
        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants<ParagraphBlock>())
        {
            if (paragraph.Parent is ListItemBlock || paragraph.Parent is QuoteBlock) continue;
            var text = InlineText(paragraph.Inline);
            if (text.Length == 0) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(text);
            if (builder.Length > maxLength) break;
        }

        var plain = Whitespace.Replace(builder.ToString(), " ").Trim();
        if (plain.Length <= maxLength) return plain;

        var cut = plain[..maxLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > maxLength / 2) cut = cut[..lastSpace];
        return cut.TrimEnd(' ', ',', ';', ':', '-', '–', '—', '.') + "…";
    }

    public static string FormatPublished(DateTimeOffset publishedAt, string? timeZoneId)
    {
        var zone = ResolveZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(publishedAt, zone);
        return string.Create(CultureInfo.InvariantCulture, $"{local:MMMM d, yyyy} at {local:h:mm tt} {ZoneLabel(zone, local)}");
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static string ZoneLabel(TimeZoneInfo zone, DateTimeOffset local) => zone.Id switch
    {
        "America/New_York" or "Eastern Standard Time" => "ET",
        "America/Chicago" or "Central Standard Time" => "CT",
        "America/Denver" or "Mountain Standard Time" => "MT",
        "America/Los_Angeles" or "Pacific Standard Time" => "PT",
        "UTC" or "Etc/UTC" => "UTC",
        _ => local.Offset == TimeSpan.Zero ? "UTC" : $"UTC{(local.Offset < TimeSpan.Zero ? "-" : "+")}{local.Offset:hh\\:mm}"
    };

    private static string InlineText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        var builder = new StringBuilder();
        foreach (var node in inline.Descendants())
        {
            switch (node)
            {
                case LiteralInline literal:
                    builder.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.Append(' ');
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static Dictionary<string, string> Tokens(string firstName, string title, string publishedAt, string url) => new()
    {
        [ArticleAlertTokens.FirstName] = string.IsNullOrWhiteSpace(firstName) ? "there" : firstName.Trim(),
        [ArticleAlertTokens.Title] = title,
        [ArticleAlertTokens.PublishedAt] = publishedAt,
        [ArticleAlertTokens.Url] = url
    };

    private static string Replace(string template, IReadOnlyDictionary<string, string> tokens, bool html)
    {
        var result = template ?? string.Empty;
        foreach (var (token, value) in tokens)
        {
            result = result.Replace(token, html ? H(value) : value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string Layout(string preheader, string messageRow, string bodyRows, string footerRows) => $"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width,initial-scale=1" /><meta name="color-scheme" content="light" /><title></title></head>
        <body style="margin:0;padding:0;background:#f4f4f5;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif">
        <div style="display:none;max-height:0;overflow:hidden;opacity:0">{H(preheader)}</div>
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f5"><tr><td align="center" style="padding:24px 12px">
        <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="width:100%;max-width:600px;background:#ffffff;border-radius:14px">
        {messageRow}
        {bodyRows}
        {footerRows}
        </table>
        </td></tr></table>
        </body></html>
        """;

    private static string Footer(ArticleAlertSettings settings, ArticleAlertBranding branding, string? unsubscribeUrl, string reason)
    {
        var footer = new StringBuilder();
        footer.Append("""<tr><td style="padding:28px 28px 0"><table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="border-top:1px solid #e5e7eb"><tr><td style="padding-top:20px">""");
        footer.Append("""<table role="presentation" cellpadding="0" cellspacing="0"><tr>""");
        if (IsAbsoluteHttp(settings.FooterPhotoUrl))
        {
            footer.Append($"""<td style="padding-right:14px;vertical-align:middle"><img src="{H(settings.FooterPhotoUrl)}" alt="" width="64" height="64" style="display:block;width:64px;height:64px;border-radius:50%;object-fit:cover;border:0" /></td>""");
        }
        footer.Append("""<td style="vertical-align:middle">""");
        if (IsAbsoluteHttp(settings.FooterLogoUrl))
        {
            footer.Append($"""<a href="{H(branding.SiteUrl)}"><img src="{H(settings.FooterLogoUrl)}" alt="{H(settings.FooterBrandLine)}" height="36" style="display:block;height:36px;width:auto;border:0;margin-bottom:6px" /></a>""");
        }
        if (!string.IsNullOrWhiteSpace(settings.FooterBrandLine))
        {
            footer.Append($"""<div style="font-size:15px;font-weight:700;color:#111827">{H(settings.FooterBrandLine)}</div>""");
        }
        if (!string.IsNullOrWhiteSpace(settings.FooterSignOff))
        {
            footer.Append($"""<div style="font-size:13px;color:#6b7280;margin-top:2px">{H(settings.FooterSignOff)}</div>""");
        }
        footer.Append("</td></tr></table></td></tr></table></td></tr>");

        footer.Append("""<tr><td style="padding:18px 28px 28px;font-size:12px;line-height:1.6;color:#9ca3af">""");
        footer.Append(H(reason));
        if (unsubscribeUrl is not null)
        {
            footer.Append($""" <a href="{H(unsubscribeUrl)}" style="color:#6b7280;text-decoration:underline">Unsubscribe</a>.""");
        }
        if (!string.IsNullOrWhiteSpace(settings.MailingAddress))
        {
            footer.Append($"""<br />{H(settings.MailingAddress)}""");
        }
        footer.Append("</td></tr>");
        return footer.ToString();
    }

    private static string FooterText(ArticleAlertSettings settings, string? unsubscribeUrl)
    {
        var text = new StringBuilder().AppendLine("--");
        if (!string.IsNullOrWhiteSpace(settings.FooterBrandLine)) text.AppendLine(settings.FooterBrandLine);
        if (!string.IsNullOrWhiteSpace(settings.FooterSignOff)) text.AppendLine(settings.FooterSignOff);
        if (!string.IsNullOrWhiteSpace(settings.MailingAddress)) text.AppendLine(settings.MailingAddress);
        if (unsubscribeUrl is not null) text.AppendLine($"Unsubscribe: {unsubscribeUrl}");
        return text.ToString();
    }

    private static bool IsAbsoluteHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string SafeAccent(string? hex) => hex is not null && HexColor.IsMatch(hex) ? hex : "#2563eb";

    private static string H(string value) => WebUtility.HtmlEncode(value);
}
