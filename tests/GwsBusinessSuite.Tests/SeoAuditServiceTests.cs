using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SeoAudit;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class SeoAuditServiceTests
{
    [Fact]
    public async Task AuditArticleAsync_ShouldScoreAWellFormedArticleHigh_WithNoAiPass()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync(new Article
        {
            Title = "A Complete Guide To Running A Small Business CRM", // 50 chars
            Slug = "small-business-crm-guide",
            PrimaryKeyword = "small business crm",
            MetaDescription = BuildMetaDescription("small business crm", 140), // within 120-160 window, keyword included
            BodyMarkdown = BuildMarkdown(
                wordCount: 350,
                headings: 3,
                includeImageWithAlt: true,
                includeLink: true,
                keyword: "small business crm")
        });

        var result = await fixture.Service.AuditArticleAsync(article.Id, model: null, primaryKeywordOverride: null);

        result.Score.Should().Be(100);
        result.AiModel.Should().BeNull();
        result.Findings.Should().OnlyContain(finding => finding.Status == SeoAuditFindingStatuses.Pass);
    }

    [Fact]
    public async Task AuditArticleAsync_ShouldScoreAThinArticleLow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync(new Article
        {
            Title = "x",
            Slug = "",
            MetaDescription = "",
            BodyMarkdown = "Just a couple words."
        });

        var result = await fixture.Service.AuditArticleAsync(article.Id, model: null, primaryKeywordOverride: null);

        result.Score.Should().BeLessThan(40);
        result.Findings.Should().Contain(finding => finding.Category == "Word count" && finding.Status == SeoAuditFindingStatuses.Fail);
        result.Findings.Should().Contain(finding => finding.Category == "Slug" && finding.Status == SeoAuditFindingStatuses.Fail);
    }

    [Fact]
    public async Task AuditCmsPageAsync_ShouldDetectHeadingLevelsAndMissingAltText()
    {
        await using var fixture = await Fixture.CreateAsync();
        var site = await fixture.AddCmsSiteAsync();
        var blocksJson = """
            {"sections":[
              {"id":"s1","label":"Section","background":"transparent","padding":"md","columnLayout":"full","columns":[
                {"id":"c1","span":12,"widgets":[
                  {"id":"w1","widgetType":"heading","props":{"text":"Main heading","level":"h2"}},
                  {"id":"w2","widgetType":"paragraph","props":{"text":"Some body copy here that talks about the product in reasonable depth."}},
                  {"id":"w3","widgetType":"image","props":{"src":"/x.png","alt":""}}
                ]}
              ]}
            ]}
            """;
        var page = await fixture.AddCmsPageAsync(site.Id, "Landing Page", "landing-page", blocksJson);

        var result = await fixture.Service.AuditCmsPageAsync(page.Id, model: null, primaryKeyword: null);

        result.Findings.Should().Contain(finding => finding.Category == "Image alt text" && finding.Status == SeoAuditFindingStatuses.Fail);
        result.Findings.Should().Contain(finding => finding.Category == "Heading structure" && finding.Points > 0);
    }

    [Fact]
    public async Task AuditCmsPageAsync_ShouldReportAParseFailure_RatherThanScoringMalformedBlocksJsonAsEmptyContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var site = await fixture.AddCmsSiteAsync();
        // A bare JSON array instead of the expected {"sections":[...]} wrapper object - valid
        // JSON, but the wrong shape, so it fails to deserialize into PageLayout. Previously this
        // was indistinguishable from a genuinely empty page and scored as if it had zero words,
        // no headings, etc.
        var page = await fixture.AddCmsPageAsync(site.Id, "Landing Page", "landing-page", "[1,2,3]");

        var result = await fixture.Service.AuditCmsPageAsync(page.Id, model: null, primaryKeyword: null);

        result.Findings.Should().ContainSingle(finding => finding.Category == "Content" && finding.Status == SeoAuditFindingStatuses.Fail);
        result.Findings.Should().NotContain(finding =>
            finding.Category == "Word count" || finding.Category == "Heading structure" || finding.Category == "Image alt text");
    }

    [Fact]
    public async Task AuditArticleAsync_ShouldBlendInAValidAiAssessment()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Ollama.Response = """{"directAnswerScore":10,"structureScore":10,"citabilityScore":10,"summary":"Very citable.","suggestions":["Add a table."]}""";
        var article = await fixture.AddArticleAsync(new Article
        {
            Title = "x",
            Slug = "",
            BodyMarkdown = "short"
        });

        var result = await fixture.Service.AuditArticleAsync(article.Id, model: "llama3.1", primaryKeywordOverride: null);

        result.AiModel.Should().Be("llama3.1");
        result.AiSummary.Should().Be("Very citable.");
        result.AiSuggestions.Should().ContainSingle().Which.Should().Be("Add a table.");
        // A perfect 10/10/10 AI score (100) blended 30% against a low deterministic score
        // must raise the final score above the deterministic-only score.
        var deterministicOnly = await fixture.Service.AuditArticleAsync(article.Id, model: null, primaryKeywordOverride: null);
        result.Score.Should().BeGreaterThan(deterministicOnly.Score);
    }

    [Fact]
    public async Task AuditArticleAsync_ShouldFallBackToDeterministicScore_WhenAiOutputIsUnparsable()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Ollama.Response = "I refuse to output JSON, sorry.";
        var article = await fixture.AddArticleAsync(new Article { Title = "x", Slug = "x", BodyMarkdown = "short" });

        var result = await fixture.Service.AuditArticleAsync(article.Id, model: "llama3.1", primaryKeywordOverride: null);

        result.AiModel.Should().BeNull();
        result.AiSummary.Should().BeEmpty();
    }

    [Fact]
    public async Task ListRunsAsync_ShouldReturnRunsNewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync(new Article { Title = "x", Slug = "x", BodyMarkdown = "short" });
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.AuditArticleAsync(article.Id, null, null);
        fixture.TimeProvider.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.AuditArticleAsync(article.Id, null, null);

        var runs = await fixture.Service.ListRunsAsync(SeoAuditContentTypes.Article, article.Id);

        runs.Should().HaveCount(2);
        runs[0].RunAt.Should().BeAfter(runs[1].RunAt);
    }

    [Fact]
    public async Task ListAuditableContentAsync_ShouldExcludeTrashedContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddArticleAsync(new Article { Title = "Live", Slug = "live", BodyMarkdown = "x" });
        await fixture.AddArticleAsync(new Article { Title = "Trashed", Slug = "trashed", BodyMarkdown = "x", TrashedAt = DateTimeOffset.UtcNow });

        var content = await fixture.Service.ListAuditableContentAsync();

        content.Should().ContainSingle(item => item.Title == "Live");
    }

    private static string BuildMetaDescription(string keyword, int targetLength)
    {
        var text = $"A complete overview of {keyword} for growing teams.";
        while (text.Length < targetLength) text += " More detail here.";
        return text.Length > targetLength ? text[..targetLength] : text;
    }

    private static string BuildMarkdown(int wordCount, int headings, bool includeImageWithAlt, bool includeLink, string keyword)
    {
        var body = string.Join(" ", Enumerable.Repeat("word", Math.Max(0, wordCount - 20)));
        var headingLines = string.Join("\n\n", Enumerable.Range(1, headings).Select(i => $"## Heading {i} about {keyword}"));
        var image = includeImageWithAlt ? "![A descriptive alt text](https://example.test/img.png)" : "";
        var link = includeLink ? "[a helpful source](https://example.test)" : "";
        return $"# Title mentioning {keyword}\n\n{headingLines}\n\n{body} {keyword} {image} {link}";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ApplicationDbContext db, FakeOllamaService ollama, FixedTimeProvider timeProvider, SeoAuditService service)
        {
            _connection = connection;
            Db = db;
            Ollama = ollama;
            TimeProvider = timeProvider;
            Service = service;
        }

        public ApplicationDbContext Db { get; }
        public FakeOllamaService Ollama { get; }
        public FixedTimeProvider TimeProvider { get; }
        public SeoAuditService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var ollama = new FakeOllamaService();
            var timeProvider = new FixedTimeProvider(DateTimeOffset.UtcNow);
            var service = new SeoAuditService(db, ollama, timeProvider, NullLogger<SeoAuditService>.Instance);
            return new Fixture(connection, db, ollama, timeProvider, service);
        }

        public async Task<Article> AddArticleAsync(Article article)
        {
            Db.Articles.Add(article);
            await Db.SaveChangesAsync();
            return article;
        }

        public async Task<CmsSite> AddCmsSiteAsync()
        {
            var site = new CmsSite { Name = "Site", Slug = "site" };
            Db.CmsSites.Add(site);
            await Db.SaveChangesAsync();
            return site;
        }

        public async Task<CmsPage> AddCmsPageAsync(Guid siteId, string title, string slug, string blocksJson)
        {
            var page = new CmsPage { SiteId = siteId, Title = title, Slug = slug, BlocksJson = blocksJson };
            Db.CmsPages.Add(page);
            await Db.SaveChangesAsync();
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeOllamaService : IOllamaService
    {
        public string Response { get; set; } = string.Empty;

        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            Task.FromResult(Response);

        public IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
