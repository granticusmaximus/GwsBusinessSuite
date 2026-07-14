namespace GwsBusinessSuite.Application.AppGeneration;

public interface IAppGenerationService
{
    Task<IReadOnlyList<AppGenerationRequestView>> ListRequestsAsync(string? status = null, CancellationToken cancellationToken = default);
    Task<AppGenerationRequestView?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<AppGenerationChatResult> StartAsync(StartAppGenerationInput input, CancellationToken cancellationToken = default);
    Task<AppGenerationChatResult> SendMessageAsync(Guid requestId, string message, CancellationToken cancellationToken = default);
    Task<AppGenerationChatResult> SubmitForApprovalAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<AppGenerationChatResult> ApproveAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<AppGenerationChatResult> RejectAsync(Guid requestId, string reason, CancellationToken cancellationToken = default);
    Task<int> CountPendingApprovalAsync(CancellationToken cancellationToken = default);
}
