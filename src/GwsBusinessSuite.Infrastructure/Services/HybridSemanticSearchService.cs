using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GwsBusinessSuite.Application.Abstractions;
using GwsBusinessSuite.Application.CmsBuilder;
using GwsBusinessSuite.Application.SemanticSearch;
using GwsBusinessSuite.Application.Wiki;
using GwsBusinessSuite.Domain.Entities;
using GwsBusinessSuite.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GwsBusinessSuite.Infrastructure.Services;

public sealed class HybridSemanticSearchService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IOllamaService ollama,
    IOptions<SemanticSearchOptions> options,
    IMemoryCache cache,
    ILogger<HybridSemanticSearchService> logger) : IHybridSearchService
{
    private const int MaximumIndexedCharacters = 12_000;
    private const int MaximumKeywordCandidates = 2_500;
    private const string EmbeddingFailureCacheKey = "semantic-search:embedding-unavailable";
    private static readonly Regex TokenPattern = new("[a-z0-9][a-z0-9._-]{1,}", RegexOptions.Compiled);

    public async Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string query,
        IReadOnlyCollection<string>? sourceTypes = null,
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = Normalize(query, 2_000);
        if (normalizedQuery.Length < 2 || take <= 0) return [];

        var allowedTypes = NormalizeTypes(sourceTypes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var keywordQuery = db.SemanticSearchDocuments.AsNoTracking();
        if (allowedTypes.Length > 0)
        {
            keywordQuery = keywordQuery.Where(item => allowedTypes.Contains(item.SourceType));
        }
        var keywordDocuments = await keywordQuery.Take(MaximumKeywordCandidates).ToListAsync(cancellationToken);
        var terms = TokenPattern.Matches(normalizedQuery.ToLowerInvariant())
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        var ranked = new Dictionary<Guid, RankedDocument>();
        foreach (var document in keywordDocuments)
        {
            var keywordScore = ScoreKeyword(document.Title, document.Content, normalizedQuery, terms);
            if (keywordScore > 0)
            {
                ranked[document.Id] = new RankedDocument(document, keywordScore, 0);
            }
        }

        if (options.Value.Enabled
            && !cache.TryGetValue(EmbeddingFailureCacheKey, out _))
        {
            try
            {
                var vectors = await ollama.EmbedAsync(options.Value.Model, [normalizedQuery], cancellationToken);
                var queryVector = vectors.Single();
                var semanticMatches = await QuerySemanticAsync(
                    db, queryVector, allowedTypes, Math.Clamp(take * 5, 25, 100), cancellationToken);
                foreach (var match in semanticMatches.Where(item => item.SemanticScore >= options.Value.SimilarityThreshold))
                {
                    if (ranked.TryGetValue(match.Document.Id, out var existing))
                    {
                        ranked[match.Document.Id] = existing with { SemanticScore = match.SemanticScore };
                    }
                    else
                    {
                        ranked[match.Document.Id] = new RankedDocument(match.Document, 0, match.SemanticScore);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException)
            {
                cache.Set(EmbeddingFailureCacheKey, true, TimeSpan.FromMinutes(1));
                logger.LogWarning(ex, "Semantic query embedding failed; returning keyword-ranked results.");
            }
        }

        return ranked.Values
            .Select(item => new SemanticSearchHit(
                item.Document.Id,
                item.Document.SourceType,
                item.Document.SourceId,
                item.Document.ParentId,
                item.Document.Title,
                Preview(item.Document.Content, normalizedQuery, terms),
                CombinedScore(item.KeywordScore, item.SemanticScore),
                item.KeywordScore,
                item.SemanticScore))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(take, 1, 100))
            .ToList();
    }

    public async Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var documents = await db.SemanticSearchDocuments.AsNoTracking()
            .Select(item => new { item.EmbeddingModel, item.IndexedAt })
            .ToListAsync(cancellationToken);
        return new SemanticIndexStatus(
            options.Value.Enabled,
            options.Value.Model,
            documents.Count,
            documents.Count == 0 ? null : documents.Max(item => item.IndexedAt),
            documents.Count(item => !string.Equals(item.EmbeddingModel, options.Value.Model, StringComparison.Ordinal)));
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled || cache.TryGetValue(EmbeddingFailureCacheKey, out _)) return;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await BuildCandidatesAsync(db, cancellationToken);
        var existing = await db.SemanticSearchDocuments.ToListAsync(cancellationToken);
        var existingBySource = existing.ToDictionary(item => (item.SourceType, item.SourceId));
        var activeKeys = candidates.Select(item => (item.SourceType, item.SourceId)).ToHashSet();
        var stale = existing.Where(item => !activeKeys.Contains((item.SourceType, item.SourceId))).ToList();
        if (stale.Count > 0) db.SemanticSearchDocuments.RemoveRange(stale);

        var changed = candidates.Where(candidate =>
            !existingBySource.TryGetValue((candidate.SourceType, candidate.SourceId), out var document)
            || !string.Equals(document.ContentHash, candidate.ContentHash, StringComparison.Ordinal)
            || !string.Equals(document.EmbeddingModel, options.Value.Model, StringComparison.Ordinal)).ToList();

        var batchSize = Math.Clamp(options.Value.BatchSize, 1, 50);
        for (var offset = 0; offset < changed.Count; offset += batchSize)
        {
            var batch = changed.Skip(offset).Take(batchSize).ToList();
            IReadOnlyList<float[]> embeddings;
            try
            {
                embeddings = await ollama.EmbedAsync(
                    options.Value.Model,
                    batch.Select(item => item.EmbeddingInput).ToList(),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or NotSupportedException)
            {
                cache.Set(EmbeddingFailureCacheKey, true, TimeSpan.FromMinutes(1));
                throw new InvalidOperationException(
                    $"Embedding model '{options.Value.Model}' is unavailable. Install it from SentinelGPT model management or configure SemanticSearch:Model.", ex);
            }

            for (var index = 0; index < batch.Count; index++)
            {
                var candidate = batch[index];
                var vector = embeddings[index];
                if (!existingBySource.TryGetValue((candidate.SourceType, candidate.SourceId), out var document))
                {
                    document = new SemanticSearchDocument
                    {
                        SourceType = candidate.SourceType,
                        SourceId = candidate.SourceId,
                        Title = candidate.Title,
                        Content = candidate.Content,
                        ContentHash = candidate.ContentHash,
                        EmbeddingModel = options.Value.Model,
                        CreatedBy = "semantic-index"
                    };
                    existingBySource[(candidate.SourceType, candidate.SourceId)] = document;
                    await db.SemanticSearchDocuments.AddAsync(document, cancellationToken);
                }

                var now = DateTimeOffset.UtcNow;
                document.ParentId = candidate.ParentId;
                document.Title = candidate.Title;
                document.Content = candidate.Content;
                document.ContentHash = candidate.ContentHash;
                document.EmbeddingModel = options.Value.Model;
                document.Dimensions = vector.Length;
                document.Embedding = ToBlob(vector);
                document.IndexedAt = now;
                document.UpdatedAt = now;
                document.UpdatedBy = "semantic-index";
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        if (changed.Count == 0 && stale.Count > 0) await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Semantic index reconciliation completed: {ActiveCount} active, {UpdatedCount} embedded, {RemovedCount} removed.",
            candidates.Count, changed.Count, stale.Count);
    }

    private async Task<List<IndexCandidate>> BuildCandidatesAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var candidates = new List<IndexCandidate>();

        var pages = await db.WikiPages.AsNoTracking()
            .Where(item => item.TrashedAt == null && item.NotionArchivedAt == null)
            .ToListAsync(cancellationToken);
        candidates.AddRange(pages.Select(page => Candidate(
            SemanticSourceTypes.WikiPage,
            page.Id,
            null,
            page.Title,
            string.Join('\n', WikiBlockJson.ParseBlocks(page.BlocksJson)
                .Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 800))))));

        var databases = await db.WikiDatabases.AsNoTracking()
            .Where(item => item.TrashedAt == null && item.NotionArchivedAt == null)
            .Include(item => item.Properties)
            .Include(item => item.Rows.Where(row => row.TrashedAt == null && row.NotionArchivedAt == null))
            .ToListAsync(cancellationToken);
        foreach (var database in databases)
        {
            var titleProperty = database.Properties.FirstOrDefault(item => item.Type == WikiDatabasePropertyTypes.Title);
            foreach (var row in database.Rows)
            {
                var values = WikiPropertyValues.ParseObject(row.PropertyValuesJson);
                var rowTitle = titleProperty is null
                    ? "Untitled"
                    : WikiPropertyValues.GetDisplayText(titleProperty, values, row.CreatedAt);
                var propertyText = string.Join(" · ", database.Properties.OrderBy(item => item.SortOrder)
                    .Select(property => $"{property.Name}: {WikiPropertyValues.GetDisplayText(property, values, row.CreatedAt)}")
                    .Where(value => !value.EndsWith(": ", StringComparison.Ordinal)));
                var blockText = string.Join('\n', WikiBlockJson.ParseBlocks(row.BlocksJson)
                    .Select(block => WikiBlockHtmlRenderer.PlainTextPreview(block, 800)));
                candidates.Add(Candidate(
                    SemanticSourceTypes.WikiDatabaseRow,
                    row.Id,
                    database.Id,
                    $"{database.Title}: {rowTitle}",
                    $"Database: {database.Title}\n{propertyText}\n{blockText}"));
            }
        }

        var activities = await db.ContactActivities.AsNoTracking().ToListAsync(cancellationToken);
        var activityByContact = activities.GroupBy(item => item.ContactId)
            .ToDictionary(group => group.Key, group => string.Join('\n', group
                .OrderByDescending(item => item.CreatedAt).Take(20).Select(item => item.Note)));
        var contacts = await db.Contacts.AsNoTracking().Where(item => item.TrashedAt == null).ToListAsync(cancellationToken);
        candidates.AddRange(contacts.Select(contact => Candidate(
            SemanticSourceTypes.CrmContact,
            contact.Id,
            null,
            contact.FullName,
            $"Name: {contact.FullName}\nCompany: {contact.Company}\nStatus: {contact.Status}\nEmail: {contact.Email}\n" +
            activityByContact.GetValueOrDefault(contact.Id, string.Empty))));

        var contactNames = contacts.ToDictionary(item => item.Id, item => item.FullName);
        var deals = await db.Deals.AsNoTracking().ToListAsync(cancellationToken);
        candidates.AddRange(deals.Select(deal => Candidate(
            SemanticSourceTypes.CrmDeal,
            deal.Id,
            deal.ContactId,
            deal.Title,
            $"Deal: {deal.Title}\nContact: {contactNames.GetValueOrDefault(deal.ContactId, "Unknown")}\n" +
            $"Stage: {deal.Stage}\nValue: {deal.ValueUsd}\nExpected close: {deal.ExpectedCloseDate:u}\nNotes: {deal.Notes}")));

        var cmsPages = await db.CmsPages.AsNoTracking().Where(item => item.TrashedAt == null).ToListAsync(cancellationToken);
        foreach (var page in cmsPages)
        {
            var layout = CmsBuilderJson.ParseLayoutOrEmpty(page.BlocksJson);
            var body = string.Join('\n', layout.Sections.SelectMany(section => section.Columns)
                .SelectMany(column => column.Widgets)
                .Select(widget => CmsBlockHtmlRenderer.PlainTextPreview(widget, 1_000)));
            candidates.Add(Candidate(
                SemanticSourceTypes.CmsPage,
                page.Id,
                page.SiteId,
                page.Title,
                $"Title: {page.Title}\nSlug: {page.Slug}\nTags: {page.Tags}\n{page.MetaDescription}\n{body}"));
        }

        return candidates;
    }

    private async Task<List<SemanticMatch>> QuerySemanticAsync(
        ApplicationDbContext db,
        float[] queryVector,
        string[] sourceTypes,
        int take,
        CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        connection.CreateFunction<byte[], byte[], double>(
            "gws_cosine_similarity",
            CosineSimilarity,
            isDeterministic: true);

        await using var command = connection.CreateCommand();
        var typeFilter = string.Empty;
        if (sourceTypes.Length > 0)
        {
            var names = new List<string>();
            for (var index = 0; index < sourceTypes.Length; index++)
            {
                var name = $"$type{index}";
                names.Add(name);
                command.Parameters.AddWithValue(name, sourceTypes[index]);
            }
            typeFilter = $" AND SourceType IN ({string.Join(',', names)})";
        }
        command.CommandText = $"""
            SELECT Id, SourceType, SourceId, ParentId, Title, Content,
                   gws_cosine_similarity(Embedding, $queryEmbedding) AS SemanticScore
            FROM SemanticSearchDocuments
            WHERE EmbeddingModel = $model AND Dimensions = $dimensions{typeFilter}
            ORDER BY SemanticScore DESC
            LIMIT $take;
            """;
        command.Parameters.Add("$queryEmbedding", SqliteType.Blob).Value = ToBlob(queryVector);
        command.Parameters.AddWithValue("$model", options.Value.Model);
        command.Parameters.AddWithValue("$dimensions", queryVector.Length);
        command.Parameters.AddWithValue("$take", take);

        var matches = new List<SemanticMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var document = new SemanticSearchDocument
            {
                Id = Guid.Parse(reader.GetString(0)),
                SourceType = reader.GetString(1),
                SourceId = Guid.Parse(reader.GetString(2)),
                ParentId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                Title = reader.GetString(4),
                Content = reader.GetString(5),
                ContentHash = string.Empty,
                EmbeddingModel = options.Value.Model
            };
            matches.Add(new SemanticMatch(document, reader.GetDouble(6)));
        }
        return matches;
    }

    private static IndexCandidate Candidate(string sourceType, Guid sourceId, Guid? parentId, string title, string content)
    {
        var normalizedTitle = Normalize(title, 300);
        var normalizedContent = Normalize(content, MaximumIndexedCharacters);
        var input = $"{normalizedTitle}\n{normalizedContent}".Trim();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        return new IndexCandidate(sourceType, sourceId, parentId, normalizedTitle, normalizedContent, input, hash);
    }

    private static string[] NormalizeTypes(IReadOnlyCollection<string>? sourceTypes) => sourceTypes is null
        ? []
        : sourceTypes.Where(SemanticSourceTypes.All.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static double ScoreKeyword(string title, string content, string query, IReadOnlyList<string> terms)
    {
        if (title.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase)) return 0.9;
        if (content.Contains(query, StringComparison.OrdinalIgnoreCase)) return 0.78;
        if (terms.Count == 0) return 0;
        var searchable = $"{title} {content}";
        var matches = terms.Count(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (matches == 0) return 0;
        var coverage = matches / (double)terms.Count;
        return 0.35 + (0.35 * coverage);
    }

    private static double CombinedScore(double keyword, double semantic) => keyword > 0 && semantic > 0
        ? Math.Min(1, (keyword * 0.58) + (semantic * 0.42) + 0.08)
        : Math.Max(keyword, semantic * 0.92);

    private static string Preview(string content, string query, IReadOnlyList<string> terms)
    {
        var position = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (position < 0)
        {
            position = terms.Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
                .Where(index => index >= 0).DefaultIfEmpty(0).Min();
        }
        var start = Math.Max(0, position - 60);
        var length = Math.Min(220, content.Length - start);
        var value = content.Substring(start, length).Trim();
        return $"{(start > 0 ? "…" : string.Empty)}{value}{(start + length < content.Length ? "…" : string.Empty)}";
    }

    private static string Normalize(string? value, int maxLength)
    {
        var normalized = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static byte[] ToBlob(IReadOnlyList<float> vector)
    {
        var bytes = new byte[vector.Count * sizeof(float)];
        for (var index = 0; index < vector.Count; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float)), vector[index]);
        }
        return bytes;
    }

    private static double CosineSimilarity(byte[] left, byte[] right)
    {
        if (left.Length == 0 || left.Length != right.Length || left.Length % sizeof(float) != 0) return -1;
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var offset = 0; offset < left.Length; offset += sizeof(float))
        {
            var a = BinaryPrimitives.ReadSingleLittleEndian(left.AsSpan(offset, sizeof(float)));
            var b = BinaryPrimitives.ReadSingleLittleEndian(right.AsSpan(offset, sizeof(float)));
            if (!float.IsFinite(a) || !float.IsFinite(b)) return -1;
            dot += a * b;
            leftMagnitude += a * a;
            rightMagnitude += b * b;
        }
        if (leftMagnitude <= 0 || rightMagnitude <= 0) return -1;
        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    private sealed record IndexCandidate(
        string SourceType,
        Guid SourceId,
        Guid? ParentId,
        string Title,
        string Content,
        string EmbeddingInput,
        string ContentHash);

    private sealed record SemanticMatch(SemanticSearchDocument Document, double SemanticScore);
    private sealed record RankedDocument(SemanticSearchDocument Document, double KeywordScore, double SemanticScore);
}
