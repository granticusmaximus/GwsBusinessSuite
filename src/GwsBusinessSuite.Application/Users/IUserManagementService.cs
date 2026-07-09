namespace GwsBusinessSuite.Application.Users;

public interface IUserManagementService
{
    Task<IReadOnlyList<UserView>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<UserManagementResult> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default);

    Task<UserManagementResult> ChangeRoleAsync(Guid userId, string newRole, CancellationToken cancellationToken = default);

    Task<UserManagementResult> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

    Task<UserManagementResult> ToggleActiveAsync(Guid userId, CancellationToken cancellationToken = default);
}
