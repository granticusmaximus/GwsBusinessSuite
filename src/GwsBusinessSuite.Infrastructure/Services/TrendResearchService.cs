using System.Net.Http.Json;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

// Discovers what the software-development community is currently talking about (Hacker
// News + dev.to, both free/keyless JSON APIs) and asks Ollama to synthesize that raw
// signal into hot takes and concrete article angles. Search/discovery is delegated to
// these real data sources because a local LLM has no way to know what's trending today;
// Ollama's role is limited to summarizing and brainstorming from data it's actually given.
//
// Reddit's public .json endpoints were tried first but now blanket-403 non-browser and
// datacenter traffic (confirmed even from a residential dev machine), so they aren't a
// usable source for a server-side feature. dev.to's official articles API has no such
// restriction and is arguably a better fit anyway: it's specifically newly-published
// technical blog posts rather than link-aggregator discussion threads.
public sealed class TrendResearchService(
    HttpClient http,
    IOllamaService ollama,
    IMemoryCache cache,
    IOptions<ContentStudioOptions> options,
    ILogger<TrendResearchService> logger) : ITrendResearchService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(4);

    public async Task<TrendResearchResult> ResearchTrendsAsync(TrendResearchRequest request, CancellationToken cancellationToken = default)
    {
        var focusArea = request.FocusArea.Trim();
        var cacheKey = $"trend-research:{(string.IsNullOrWhiteSpace(focusArea) ? "general" : focusArea.ToLowerInvariant())}";

        if (!request.ForceRefresh && cache.TryGetValue(cacheKey, out TrendResearchResult? cached) && cached is not null)
        {
            return cached with { FromCache = true };
        }

        var signals = await GatherSignalsAsync(focusArea, cancellationToken);

        if (signals.Count == 0)
        {
            logger.LogWarning("Trend research found no signals from Hacker News or dev.to for focus area '{FocusArea}'.", focusArea);
        }

        var (summary, suggestions) = await SynthesizeWithOllamaAsync(focusArea, signals, cancellationToken);

        var result = new TrendResearchResult
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            FocusArea = focusArea,
            OverallSummary = summary,
            Signals = signals,
            Suggestions = suggestions,
            FromCache = false
        };

        cache.Set(cacheKey, result, CacheDuration);

        return result;
    }

    private async Task<List<TrendSignal>> GatherSignalsAsync(string focusArea, CancellationToken cancellationToken)
    {
        var signals = new List<TrendSignal>();

        try
        {
            signals.AddRange(await FetchHackerNewsAsync(focusArea, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hacker News trend fetch failed for focus area '{FocusArea}'.", focusArea);
        }

        try
        {
            signals.AddRange(await FetchDevToAsync(focusArea, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "dev.to trend fetch failed for focus area '{FocusArea}'.", focusArea);
        }

        return signals
            .Where(signal => !string.IsNullOrWhiteSpace(signal.Title))
            .OrderByDescending(signal => signal.Score)
            .Take(30)
            .ToList();
    }

    private async Task<List<TrendSignal>> FetchHackerNewsAsync(string focusArea, CancellationToken cancellationToken)
    {
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();

        var url = string.IsNullOrWhiteSpace(focusArea)
            ? "https://hn.algolia.com/api/v1/search?tags=front_page&hitsPerPage=20"
            : $"https://hn.algolia.com/api/v1/search?tags=story&query={Uri.EscapeDataString(focusArea)}&numericFilters={Uri.EscapeDataString($"created_at_i>{weekAgo}")}&hitsPerPage=15";

        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<HackerNewsResponse>(cancellationToken: cancellationToken);
        if (payload?.Hits is null)
        {
            return [];
        }

        return payload.Hits
            .Where(hit => !string.IsNullOrWhiteSpace(hit.Title))
            .Select(hit => new TrendSignal
            {
                Title = hit.Title ?? string.Empty,
                Url = string.IsNullOrWhiteSpace(hit.Url) ? $"https://news.ycombinator.com/item?id={hit.ObjectId}" : hit.Url!,
                Source = "Hacker News",
                Score = hit.Points,
                CommentCount = hit.NumComments
            })
            .ToList();
    }

    private async Task<List<TrendSignal>> FetchDevToAsync(string focusArea, CancellationToken cancellationToken)
    {
        // dev.to tags are single lowercase words with no spaces/punctuation.
        var tagSlug = new string(focusArea.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var url = string.IsNullOrWhiteSpace(tagSlug)
            ? "https://dev.to/api/articles?top=7&per_page=20"
            : $"https://dev.to/api/articles?top=7&per_page=15&tag={Uri.EscapeDataString(tagSlug)}";

        using var response = await http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var posts = await response.Content.ReadFromJsonAsync<DevToArticle[]>(cancellationToken: cancellationToken);

        return (posts ?? [])
            .Where(post => !string.IsNullOrWhiteSpace(post.Title))
            .Select(post => new TrendSignal
            {
                Title = post.Title ?? string.Empty,
                Url = post.Url ?? string.Empty,
                Source = "dev.to",
                Score = post.PublicReactionsCount,
                CommentCount = post.CommentsCount
            })
            .ToList();
    }

    private async Task<(string Summary, List<TrendTopicSuggestion> Suggestions)> SynthesizeWithOllamaAsync(
        string focusArea,
        IReadOnlyList<TrendSignal> signals,
        CancellationToken cancellationToken)
    {
        if (signals.Count == 0)
        {
            return ("No live trend data could be retrieved. Check network access to Hacker News and dev.to and try again.", []);
        }

        var model = string.IsNullOrWhiteSpace(options.Value.Model)
            ? ContentStudioOptions.DefaultModel
            : options.Value.Model;

        var signalLines = signals
            .Take(25)
            .Select(signal => $"- [{signal.Source}, score {signal.Score}, {signal.CommentCount} comments] {signal.Title}");

        var focusLine = string.IsNullOrWhiteSpace(focusArea)
            ? "general software development"
            : focusArea;

        var prompt = $$"""
        You are a technical content strategist for a blog about C#, .NET, ASP.NET Core, and Blazor
        aimed at professional developers. Below is a list of real, currently trending posts and
        articles from Hacker News and dev.to related to "{{focusLine}}".

        Trending items:
        {{string.Join('\n', signalLines)}}

        Using only the items above:
        1. Write a short overall summary (3-5 sentences) of what the developer community is currently
           talking about, including any notable positive and negative hot takes you can infer.
        2. Suggest exactly 4 article topics this blog could write that would resonate with these
           trends. Each must be realistic for a C#/.NET-focused blog (it is fine to relate a general
           trend back to a .NET/C#/Blazor angle).

        Respond using exactly this plain-text format, with no markdown formatting, no extra commentary,
        and each suggestion separated by a line containing only ---:

        OVERALL_SUMMARY: <your summary>
        ---
        TOPIC: <article topic>
        PRIMARY_KEYWORD: <one short SEO keyword phrase>
        SECONDARY_KEYWORDS: <comma separated keyword phrases>
        RATIONALE: <why this topic fits the current trend, one or two sentences>
        POSITIVE_TAKE: <a positive hot take relevant to this topic>
        NEGATIVE_TAKE: <a negative/skeptical hot take relevant to this topic>
        ---
        TOPIC: <article topic>
        PRIMARY_KEYWORD: <one short SEO keyword phrase>
        SECONDARY_KEYWORDS: <comma separated keyword phrases>
        RATIONALE: <why this topic fits the current trend, one or two sentences>
        POSITIVE_TAKE: <a positive hot take relevant to this topic>
        NEGATIVE_TAKE: <a negative/skeptical hot take relevant to this topic>
        """;

        var raw = (await ollama.GenerateAsync(model, string.Empty, prompt, cancellationToken)).Trim();

        return ParseOllamaResponse(raw);
    }

    public static (string Summary, List<TrendTopicSuggestion> Suggestions) ParseOllamaResponse(string raw)
    {
        var blocks = raw.Split("---", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (blocks.Length == 0)
        {
            return (raw, []);
        }

        var summary = ExtractField(blocks[0], "OVERALL_SUMMARY");
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = blocks[0].Trim();
        }

        var suggestions = new List<TrendTopicSuggestion>();

        foreach (var block in blocks.Skip(1))
        {
            var topic = ExtractField(block, "TOPIC");
            if (string.IsNullOrWhiteSpace(topic))
            {
                continue;
            }

            suggestions.Add(new TrendTopicSuggestion
            {
                Topic = topic,
                PrimaryKeyword = ExtractField(block, "PRIMARY_KEYWORD"),
                SecondaryKeywords = ExtractField(block, "SECONDARY_KEYWORDS"),
                Rationale = ExtractField(block, "RATIONALE"),
                PositiveTake = ExtractField(block, "POSITIVE_TAKE"),
                NegativeTake = ExtractField(block, "NEGATIVE_TAKE")
            });
        }

        return (summary, suggestions);
    }

    private static string ExtractField(string block, string fieldName)
    {
        var prefix = $"{fieldName}:";
        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return string.Empty;
    }

    private sealed record HackerNewsResponse(HackerNewsHit[]? Hits);

    private sealed record HackerNewsHit(
        string? Title,
        string? Url,
        int Points,
        [property: System.Text.Json.Serialization.JsonPropertyName("num_comments")] int NumComments,
        [property: System.Text.Json.Serialization.JsonPropertyName("objectID")] string? ObjectId);

    private sealed record DevToArticle(
        string? Title,
        string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("public_reactions_count")] int PublicReactionsCount,
        [property: System.Text.Json.Serialization.JsonPropertyName("comments_count")] int CommentsCount);
}