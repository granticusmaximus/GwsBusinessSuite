using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace GwsBusinessSuite.Infrastructure.Services;

// Turns clipped HTML (a Readability article or a selection) into the Markdown dialect
// WikiBlockJson.FromMarkdown reads, so a clip becomes ordinary editable blocks. Only structure
// and text are kept: scripts, styles, forms and embeds are dropped, links keep http(s)/mailto
// targets only, and images keep absolute http(s) sources (they display through img-src https:,
// nothing is fetched by the server).
public static partial class WebClipMarkdownConverter
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "template", "iframe", "svg", "canvas", "form", "button",
        "input", "select", "textarea", "object", "embed", "video", "audio", "link", "meta", "head"
    };

    private static readonly HashSet<string> Inline = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "strong", "i", "em", "u", "s", "strike", "del", "code", "kbd", "samp",
        "span", "small", "sub", "sup", "mark", "cite", "q", "time", "label", "font", "br", "img", "var"
    };

    public static string Convert(string html, Uri baseUrl)
    {
        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        var output = new StringBuilder();
        if (document.Body is { } body) WriteBlocks(body.ChildNodes, output, baseUrl, 0);
        return BlankRuns().Replace(output.ToString(), "\n\n").Trim();
    }

    // For a page Readability can't make sense of: the page's <main>/<article> if it has one,
    // otherwise the body, without navigation, headers, footers and sidebars.
    public static string ConvertMainContent(string html, Uri baseUrl)
    {
        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        var root = document.QuerySelector("main, [role=main]") ?? document.QuerySelector("article") ?? document.Body;
        if (root is null) return string.Empty;
        foreach (var chrome in root.QuerySelectorAll("nav, header, footer, aside, [role=navigation], [role=banner], [role=contentinfo]").ToList())
            chrome.Remove();
        var output = new StringBuilder();
        WriteBlocks(root.ChildNodes, output, baseUrl, 0);
        return BlankRuns().Replace(output.ToString(), "\n\n").Trim();
    }

    private static void WriteBlocks(INodeList nodes, StringBuilder output, Uri baseUrl, int depth)
    {
        var inline = new StringBuilder();
        void FlushInline()
        {
            var text = inline.ToString().Trim();
            inline.Clear();
            if (text.Length == 0) return;
            output.Append(Indent(depth)).Append(EscapeLineStart(text)).Append("\n\n");
        }

        foreach (var node in nodes)
        {
            if (node is IElement element && !Inline.Contains(element.LocalName))
            {
                FlushInline();
                WriteBlock(element, output, baseUrl, depth);
            }
            else if (node is IElement { LocalName: "img" } image)
            {
                // A standalone image becomes an image block; one mid-sentence stays inline below.
                var src = Absolute(image.GetAttribute("src"), baseUrl, allowMailto: false);
                if (inline.ToString().Trim().Length == 0 && src is not null)
                {
                    output.Append(Indent(depth)).Append("![").Append(EscapeInline(image.GetAttribute("alt") ?? string.Empty)).Append("](").Append(src).Append(")\n\n");
                }
                else
                {
                    inline.Append(InlineText(node, baseUrl));
                }
            }
            else
            {
                inline.Append(InlineText(node, baseUrl));
            }
        }
        FlushInline();
    }

    private static void WriteBlock(IElement element, StringBuilder output, Uri baseUrl, int depth)
    {
        var name = element.LocalName.ToLowerInvariant();
        if (Skipped.Contains(name) || element.HasAttribute("hidden")) return;
        switch (name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var level = Math.Min(3, name[1] - '0');
                var heading = Collapse(InlineText(element, baseUrl));
                if (heading.Length > 0) output.Append(new string('#', level)).Append(' ').Append(heading).Append("\n\n");
                return;
            case "ul" or "ol":
                WriteList(element, output, baseUrl, depth, ordered: name == "ol");
                return;
            case "pre":
                var language = LanguageClass().Match(element.QuerySelector("code")?.ClassName ?? element.ClassName ?? string.Empty);
                var code = (element.TextContent ?? string.Empty).TrimEnd('\n').Replace("```", "` ` `");
                output.Append("```").Append(language.Success ? language.Groups[1].Value : string.Empty).Append('\n')
                    .Append(code).Append("\n```\n\n");
                return;
            case "blockquote":
                var quoted = new StringBuilder();
                WriteBlocks(element.ChildNodes, quoted, baseUrl, 0);
                foreach (var line in quoted.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    output.Append("> ").Append(line.Trim()).Append('\n');
                output.Append('\n');
                return;
            case "hr":
                output.Append("---\n\n");
                return;
            case "table":
                WriteTable(element, output, baseUrl);
                return;
            case "figcaption":
                var caption = Collapse(InlineText(element, baseUrl));
                if (caption.Length > 0) output.Append('*').Append(caption).Append("*\n\n");
                return;
            default:
                WriteBlocks(element.ChildNodes, output, baseUrl, depth);
                return;
        }
    }

    private static void WriteList(IElement list, StringBuilder output, Uri baseUrl, int depth, bool ordered)
    {
        var number = 1;
        foreach (var item in list.Children.Where(child => child.LocalName == "li"))
        {
            var own = new StringBuilder();
            var nested = new List<IElement>();
            foreach (var child in item.ChildNodes)
            {
                if (child is IElement { LocalName: "ul" or "ol" } sub) nested.Add(sub);
                else own.Append(InlineText(child, baseUrl));
            }
            var text = Collapse(own.ToString());
            if (text.Length > 0)
            {
                output.Append(Indent(depth)).Append(ordered ? $"{number++}. " : "- ").Append(text).Append('\n');
            }
            foreach (var sub in nested) WriteList(sub, output, baseUrl, depth + 1, sub.LocalName == "ol");
        }
        if (depth == 0) output.Append('\n');
    }

    private static void WriteTable(IElement table, StringBuilder output, Uri baseUrl)
    {
        var rows = table.QuerySelectorAll("tr")
            .Select(row => row.Children.Where(cell => cell.LocalName is "td" or "th")
                .Select(cell => Collapse(InlineText(cell, baseUrl)).Replace("|", "\\|")).ToList())
            .Where(cells => cells.Count > 0)
            .ToList();
        if (rows.Count == 0) return;
        var width = rows.Max(cells => cells.Count);
        for (var index = 0; index < rows.Count; index++)
        {
            var cells = rows[index].Concat(Enumerable.Repeat(string.Empty, width - rows[index].Count));
            output.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (index == 0) output.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
        }
        output.Append('\n');
    }

    private static string InlineText(INode node, Uri baseUrl)
    {
        if (node is IText text) return EscapeInline(WhiteSpace().Replace(text.Data, " "));
        if (node is not IElement element) return string.Empty;
        var name = element.LocalName.ToLowerInvariant();
        if (Skipped.Contains(name)) return string.Empty;
        string Children() => string.Concat(element.ChildNodes.Select(child => InlineText(child, baseUrl)));
        string Wrap(string mark)
        {
            var inner = Children();
            var trimmed = inner.Trim();
            return trimmed.Length == 0 ? inner : $"{Lead(inner)}{mark}{trimmed}{mark}{Trail(inner)}";
        }
        switch (name)
        {
            case "br":
                return " ";
            case "b" or "strong":
                return Wrap("**");
            case "i" or "em" or "cite":
                return Wrap("*");
            case "s" or "strike" or "del":
                return Wrap("~~");
            case "code" or "kbd" or "samp":
                var code = Collapse(element.TextContent ?? string.Empty).Replace("`", "'");
                return code.Length == 0 ? string.Empty : $"`{code}`";
            case "img":
                var alt = Collapse(element.GetAttribute("alt") ?? string.Empty);
                return alt.Length == 0 ? string.Empty : EscapeInline(alt);
            case "a":
                var label = Children();
                var href = Absolute(element.GetAttribute("href"), baseUrl, allowMailto: true);
                var trimmedLabel = Collapse(label);
                if (href is null || trimmedLabel.Length == 0) return label;
                return $"{Lead(label)}[{trimmedLabel.Replace("[", "\\[").Replace("]", "\\]")}]({href}){Trail(label)}";
            default:
                // Block content inside an inline context (e.g. a <p> in a list item) reads as a space.
                return Inline.Contains(name) ? Children() : $" {Children()} ";
        }
    }

    internal static string? Absolute(string? raw, Uri baseUrl, bool allowMailto)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(baseUrl, raw.Trim(), out var uri)) return null;
        var allowed = uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp
            || (allowMailto && uri.Scheme == Uri.UriSchemeMailto);
        // Spaces and parentheses would end the Markdown link early.
        return allowed ? uri.AbsoluteUri.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29") : null;
    }

    private static string EscapeInline(string text) => InlineSpecial().Replace(text, @"\$0");

    // "- x", "1. x", "# x", "> x" and "|" at the start of a paragraph are list/heading/quote/table
    // syntax; a leading backslash keeps them as text (the parser drops the backslash).
    private static string EscapeLineStart(string text) =>
        LineStartSpecial().IsMatch(text) ? "\\" + text : text;

    private static string Collapse(string text) => WhiteSpace().Replace(text, " ").Trim();
    private static string Lead(string text) => text.Length > 0 && char.IsWhiteSpace(text[0]) ? " " : string.Empty;
    private static string Trail(string text) => text.Length > 0 && char.IsWhiteSpace(text[^1]) ? " " : string.Empty;
    private static string Indent(int depth) => new(' ', depth * 4);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"[\\`*_\[\]~<+]")]
    private static partial Regex InlineSpecial();

    [GeneratedRegex(@"^(?:[-*+>#|]|\d+[.)]\s|-{3,}|```)")]
    private static partial Regex LineStartSpecial();

    [GeneratedRegex(@"(?:language|lang)-([A-Za-z0-9_+-]+)")]
    private static partial Regex LanguageClass();
}
