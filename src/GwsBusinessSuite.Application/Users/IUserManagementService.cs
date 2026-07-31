namespace GwsBusinessSuite.Application.Users;

public interface IUserManagementService
{
    Task<IReadOnlyList<UserView>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<UserManagementResult> CreateUserAsync(CreateUserInput input, CancellationToken cancellationToken = default);

    Task<UserManagementResult> ChangeRoleAsync(Guid userId, string newRole, CancellationToken cancellationToken = default);

    Task<UserManagementResult> ResetPasswordAsync(Guid userId, string newPassword, CancellationToken cancellationToken = default);

    Task<UserManagementResult> ToggleActiveAsync(Guid userId, CancellationToken cancellationToken = default);

    // Verifies credentials with per-account lockout tracking (see LoginLockoutPolicy) -
    // the sole entry point /auth/login uses, so the endpoint itself never touches
    // AppUser/PasswordHash directly.
    Task<LoginAttemptResult> AttemptLoginAsync(string username, string password, CancellationToken cancellationToken = default);

    Task<MfaEnrollment?> PrepareMfaEnrollmentAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<MfaEnrollmentResult> CompleteMfaEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    Task<MfaVerificationResult> VerifyMfaAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    // Admin override to clear a lockout (and reset the failed-attempt counter) without
    // waiting for it to expire, independent of resetting the password.
    Task<UserManagementResult> UnlockUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
