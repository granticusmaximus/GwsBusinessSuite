using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.Localization;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GwsBusinessSuite.Tests;

public sealed class ContentLocalizationServiceTests
{
    [Fact]
    public async Task ListLocalizableContentAsync_ShouldReturnArticlesAndCmsPages()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddArticleAsync("Article title", "Body");
        await fixture.AddCmsPageAsync("Page title", new PageLayout());

        var content = await fixture.Service.ListLocalizableContentAsync();

        content.Should().Contain(item => item.Title == "Article title" && item.ContentType == ContentLocalizationContentTypes.Article);
        content.Should().Contain(item => item.Title == "Page title" && item.ContentType == ContentLocalizationContentTypes.CmsPage);
    }

    [Fact]
    public async Task SaveAsync_ShouldUpsertTheSameContentAndLanguageInPlace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync("Source", "Body");

        var created = await fixture.Service.SaveAsync(
            ContentLocalizationContentTypes.Article, article.Id,
            new ContentLocalizationEditor { LanguageCode = "ES", Title = "Primero", Body = "Uno" }, "author");
        var updated = await fixture.Service.SaveAsync(
            ContentLocalizationContentTypes.Article, article.Id,
            new ContentLocalizationEditor { LanguageCode = "es", Title = "Segundo", Body = "Dos" }, "editor");

        updated.Id.Should().Be(created.Id);
        updated.Title.Should().Be("Segundo");
        updated.IsAiGenerated.Should().BeFalse();
        (await fixture.Db.ContentLocalizations.ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task GenerateTranslationAsync_ShouldTranslateAnArticleAsAnAiDraft()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync("Source title", "Source **body**", "Source description");
        fixture.Ollama!.Response = """["Título","Cuerpo **traducido**",""]""";

        var result = await fixture.Service.GenerateTranslationAsync(
            ContentLocalizationContentTypes.Article, article.Id, "es", "llama3.1", "author");

        result.Title.Should().Be("Título");
        result.Body.Should().Be("Cuerpo **traducido**");
        result.MetaDescription.Should().BeNull();
        result.Status.Should().Be(ContentLocalizationStatuses.Draft);
        result.IsAiGenerated.Should().BeTrue();
        result.AiModel.Should().Be("llama3.1");
        fixture.Ollama.LastModel.Should().Be("llama3.1");
        fixture.Ollama.LastUserPrompt.Should().Contain("Source title").And.Contain("Source **body**");
    }

    [Fact]
    public async Task GenerateTranslationAsync_ShouldTranslateFlatCmsWidgetFieldsWithoutChangingStructure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var layout = new PageLayout
        {
            Sections =
            [
                new LayoutSection
                {
                    Id = "section-1",
                    Columns =
                    [
                        new LayoutColumn
                        {
                            Id = "column-1",
                            Widgets =
                            [
                                new LayoutWidget { Id = "heading-1", WidgetType = "heading", Props = new() { ["text"] = "Heading", ["level"] = "h2" } },
                                new LayoutWidget { Id = "paragraph-1", WidgetType = "paragraph", Props = new() { ["text"] = "Paragraph" } }
                            ]
                        }
                    ]
                }
            ]
        };
        var page = await fixture.AddCmsPageAsync("Source page", layout, "Source description");
        fixture.Ollama!.Response = """["Página","Encabezado","Párrafo","Descripción traducida"]""";

        var result = await fixture.Service.GenerateTranslationAsync(
            ContentLocalizationContentTypes.CmsPage, page.Id, "es", "llama3.1", "author");
        var translated = CmsBuilderJson.ParseLayoutOrEmpty(result.Body);
        var widgets = translated.Sections.Single().Columns.Single().Widgets;

        result.Title.Should().Be("Página");
        result.MetaDescription.Should().Be("Descripción traducida");
        widgets.Should().HaveCount(2);
        widgets[0].Id.Should().Be("heading-1");
        widgets[0].WidgetType.Should().Be("heading");
        widgets[0].Props["text"].Should().Be("Encabezado");
        widgets[0].Props["level"].Should().Be("h2");
        widgets[1].Id.Should().Be("paragraph-1");
        widgets[1].Props["text"].Should().Be("Párrafo");
    }

    [Fact]
    public async Task GenerateTranslationAsync_ShouldThrowWhenOllamaIsUnavailable()
    {
        await using var fixture = await Fixture.CreateAsync(withOllama: false);
        var article = await fixture.AddArticleAsync("Source", "Body");

        var act = () => fixture.Service.GenerateTranslationAsync(
            ContentLocalizationContentTypes.Article, article.Id, "es", "llama3.1", "author");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ollama is not available*");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[\"only one\"]")]
    public async Task GenerateTranslationAsync_ShouldThrowForAnInvalidTranslationPayload(string response)
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync("Source", "Body");
        fixture.Ollama!.Response = response;

        var act = () => fixture.Service.GenerateTranslationAsync(
            ContentLocalizationContentTypes.Article, article.Id, "es", "llama3.1", "author");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*invalid translation*");
    }

    [Fact]
    public async Task SetStatusAndGetPublishedAsync_ShouldExposeOnlyPublishedContent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync("Source", "Body");
        var draft = await fixture.Service.SaveAsync(
            ContentLocalizationContentTypes.Article, article.Id,
            new ContentLocalizationEditor { LanguageCode = "fr", Title = "Titre", Body = "Corps" }, "author");

        (await fixture.Service.GetPublishedAsync(ContentLocalizationContentTypes.Article, article.Id, "fr")).Should().BeNull();
        var published = await fixture.Service.SetStatusAsync(draft.Id, ContentLocalizationStatuses.Published, "publisher");
        var resolved = await fixture.Service.GetPublishedAsync(ContentLocalizationContentTypes.Article, article.Id, "FR");

        published.Status.Should().Be(ContentLocalizationStatuses.Published);
        resolved.Should().Be(new PublishedLocalization("Titre", "Corps", null));
        (await fixture.Service.GetPublishedAsync(ContentLocalizationContentTypes.Article, article.Id, "de")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveTheLocalization()
    {
        await using var fixture = await Fixture.CreateAsync();
        var article = await fixture.AddArticleAsync("Source", "Body");
        var localization = await fixture.Service.SaveAsync(
            ContentLocalizationContentTypes.Article, article.Id,
            new ContentLocalizationEditor { LanguageCode = "de", Title = "Titel", Body = "Text" }, "author");

        await fixture.Service.DeleteAsync(localization.Id, "author");

        (await fixture.Service.ListLocalizationsAsync(ContentLocalizationContentTypes.Article, article.Id)).Should().BeEmpty();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            ApplicationDbContext db,
            FakeOllamaService? ollama,
            ContentLocalizationService service)
        {
            _connection = connection;
            Db = db;
            Ollama = ollama;
            Service = service;
        }

        public ApplicationDbContext Db { get; }
        public FakeOllamaService? Ollama { get; }
        public ContentLocalizationService Service { get; }

        public static async Task<Fixture> CreateAsync(bool withOllama = true)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var ollama = withOllama ? new FakeOllamaService() : null;
            var service = new ContentLocalizationService(
                db, ollama, TimeProvider.System, NullLogger<ContentLocalizationService>.Instance);
            return new Fixture(connection, db, ollama, service);
        }

        public async Task<Article> AddArticleAsync(string title, string body, string? metaDescription = null)
        {
            var article = new Article
            {
                Title = title,
                Slug = $"article-{Guid.NewGuid():N}",
                BodyMarkdown = body,
                MetaDescription = metaDescription ?? string.Empty
            };
            Db.Articles.Add(article);
            await Db.SaveChangesAsync();
            return article;
        }

        public async Task<CmsPage> AddCmsPageAsync(string title, PageLayout layout, string? metaDescription = null)
        {
            var site = new CmsSite { Name = "Site", Slug = $"site-{Guid.NewGuid():N}" };
            Db.CmsSites.Add(site);
            var page = new CmsPage
            {
                SiteId = site.Id,
                Title = title,
                Slug = $"page-{Guid.NewGuid():N}",
                BlocksJson = CmsBuilderJson.Serialize(layout),
                MetaDescription = metaDescription ?? string.Empty
            };
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
        public string? LastModel { get; private set; }
        public string? LastUserPrompt { get; private set; }

        public Task<string> GenerateAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken ct = default)
        {
            LastModel = model;
            LastUserPrompt = userPrompt;
            return Task.FromResult(Response);
        }

        public IAsyncEnumerable<string> GenerateStreamAsync(
            string model,
            string systemPrompt,
            string userPrompt,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PullModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
