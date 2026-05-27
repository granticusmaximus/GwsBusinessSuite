namespace GwsBusinessSuite.Application.CmsBuilder;

public interface IReactPageBuilderService
{
    Task<IReadOnlyList<ReactPageReference>> ListReactPagesAsync(CancellationToken cancellationToken = default);
    Task<ReactPageEditorState?> LoadEditorStateAsync(string pageKey, CancellationToken cancellationToken = default);
    Task<ReactPageSaveResult> SaveAsync(ReactPageSaveRequest request, CancellationToken cancellationToken = default);
    Task<ReactPublishStatus> GetPublishStatusAsync(CancellationToken cancellationToken = default);
    Task<ReactPublishResult> PublishAsync(ReactPublishRequest request, CancellationToken cancellationToken = default);
}
