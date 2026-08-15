using System.Buffers.Binary;
using FluentAssertions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using GwsBusinessSuite.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Tests;

public sealed class HybridSemanticSearchServiceTests
{
    [Fact]
    public async Task SearchAsync_ShouldRankMeaningMatchWithoutKeywordOverlap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SemanticSearchDocuments.AddRange(
                Document("wiki-page", "Disaster recovery runbook", "Restore encrypted backups and rotate keys.", [1f, 0f]),
                Document("wiki-page", "Office lunch menu", "Sandwiches and salads for Friday.", [0f, 1f]));
            await setup.SaveChangesAsync();
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new HybridSemanticSearchService(
            factory,
            new FakeOllamaService([1f, 0f]),
            Options.Create(new SemanticSearchOptions { Model = "test-embedding", SimilarityThreshold = 0.5 }),
            cache,
            NullLogger<HybridSemanticSearchService>.Instance);

        var results = await service.SearchAsync("business continuity when everything becomes unreachable", [SemanticSourceTypes.WikiPage]);

        results.Should().ContainSingle();
        results[0].Title.Should().Be("Disaster recovery runbook");
        results[0].KeywordScore.Should().Be(0);
        results[0].SemanticScore.Should().BeApproximately(1, 0.0001);
    }

    [Fact]
    public async Task SearchAsync_ShouldFallBackToKeywordResultsWhenEmbeddingsFail()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.SemanticSearchDocuments.Add(Document("cms-page", "Launch checklist", "Verify the blue switch before launch.", [1f, 0f]));
            await setup.SaveChangesAsync();
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new HybridSemanticSearchService(
            factory,
            new FakeOllamaService(null),
            Options.Create(new SemanticSearchOptions { Model = "missing-model" }),
            cache,
            NullLogger<HybridSemanticSearchService>.Instance);

        var results = await service.SearchAsync("blue switch", [SemanticSourceTypes.CmsPage]);

        results.Should().ContainSingle(result => result.Title == "Launch checklist" && result.KeywordScore > 0);
    }

    [Fact]
    public async Task RebuildAsync_ShouldIndexNewContent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        Guid pageId;
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            var page = new WikiPage { Title = "Runbook", Slug = $"runbook-{Guid.NewGuid():N}", BlocksJson = "[]" };
            setup.WikiPages.Add(page);
            await setup.SaveChangesAsync();
            pageId = page.Id;
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ollama = new FakeOllamaService([1f, 0f]);
        var service = new HybridSemanticSearchService(
            factory, ollama, Options.Create(new SemanticSearchOptions { Model = "test-embedding" }),
            cache, NullLogger<HybridSemanticSearchService>.Instance);

        await service.RebuildAsync();

        await using var verify = factory.CreateDbContext();
        var documents = await verify.SemanticSearchDocuments.ToListAsync();
        documents.Should().ContainSingle(item => item.SourceType == SemanticSourceTypes.WikiPage && item.SourceId == pageId);
        ollama.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task RebuildAsync_ShouldRemoveStaleDocuments_EvenWhenNothingElseChanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            var page = new WikiPage { Title = "Runbook", Slug = $"runbook-{Guid.NewGuid():N}", BlocksJson = "[]" };
            setup.WikiPages.Add(page);
            await setup.SaveChangesAsync();

            // An already-current document for the live page (same hash/model, so it should NOT
            // be re-embedded) plus a leftover document whose source no longer exists (the page
            // was deleted/trashed after it was indexed) - this is the "changed.Count == 0 &&
            // stale.Count > 0" branch, previously untested, that still needs its own
            // SaveChangesAsync to actually persist the stale row's removal.
            setup.SemanticSearchDocuments.Add(Document(SemanticSourceTypes.WikiPage, "Runbook", "", [1f, 0f], page.Id));
            setup.SemanticSearchDocuments.Add(Document(SemanticSourceTypes.WikiPage, "Deleted page", "gone", [0f, 1f], Guid.NewGuid()));
            await setup.SaveChangesAsync();

            // Match the up-to-date document's stored hash exactly (mirrors Candidate()'s hash
            // input shape: "{title}\n{content}").
            var current = await setup.SemanticSearchDocuments.FirstAsync(item => item.SourceId == page.Id);
            current.ContentHash = ContentHash("Runbook", "");
            await setup.SaveChangesAsync();
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ollama = new FakeOllamaService([1f, 0f]);
        var service = new HybridSemanticSearchService(
            factory, ollama, Options.Create(new SemanticSearchOptions { Model = "test-embedding" }),
            cache, NullLogger<HybridSemanticSearchService>.Instance);

        await service.RebuildAsync();

        await using var verify = factory.CreateDbContext();
        var documents = await verify.SemanticSearchDocuments.ToListAsync();
        documents.Should().ContainSingle();
        documents[0].Title.Should().Be("Runbook");
        ollama.CallCount.Should().Be(0, "the surviving document's content hash was already current");
    }

    [Fact]
    public async Task RebuildAsync_ShouldReembed_WhenTheEmbeddingModelChanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            var page = new WikiPage { Title = "Runbook", Slug = $"runbook-{Guid.NewGuid():N}", BlocksJson = "[]" };
            setup.WikiPages.Add(page);
            await setup.SaveChangesAsync();

            var stale = Document(SemanticSourceTypes.WikiPage, "Runbook", "", [1f, 0f], page.Id);
            stale.ContentHash = ContentHash("Runbook", "");
            stale.EmbeddingModel = "old-embedding-model";
            setup.SemanticSearchDocuments.Add(stale);
            await setup.SaveChangesAsync();
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var ollama = new FakeOllamaService([0f, 1f]);
        var service = new HybridSemanticSearchService(
            factory, ollama, Options.Create(new SemanticSearchOptions { Model = "new-embedding-model" }),
            cache, NullLogger<HybridSemanticSearchService>.Instance);

        await service.RebuildAsync();

        await using var verify = factory.CreateDbContext();
        var document = await verify.SemanticSearchDocuments.SingleAsync();
        document.EmbeddingModel.Should().Be("new-embedding-model");
        ollama.CallCount.Should().Be(1, "an embedding-model change must force re-embedding even with an unchanged content hash");
    }

    [Fact]
    public async Task RebuildAsync_ShouldThrow_WhenEmbeddingIsUnavailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var factory = new TestDbContextFactory(connection);
        await using (var setup = factory.CreateDbContext())
        {
            await setup.Database.EnsureCreatedAsync();
            setup.WikiPages.Add(new WikiPage { Title = "Runbook", Slug = $"runbook-{Guid.NewGuid():N}", BlocksJson = "[]" });
            await setup.SaveChangesAsync();
        }

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new HybridSemanticSearchService(
            factory, new FakeOllamaService(null), Options.Create(new SemanticSearchOptions { Model = "missing-model" }),
            cache, NullLogger<HybridSemanticSearchService>.Instance);

        var act = () => service.RebuildAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static string ContentHash(string title, string content)
    {
        var input = $"{title}\n{content}".Trim();
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
    }

    private static SemanticSearchDocument Document(string sourceType, string title, string content, float[] vector, Guid sourceId) => new()
    {
        SourceType = sourceType,
        SourceId = sourceId,
        Title = title,
        Content = content,
        ContentHash = Guid.NewGuid().ToString("N"),
        EmbeddingModel = "test-embedding",
        Dimensions = vector.Length,
        Embedding = ToBlob(vector),
        IndexedAt = DateTimeOffset.UtcNow,
        CreatedBy = "test"
    };

    private static SemanticSearchDocument Document(string sourceType, string title, string content, float[] vector) => new()
    {
        SourceType = sourceType,
        SourceId = Guid.NewGuid(),
        Title = title,
        Content = content,
        ContentHash = Guid.NewGuid().ToString("N"),
        EmbeddingModel = "test-embedding",
        Dimensions = vector.Length,
        Embedding = ToBlob(vector),
        IndexedAt = DateTimeOffset.UtcNow,
        CreatedBy = "test"
    };

    private static byte[] ToBlob(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count * sizeof(float)];
        for (var index = 0; index < vector.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float)), vector[index]);
        return bytes;
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FakeOllamaService(float[]? queryVector) : IOllamaService
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default)
        {
            CallCount++;
            return queryVector is null
                ? throw new HttpRequestException("Embedding model unavailable")
                : Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => queryVector).ToList());
        }
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
