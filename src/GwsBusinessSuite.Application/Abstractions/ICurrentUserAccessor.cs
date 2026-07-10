namespace GwsBusinessSuite.Application.Abstractions;

public interface ICurrentUserAccessor
{
    Task<string> GetCurrentUsernameAsync(CancellationToken cancellationToken = default);
}

public sealed class FixedCurrentUserAccessor(string username) : ICurrentUserAccessor
{
    public static ICurrentUserAccessor Unknown { get; } = new FixedCurrentUserAccessor("unknown");

    public Task<string> GetCurrentUsernameAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(username) ? "unknown" : username);
}
