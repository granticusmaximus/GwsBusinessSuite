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
        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct = default) =>
            queryVector is null
                ? throw new HttpRequestException("Embedding model unavailable")
                : Task.FromResult<IReadOnlyList<float[]>>(inputs.Select(_ => queryVector).ToList());
        public Task<string> GenerateAsync(string model, string systemPrompt, string userPrompt, CancellationToken ct = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<string> GenerateStreamAsync(string model, string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
        public Task<IReadOnlyCollection<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
        public Task PullModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GenerateImageAsync(string model, string prompt, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
