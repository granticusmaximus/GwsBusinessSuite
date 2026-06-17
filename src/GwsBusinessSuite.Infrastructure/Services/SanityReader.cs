using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.ContentStudio;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class SanityReader(HttpClient http, IOptions<SanityOptions> options) : ISanityReader
{
    public async Task<IReadOnlyList<SanityArticleSummary>> GetArticlesAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var query = $"*[_type == \"{opts.DocumentType}\"] | order(approvedAt desc) " +
                    "{\"slug\": slug.current, title, " +
                    "\"metaDescription\": coalesce(seo.metaDescription, excerpt), " +
                    "primaryKeyword, approvedAt, bodyMarkdown}";

        var rows = await RunQueryAsync<SanityRawArticle>(query, ct);

        return rows
            .Where(a => !string.IsNullOrWhiteSpace(a.Slug))
            .Select(a => new SanityArticleSummary(
                Slug: a.Slug!,
                Title: a.Title ?? a.Slug!,
                MetaDescription: a.MetaDescription,
                PrimaryKeyword: a.PrimaryKeyword,
                EstimatedReadingTime: EstimateReadingTime(a.BodyMarkdown),
                PublishedAt: a.ApprovedAt,
                HasHeroImage: false))
            .ToList();
    }

    public async Task<SanityArticleDetail?> GetArticleBySlugAsync(string slug, CancellationToken ct = default)
    {
        var opts = options.Value;
        var safeSlug = slug.Replace("\"", "").Replace("\\", "");
        var query = $"*[_type == \"{opts.DocumentType}\" && slug.current == \"{safeSlug}\"] " +
                    "{\"slug\": slug.current, title, topic, " +
                    "\"metaDescription\": coalesce(seo.metaDescription, excerpt), " +
                    "bodyMarkdown, primaryKeyword, secondaryKeywords, approvedAt, author}";

        var rows = await RunQueryAsync<SanityRawArticle>(query, ct);
        var a = rows.FirstOrDefault();
        if (a is null || string.IsNullOrWhiteSpace(a.Slug))
            return null;

        return new SanityArticleDetail(
            Slug: a.Slug,
            Title: a.Title ?? a.Slug,
            Topic: a.Topic,
            MetaDescription: a.MetaDescription,
            ArticleMarkdown: a.BodyMarkdown,
            PrimaryKeyword: a.PrimaryKeyword,
            SecondaryKeywords: a.SecondaryKeywords,
            PublishedAt: a.ApprovedAt,
            Author: string.IsNullOrWhiteSpace(a.Author) ? "Grant Watson" : a.Author,
            EstimatedReadingTime: EstimateReadingTime(a.BodyMarkdown),
            HasHeroImage: false,
            HeroImageAltText: null,
            HeroImageCaption: null);
    }

    private async Task<List<T>> RunQueryAsync<T>(string query, CancellationToken ct)
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.ProjectId))
            return [];

        var apiVersion = string.IsNullOrWhiteSpace(opts.ApiVersion) ? "2021-10-21" : opts.ApiVersion;
        var dataset = string.IsNullOrWhiteSpace(opts.Dataset) ? "production" : opts.Dataset;
        var encoded = Uri.EscapeDataString(query);
        var url = $"https://{opts.ProjectId}.apicdn.sanity.io/v{apiVersion}/data/query/{dataset}?query={encoded}";

        if (!string.IsNullOrWhiteSpace(opts.Token))
            http.DefaultRequestHeaders.Authorization = new("Bearer", opts.Token);

        try
        {
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return [];
            var result = await response.Content.ReadFromJsonAsync<SanityQueryResponse<T>>(ct);
            return result?.Result ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string EstimateReadingTime(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "1 min read";
        var words = markdown.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
        var minutes = Math.Max(1, (int)Math.Ceiling(words / 200.0));
        return $"{minutes} min read";
    }
}

internal sealed class SanityQueryResponse<T>
{
    [JsonPropertyName("result")]
    public List<T>? Result { get; init; }
}

internal sealed class SanityRawArticle
{
    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("topic")]
    public string? Topic { get; init; }

    [JsonPropertyName("metaDescription")]
    public string? MetaDescription { get; init; }

    [JsonPropertyName("primaryKeyword")]
    public string? PrimaryKeyword { get; init; }

    [JsonPropertyName("secondaryKeywords")]
    public string? SecondaryKeywords { get; init; }

    [JsonPropertyName("approvedAt")]
    public DateTimeOffset? ApprovedAt { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("bodyMarkdown")]
    public string? BodyMarkdown { get; init; }
}
