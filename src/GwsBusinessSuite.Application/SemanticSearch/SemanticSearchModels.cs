namespace GwsBusinessSuite.Application.SemanticSearch;

public static class SemanticSourceTypes
{
    public const string WikiPage = "wiki-page";
    public const string WikiDatabaseRow = "wiki-database-row";
    public const string CrmContact = "crm-contact";
    public const string CrmDeal = "crm-deal";
    public const string CmsPage = "cms-page";

    public static readonly string[] All = [WikiPage, WikiDatabaseRow, CrmContact, CrmDeal, CmsPage];
}

public sealed class SemanticSearchOptions
{
    public const string SectionName = "SemanticSearch";
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "embeddinggemma";
    public int BatchSize { get; set; } = 12;
    public int ReconciliationMinutes { get; set; } = 10;
    public double SimilarityThreshold { get; set; } = 0.45;
}

public sealed record SemanticSearchHit(
    Guid DocumentId,
    string SourceType,
    Guid SourceId,
    Guid? ParentId,
    string Title,
    string Preview,
    double Score,
    double KeywordScore,
    double SemanticScore);

public sealed record SemanticIndexStatus(
    bool Enabled,
    string Model,
    int DocumentCount,
    DateTimeOffset? LastIndexedAt,
    int PendingDocumentCount);

public interface IHybridSearchService
{
    Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string query,
        IReadOnlyCollection<string>? sourceTypes = null,
        int take = 20,
        CancellationToken cancellationToken = default);

    Task<SemanticIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task RebuildAsync(CancellationToken cancellationToken = default);
}
