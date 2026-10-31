using Markdig;

namespace GwsBusinessSuite.Infrastructure.Services;

// What Mail sends (31-10-26): the message as typed, in Markdown, goes out as plain text and as the
// HTML it formats to - **bold**, _italic_, lists, links, and "> " quotes from a reply shown as a
// quote block. Raw HTML typed into the message is shown as text, not run. Styles are inline,
// since many mail apps drop <style>.
public static class MailFormatting
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseSoftlineBreakAsHardlineBreak()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    public static string ToHtml(string markdown)
    {
        var html = Markdown.ToHtml(markdown ?? string.Empty, Pipeline)
            .Replace("<blockquote>", "<blockquote style=\"margin:0 0 0 .8ex;border-left:1px solid #ccc;padding-left:1ex;color:#555\">", StringComparison.Ordinal)
            .Replace("<p>", "<p style=\"margin:0 0 1em\">", StringComparison.Ordinal);
        return $"<div style=\"font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.5;color:#1f2328\">{html}</div>";
    }
}
