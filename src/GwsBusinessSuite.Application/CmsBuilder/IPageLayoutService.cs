namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IPageLayoutService
{
    Task<PageLayout> LoadLayoutAsync(string pageKey, CancellationToken cancellationToken = default);
    Task SaveLayoutAsync(PageLayout layout, CancellationToken cancellationToken = default);
}
