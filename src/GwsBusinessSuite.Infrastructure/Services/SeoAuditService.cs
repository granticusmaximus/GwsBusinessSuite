using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.SeoAudit;
using GwsBusinessSuite.Domain.Entities;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GwsBusinessSuite.Infrastructure.Services;

// A deterministic SEO checklist (title/description length, heading structure, image alt text,
// keyword coverage, slug quality, link presence - all computed directly, no AI involved) blended
// with an optional "AI-era readiness" pass from a local Ollama model (does this content read as
// a clear, quotable, citable answer the way an AI search engine would want to lift it). The AI
// pass is best-effort: if Ollama is unavailable or its output can't be parsed, the run still
// completes and is scored from the deterministic checklist alone - this is deliberately NOT a
// trained SEO-ranking model, just real checks plus a real (but skippable) LLM opinion.
public sealed class SeoAuditService(
    IAppDbContext db,
    IOllamaService? ollama,
    TimeProvider timeProvider,
    ILogger<SeoAuditService> logger) : ISeoAuditService
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private const string AiSystemPrompt =
        "You are an expert in both classic SEO and \"AI-era\" search readiness - how likely an " +
        "AI answer engine (ChatGPT, Perplexity, Google AI Overviews) would extract and cite this " +
        "content. Score three dimensions from 0 (poor) to 10 (excellent): directAnswerScore (does " +
        "it state a clear, quotable answer early, rather than burying it), structureScore (lists/" +
        "headings/short paragraphs an AI can parse cleanly), citabilityScore (specific facts, data, " +
        "or named sources that make it worth citing over a generic page). Respond with ONLY a JSON " +
        "object: {\"directAnswerScore\":N,\"structureScore\":N,\"citabilityScore\":N,\"summary\":" +
        "\"one or two sentences\",\"suggestions\":[\"concrete suggestion\", \"concrete suggestion\"]}";

    public async Task<IReadOnlyList<SeoAuditableContent>> ListAuditableContentAsync(CancellationToken cancellationToken = default)
    {
        var articles = await db.Articles.AsNoTracking()
            .Where(article => article.TrashedAt == null)
            .Select(article => new SeoAuditableContent(article.Id, article.Title, SeoAuditContentTypes.Article))
            .ToListAsync(cancellationToken);
        var pages = await db.CmsPages.AsNoTracking()
            .Where(page => page.TrashedAt == null)
            .Select(page => new SeoAuditableContent(page.Id, page.Title, SeoAuditContentTypes.CmsPage))
            .ToListAsync(cancellationToken);
        return articles.Concat(pages).OrderBy(item => item.Title).ToList();
    }

    public async Task<SeoAuditResult> AuditArticleAsync(
        Guid articleId, string? model, string? primaryKeywordOverride, CancellationToken cancellationToken = default)
    {
        var article = await db.Articles.AsNoTracking().FirstOrDefaultAsync(item => item.Id == articleId, cancellationToken)
            ?? throw new InvalidOperationException($"Article {articleId} was not found.");

        var analysis = AnalyzeMarkdown(article.BodyMarkdown);
        var keyword = string.IsNullOrWhiteSpace(primaryKeywordOverride) ? article.PrimaryKeyword : primaryKeywordOverride;
        var findings = BuildDeterministicFindings(
            article.Title, article.MetaDescription, article.Slug, analysis.PlainText, analysis.WordCount,
            analysis.HeadingLevels, analysis.ImageHasAlt, analysis.HasLink, keyword);

        return await FinishAuditAsync(
            SeoAuditContentTypes.Article, article.Id, article.Title, findings, analysis.PlainText, model, cancellationToken);
    }

    public async Task<SeoAuditResult> AuditCmsPageAsync(
        Guid pageId, string? model, string? primaryKeyword, CancellationToken cancellationToken = default)
    {
        var page = await db.CmsPages.AsNoTracking().FirstOrDefaultAsync(item => item.Id == pageId, cancellationToken)
            ?? throw new InvalidOperationException($"Page {pageId} was not found.");

        var analysis = AnalyzeLayout(page.BlocksJson);
        var findings = BuildDeterministicFindings(
            string.IsNullOrWhiteSpace(page.MetaTitle) ? page.Title : page.MetaTitle, page.MetaDescription, page.Slug,
            analysis.PlainText, analysis.WordCount, analysis.HeadingLevels, analysis.ImageHasAlt, analysis.HasLink, primaryKeyword);

        return await FinishAuditAsync(
            SeoAuditContentTypes.CmsPage, page.Id, page.Title, findings, analysis.PlainText, model, cancellationToken);
    }

    public async Task<IReadOnlyList<SeoAuditRunSummary>> ListRunsAsync(string contentType, Guid contentId, CancellationToken cancellationToken = default)
    {
        var runs = await db.SeoAuditRuns.AsNoTracking()
            .Where(run => run.ContentType == contentType && run.ContentId == contentId)
            .ToListAsync(cancellationToken);
        // SQLite/EF Core can't translate ORDER BY on a DateTimeOffset column - sort client-side.
        return runs
            .OrderByDescending(run => run.CreatedAt)
            .Select(run => new SeoAuditRunSummary(run.Id, run.Score, run.CreatedAt))
            .ToList();
    }

    private async Task<SeoAuditResult> FinishAuditAsync(
        string contentType, Guid contentId, string contentTitle, List<SeoAuditFinding> findings, string plainText,
        string? model, CancellationToken cancellationToken)
    {
        var deterministicScore = ComputeScore(findings);
        string? usedModel = null;
        var aiSummary = string.Empty;
        var aiSuggestions = new List<string>();
        var finalScore = deterministicScore;

        if (ollama is not null && !string.IsNullOrWhiteSpace(model))
        {
            var aiAssessment = await TryAssessAiReadinessAsync(ollama, model, plainText, cancellationToken);
            if (aiAssessment is not null)
            {
                usedModel = model;
                aiSummary = aiAssessment.Value.Summary;
                aiSuggestions = aiAssessment.Value.Suggestions;
                finalScore = (int)Math.Round(deterministicScore * 0.7 + aiAssessment.Value.Score * 0.3);
            }
        }

        var now = timeProvider.GetUtcNow();
        var run = new SeoAuditRun
        {
            ContentType = contentType,
            ContentId = contentId,
            Score = finalScore,
            FindingsJson = JsonSerializer.Serialize(findings),
            AiModel = usedModel,
            AiSummary = aiSummary,
            AiSuggestionsJson = JsonSerializer.Serialize(aiSuggestions),
            CreatedAt = now,
            CreatedBy = "seo-audit"
        };
        db.SeoAuditRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        return new SeoAuditResult(run.Id, contentType, contentId, contentTitle, finalScore, findings, usedModel, aiSummary, aiSuggestions, now);
    }

    private async Task<(int Score, string Summary, List<string> Suggestions)?> TryAssessAiReadinessAsync(
        IOllamaService ai, string model, string plainText, CancellationToken cancellationToken)
    {
        try
        {
            var userPrompt = $"CONTENT TO ASSESS:\n{LimitText(plainText, 6000)}";
            var raw = await ai.GenerateAsync(model, AiSystemPrompt, userPrompt, numCtx: 4096, cancellationToken);
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            if (JsonNode.Parse(raw[start..(end + 1)]) is not JsonObject parsed) return null;

            var direct = Math.Clamp(parsed["directAnswerScore"]?.GetValue<int>() ?? 0, 0, 10);
            var structure = Math.Clamp(parsed["structureScore"]?.GetValue<int>() ?? 0, 0, 10);
            var citability = Math.Clamp(parsed["citabilityScore"]?.GetValue<int>() ?? 0, 0, 10);
            var summary = parsed["summary"]?.GetValue<string>() ?? string.Empty;
            var suggestions = (parsed["suggestions"] as JsonArray)?
                .Select(item => item?.GetValue<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Take(6)
                .ToList() ?? [];

            var score = (int)Math.Round(100.0 * (direct + structure + citability) / 30);
            return (score, summary, suggestions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI-era readiness assessment failed - the audit will fall back to the deterministic score alone.");
            return null;
        }
    }

    private static int ComputeScore(List<SeoAuditFinding> findings)
    {
        var maxTotal = findings.Sum(finding => finding.MaxPoints);
        if (maxTotal == 0) return 0;
        var achieved = findings.Sum(finding => finding.Points);
        return (int)Math.Round(100.0 * achieved / maxTotal);
    }

    private static List<SeoAuditFinding> BuildDeterministicFindings(
        string title, string metaDescription, string slug, string bodyText, int wordCount,
        IReadOnlyList<int> headingLevels, IReadOnlyList<bool> imageHasAlt, bool hasLink, string? keyword)
    {
        var findings = new List<SeoAuditFinding>
        {
            EvaluateLength("Title length", title, 30, 60, 15),
            EvaluateLength("Meta description length", metaDescription, 120, 160, 15),
            EvaluateWordCount(wordCount),
            EvaluateHeadingStructure(headingLevels),
            EvaluateImageAltText(imageHasAlt),
            EvaluateSlug(slug)
        };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            findings.Add(EvaluateKeyword(keyword, title, metaDescription, bodyText));
        }

        findings.Add(hasLink
            ? new SeoAuditFinding("Links", SeoAuditFindingStatuses.Pass, "At least one link was found.", 5, 5)
            : new SeoAuditFinding("Links", SeoAuditFindingStatuses.Warning, "No links found - consider linking to a source or related page.", 2, 5));

        return findings;
    }

    private static SeoAuditFinding EvaluateKeyword(string keyword, string title, string metaDescription, string bodyText)
    {
        var inTitle = title.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        var inDescription = metaDescription.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        var inBody = bodyText.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        var points = (inTitle ? 5 : 0) + (inDescription ? 5 : 0) + (inBody ? 5 : 0);
        var status = points == 15 ? SeoAuditFindingStatuses.Pass : points > 0 ? SeoAuditFindingStatuses.Warning : SeoAuditFindingStatuses.Fail;
        var missing = new List<string>();
        if (!inTitle) missing.Add("title");
        if (!inDescription) missing.Add("meta description");
        if (!inBody) missing.Add("body");
        var message = missing.Count == 0
            ? $"\"{keyword}\" appears in the title, meta description, and body."
            : $"\"{keyword}\" is missing from: {string.Join(", ", missing)}.";
        return new SeoAuditFinding("Keyword coverage", status, message, points, 15);
    }

    private static SeoAuditFinding EvaluateLength(string category, string value, int idealMin, int idealMax, int maxPoints)
    {
        var length = value?.Trim().Length ?? 0;
        if (length == 0)
        {
            return new SeoAuditFinding(category, SeoAuditFindingStatuses.Fail, $"{category} is empty.", 0, maxPoints);
        }
        if (length >= idealMin && length <= idealMax)
        {
            return new SeoAuditFinding(category, SeoAuditFindingStatuses.Pass, $"{length} characters - within the {idealMin}-{idealMax} target range.", maxPoints, maxPoints);
        }
        return new SeoAuditFinding(category, SeoAuditFindingStatuses.Warning, $"{length} characters - outside the {idealMin}-{idealMax} target range.", maxPoints / 2, maxPoints);
    }

    private static SeoAuditFinding EvaluateWordCount(int wordCount)
    {
        if (wordCount < 150)
            return new SeoAuditFinding("Word count", SeoAuditFindingStatuses.Fail, $"{wordCount} words - too short for meaningful SEO coverage.", 0, 15);
        if (wordCount < 300)
            return new SeoAuditFinding("Word count", SeoAuditFindingStatuses.Warning, $"{wordCount} words - consider expanding to 300+.", 8, 15);
        return new SeoAuditFinding("Word count", SeoAuditFindingStatuses.Pass, $"{wordCount} words.", 15, 15);
    }

    private static SeoAuditFinding EvaluateHeadingStructure(IReadOnlyList<int> headingLevels)
    {
        var subheadingCount = headingLevels.Count(level => level >= 2);
        if (subheadingCount == 0)
            return new SeoAuditFinding("Heading structure", SeoAuditFindingStatuses.Fail, "No subheadings found - break the content up with H2/H3s.", 0, 15);
        if (subheadingCount == 1)
            return new SeoAuditFinding("Heading structure", SeoAuditFindingStatuses.Warning, "Only one subheading - add more to improve structure.", 8, 15);
        return new SeoAuditFinding("Heading structure", SeoAuditFindingStatuses.Pass, $"{subheadingCount} subheadings.", 15, 15);
    }

    private static SeoAuditFinding EvaluateImageAltText(IReadOnlyList<bool> imageHasAlt)
    {
        if (imageHasAlt.Count == 0)
            return new SeoAuditFinding("Image alt text", SeoAuditFindingStatuses.Pass, "No images to check.", 10, 10);
        var missing = imageHasAlt.Count(hasAlt => !hasAlt);
        return missing == 0
            ? new SeoAuditFinding("Image alt text", SeoAuditFindingStatuses.Pass, $"All {imageHasAlt.Count} image(s) have alt text.", 10, 10)
            : new SeoAuditFinding("Image alt text", SeoAuditFindingStatuses.Fail, $"{missing} of {imageHasAlt.Count} image(s) are missing alt text.", 0, 10);
    }

    private static SeoAuditFinding EvaluateSlug(string slug)
    {
        var trimmed = slug?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return new SeoAuditFinding("Slug", SeoAuditFindingStatuses.Fail, "No slug set.", 0, 10);
        var isClean = trimmed.Length <= 60 && trimmed == trimmed.ToLowerInvariant() && !trimmed.Contains(' ') && !trimmed.Contains('_');
        return isClean
            ? new SeoAuditFinding("Slug", SeoAuditFindingStatuses.Pass, $"\"{trimmed}\" is short, lowercase, and hyphenated.", 10, 10)
            : new SeoAuditFinding("Slug", SeoAuditFindingStatuses.Warning, $"\"{trimmed}\" - prefer short, lowercase, hyphen-separated slugs.", 5, 10);
    }

    private readonly record struct ContentAnalysis(string PlainText, int WordCount, List<int> HeadingLevels, List<bool> ImageHasAlt, bool HasLink);

    private static ContentAnalysis AnalyzeMarkdown(string markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, MarkdownPipeline);
        var headingLevels = document.Descendants<HeadingBlock>().Select(heading => heading.Level).ToList();
        var links = document.Descendants<LinkInline>().ToList();
        var imageHasAlt = links.Where(link => link.IsImage).Select(link => !string.IsNullOrWhiteSpace(InlineText(link.FirstChild))).ToList();
        var hasLink = links.Any(link => !link.IsImage);
        var plainText = Markdig.Markdown.ToPlainText(markdown ?? string.Empty, MarkdownPipeline);
        var wordCount = CountWords(plainText);
        return new ContentAnalysis(plainText, wordCount, headingLevels, imageHasAlt, hasLink);
    }

    private static string InlineText(Inline? inline)
    {
        var builder = new StringBuilder();
        for (var current = inline; current is not null; current = current.NextSibling)
        {
            if (current is LiteralInline literal) builder.Append(literal.Content.ToString());
            else if (current is ContainerInline container) builder.Append(InlineText(container.FirstChild));
        }
        return builder.ToString();
    }

    private static ContentAnalysis AnalyzeLayout(string blocksJson)
    {
        var layout = CmsBuilderJson.ParseLayoutOrEmpty(blocksJson);
        var widgets = layout.Sections.SelectMany(section => section.Columns).SelectMany(column => column.Widgets).ToList();

        var headingLevels = widgets
            .Where(widget => widget.WidgetType == "heading")
            .Select(widget => widget.Props.GetValueOrDefault("level", "h2"))
            .Select(level => level.Length == 2 && level[0] is 'h' or 'H' && char.IsDigit(level[1]) ? level[1] - '0' : 2)
            .ToList();

        var imageHasAlt = widgets
            .Where(widget => widget.WidgetType == "image")
            .Select(widget => !string.IsNullOrWhiteSpace(widget.Props.GetValueOrDefault("alt")))
            .ToList();

        var hasLink = widgets.Any(widget => widget.WidgetType == "button" && !string.IsNullOrWhiteSpace(widget.Props.GetValueOrDefault("href")));

        var textBuilder = new StringBuilder();
        foreach (var widget in widgets)
        {
            var text = widget.WidgetType switch
            {
                "heading" => widget.Props.GetValueOrDefault("text"),
                "paragraph" => widget.Props.GetValueOrDefault("text"),
                "richtext" => widget.Props.GetValueOrDefault("content"),
                "hero" => widget.Props.GetValueOrDefault("headline"),
                "card" => widget.Props.GetValueOrDefault("title"),
                "testimonial" => widget.Props.GetValueOrDefault("quote"),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) textBuilder.Append(text).Append(' ');
        }
        var plainText = textBuilder.ToString();
        return new ContentAnalysis(plainText, CountWords(plainText), headingLevels, imageHasAlt, hasLink);
    }

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string LimitText(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
