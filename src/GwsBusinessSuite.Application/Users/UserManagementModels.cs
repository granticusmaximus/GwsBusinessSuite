using System.ComponentModel.DataAnnotations;

namespace GwsBusinessSuite.Application.Users;

public sealed class UserView
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed class CreateUserInput
{
    [Required(ErrorMessage = "Username is required.")]
    public string Username { get; init; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;
}

public sealed record UserManagementResult(bool Succeeded, string? FailureReason = null)
{
    public static UserManagementResult Success() => new(true);
    public static UserManagementResult Failure(string reason) => new(false, reason);
}
