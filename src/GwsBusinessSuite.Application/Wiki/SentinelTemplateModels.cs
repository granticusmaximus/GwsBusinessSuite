using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.Wiki;

public sealed record SentinelPageTemplateView(
    Guid Id,
    string Name,
    string PageTitle,
    string? Icon,
    int BlockCount,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record SentinelDatabaseTemplateView(
    Guid Id,
    string Name,
    string DatabaseTitle,
    string? Icon,
    int PropertyCount,
    int RowCount,
    int ViewCount,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public sealed record SentinelBlockTemplateView(
    Guid Id,
    string Name,
    int BlockCount,
    string Preview,
    DateTimeOffset CreatedAt,
    string CreatedBy);

public interface ISentinelTemplateService
{
    Task<IReadOnlyList<SentinelPageTemplateView>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<SentinelPageTemplateView> CreateFromPageAsync(
        Guid wikiPageId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task<WikiPage> CreatePageAsync(
        Guid templateId,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelBlockTemplateView>> ListBlockTemplatesAsync(
        CancellationToken cancellationToken = default);

    Task<SentinelBlockTemplateView> CreateBlockTemplateAsync(
        string name,
        string blocksJson,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task<string> MaterializeBlockTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);

    Task DeleteBlockTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SentinelDatabaseTemplateView>> ListDatabaseTemplatesAsync(
        CancellationToken cancellationToken = default);

    Task<SentinelDatabaseTemplateView> CreateFromDatabaseAsync(
        Guid wikiDatabaseId,
        string name,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task<WikiDatabase> CreateDatabaseAsync(
        Guid templateId,
        Guid? parentWikiPageId,
        string performedBy,
        CancellationToken cancellationToken = default);

    Task DeleteDatabaseTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);
}
