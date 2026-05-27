using GwsBusinessSuite.Domain.Entities;

namespace GwsBusinessSuite.Application.AppRegistry;

public interface IAppRegistryService
{
    Task<IReadOnlyList<BusinessApp>> ListAppsAsync(CancellationToken cancellationToken = default);
    Task<BusinessApp?> GetAppAsync(Guid appId, CancellationToken cancellationToken = default);
    Task<BusinessApp> SaveAppAsync(AppRegistryEditorModel editor, CancellationToken cancellationToken = default);
    Task DeleteAppAsync(Guid appId, CancellationToken cancellationToken = default);
}
